// Application entry point. This file has two halves:
//   1. Service registration (dependency injection) — wire up the LLM client, the ticket
//      backend, and the orchestration pieces.
//   2. The HTTP pipeline — middleware and the three chat endpoints.
// .NET "top-level statements": the code below runs directly, no Main method needed.
using System.Text.Json;
using Microsoft.Extensions.AI;
using Scalar.AspNetCore;
using TicketAssistant.Api.Orchestration;
using TicketAssistant.Api.Providers;

var builder = WebApplication.CreateBuilder(args);

// Allow the Angular dev server (future frontend) to call this API from another origin.
const string AngularDevCorsPolicy = "AngularDev";
builder.Services.AddCors(options => options.AddPolicy(AngularDevCorsPolicy, policy =>
    policy.WithOrigins("http://localhost:4200").AllowAnyMethod().AllowAnyHeader()));

builder.Services.AddOpenApi();                              // OpenAPI doc for the Scalar UI
builder.Services.AddHttpContextAccessor();                  // lets the handler below read the request
builder.Services.AddTransient<UserIdForwardingHandler>();   // forwards X-User-Id to the ticket backend

// Tickets:Backend switches the ITicketProvider implementation. "Http" (default) calls an
// external ticketing system over REST (the TicketingMock.Api service in this repo);
// "InMemory" uses the in-process InMemoryTicketProvider stub for offline runs.
var ticketsBackend = builder.Configuration["Tickets:Backend"] ?? "Http";
if (ticketsBackend.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<ITicketProvider, InMemoryTicketProvider>();
}
else
{
    var ticketsBaseUrl = builder.Configuration["Tickets:Http:BaseUrl"] ?? "http://localhost:5090";
    builder.Services.AddHttpClient<ITicketProvider, HttpTicketProvider>(
            c => c.BaseAddress = new Uri(ticketsBaseUrl))
        .AddHttpMessageHandler<UserIdForwardingHandler>();
}

// The chat client is created per request by ChatClientFactory, so the provider/model can be
// switched with the X-Llm-Provider / X-Llm-Model headers instead of only via configuration.
// Deliberately no .UseFunctionInvocation() — OrchestrationLoop drives the tool-call loop by
// hand so it can intercept writes before they run.
builder.Services.AddSingleton<ChatClientFactory>();

// Build the tool menu from whichever provider was registered above, then the loop (which
// takes the chat client + tools + provider) and the conversation memory. All singletons:
// one shared instance for the app's lifetime.
builder.Services.AddSingleton<UndoStore>();
builder.Services.AddSingleton(sp =>
    TicketTools.Build(sp.GetRequiredService<ITicketProvider>(), sp.GetRequiredService<UndoStore>()));
builder.Services.AddSingleton<OrchestrationLoop>();
builder.Services.AddSingleton<ConversationStore>();

var app = builder.Build();

// ----- HTTP pipeline -----
app.UseCors(AngularDevCorsPolicy);

// Serves wwwroot/index.html at "/" — a minimal SSE chat client for testing the API
// in a browser without standing up the Angular app. Same-origin, so no CORS involved.
app.UseDefaultFiles();
app.UseStaticFiles();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(); // http://localhost:<port>/scalar/v1
}

// Where a browser can reach the ticket board. Distinct from Tickets:Http:BaseUrl, which is
// how *this service* reaches the backend (a container hostname that means nothing to a browser).
var boardUrl = builder.Configuration["Tickets:BoardUrl"] ?? "http://localhost:5090";

// Start a new chat. Returns its id, the assistant's greeting, and the board URL so the client
// can turn ticket ids in replies into links.
app.MapPost("/api/conversations", (ConversationStore store) =>
    Results.Ok(new { conversationId = store.Create(), greeting = ConversationStore.Greeting, boardUrl }));

// Which LLM providers exist and which one this request would use, so the console can offer a
// switcher. Callers override per request with the X-Llm-Provider / X-Llm-Model headers.
app.MapGet("/api/llm", (ChatClientFactory chatClients) =>
{
    var (provider, model) = chatClients.Current();
    return Results.Ok(new
    {
        providers = ChatClientFactory.KnownProviders,
        defaultModels = ChatClientFactory.KnownProviders.ToDictionary(p => p, chatClients.DefaultModelFor),
        provider,
        model
    });
});

// Send a user message. Appends it to the conversation, runs the loop, and streams the
// resulting events (assistant text / tools ran / a confirmation request) back as SSE.
app.MapPost("/api/conversations/{id:guid}/messages", async (
    Guid id,
    ChatRequest request,
    ConversationStore store,
    OrchestrationLoop loop,
    HttpContext http,
    CancellationToken ct) =>
{
    var messages = store.Get(id);
    messages.Add(new ChatMessage(ChatRole.User, request.Text));

    await WriteSseAsync(http.Response, loop.RunAsync(messages, ct), ct);
});

// Answer to a confirmation card: approve (optionally with edits) or decline the paused
// write, then resume the loop and stream what happens next.
app.MapPost("/api/conversations/{id:guid}/confirm", async (
    Guid id,
    ConfirmRequest request,
    ConversationStore store,
    OrchestrationLoop loop,
    HttpContext http,
    CancellationToken ct) =>
{
    var messages = store.Get(id);

    await WriteSseAsync(
        http.Response,
        loop.ResumeAfterConfirmationAsync(
            messages, request.CallId, request.Approved, request.Approved ? request.Edits : null, ct),
        ct);
});

app.Run();

// Streams the loop's events to the browser as Server-Sent Events: each event is turned into
// a small JSON object and written as a "data: {...}" frame, flushed immediately so the UI
// updates live instead of waiting for the whole turn to finish.
static async Task WriteSseAsync(
    HttpResponse response,
    IAsyncEnumerable<OrchestrationEvent> events,
    CancellationToken ct)
{
    response.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";

    await foreach (var evt in events.WithCancellation(ct))
    {
        // Translate the internal event record into the JSON shape the browser expects.
        object payload = evt switch
        {
            OrchestrationEvent.AssistantText e => new { type = "assistant_text", text = e.Text },
            OrchestrationEvent.AssistantTextDelta e => new { type = "assistant_delta", text = e.Text },
            OrchestrationEvent.AssistantReplace e => new { type = "assistant_replace", text = e.Text },
            OrchestrationEvent.ToolExecuted e => new { type = "tool_executed", toolName = e.ToolName, succeeded = e.Succeeded },
            OrchestrationEvent.ConfirmationRequired e => new
            {
                type = "confirmation_required",
                callId = e.CallId,
                toolName = e.ToolName,
                arguments = e.Arguments
            },
            _ => throw new InvalidOperationException($"Unhandled event type '{evt.GetType().Name}'.")
        };

        await response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}

// Request body for POST .../messages: the user's chat text.
internal sealed record ChatRequest(string Text);

// Edits holds the (optionally user-modified) tool arguments keyed by parameter name —
// e.g. { title, description, priority } for create_ticket, { status } for a status change.
internal sealed record ConfirmRequest(
    string CallId, bool Approved, Dictionary<string, object?>? Edits);
