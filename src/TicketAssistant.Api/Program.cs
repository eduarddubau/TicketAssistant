// Application entry point. This file has two halves:
//   1. Service registration (dependency injection) — wire up the LLM client, the ticket
//      backend, and the orchestration pieces.
//   2. The HTTP pipeline — middleware and the three chat endpoints.
// .NET "top-level statements": the code below runs directly, no Main method needed.
using System.ClientModel;
using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;
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

// Llm:Provider selects the chat backend behind the single IChatClient abstraction:
// "Ollama" (default, local, no key), "Anthropic", "OpenAI", or "Google". Google is reached
// through its OpenAI-compatible endpoint, so it reuses the OpenAI client. Deliberately not
// .UseFunctionInvocation() — OrchestrationLoop drives the tool-call loop by hand so it can
// intercept create_ticket before it runs.
builder.Services.AddSingleton<IChatClient>(sp =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var provider = (cfg["Llm:Provider"] ?? "Ollama").ToLowerInvariant();

    static string RequireKey(IConfiguration cfg, string key) =>
        cfg[key] ?? throw new InvalidOperationException($"{key} is required for the selected Llm:Provider.");

    switch (provider)
    {
        case "anthropic":
        {
            var apiKey = cfg["Anthropic:ApiKey"];
            var client = string.IsNullOrEmpty(apiKey) ? new AnthropicClient() : new AnthropicClient { ApiKey = apiKey };
            return client.AsIChatClient(cfg["Anthropic:Model"] ?? "claude-sonnet-5");
        }
        case "openai":
        {
            return new OpenAIClient(new ApiKeyCredential(RequireKey(cfg, "OpenAI:ApiKey")))
                .GetChatClient(cfg["OpenAI:Model"] ?? "gpt-4o-mini")
                .AsIChatClient();
        }
        case "google":
        {
            var endpoint = cfg["Google:BaseUrl"] ?? "https://generativelanguage.googleapis.com/v1beta/openai/";
            return new OpenAIClient(
                    new ApiKeyCredential(RequireKey(cfg, "Google:ApiKey")),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .GetChatClient(cfg["Google:Model"] ?? "gemini-2.0-flash")
                .AsIChatClient();
        }
        default:
        {
            var baseUrl = cfg["Ollama:BaseUrl"] ?? "http://localhost:11434";
            return new OllamaApiClient(new Uri(baseUrl), cfg["Ollama:Model"] ?? "llama3.2:3b");
        }
    }
});

// Build the tool menu from whichever provider was registered above, then the loop (which
// takes the chat client + tools + provider) and the conversation memory. All singletons:
// one shared instance for the app's lifetime.
builder.Services.AddSingleton(sp => TicketTools.Build(sp.GetRequiredService<ITicketProvider>()));
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

// Start a new chat; returns its id for the client to use on the calls below.
app.MapPost("/api/conversations", (ConversationStore store) =>
    Results.Ok(new { conversationId = store.Create() }));

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
