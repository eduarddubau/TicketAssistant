using System.Text.Json;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Scalar.AspNetCore;
using TicketAssistant.Api.Orchestration;
using TicketAssistant.Api.Providers;

var builder = WebApplication.CreateBuilder(args);

const string AngularDevCorsPolicy = "AngularDev";
builder.Services.AddCors(options => options.AddPolicy(AngularDevCorsPolicy, policy =>
    policy.WithOrigins("http://localhost:4200").AllowAnyMethod().AllowAnyHeader()));

builder.Services.AddOpenApi();

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
        c => c.BaseAddress = new Uri(ticketsBaseUrl));
}

// A local Ollama model provides chat + tool calling. Deliberately not
// .UseFunctionInvocation() — OrchestrationLoop drives the tool-call loop by hand so it
// can intercept create_ticket before it runs.
builder.Services.AddSingleton<IChatClient>(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var baseUrl = configuration["Ollama:BaseUrl"] ?? "http://localhost:11434";
    var model = configuration["Ollama:Model"] ?? "llama3.2:3b";
    return new OllamaApiClient(new Uri(baseUrl), model);
});

builder.Services.AddSingleton(sp => TicketTools.Build(sp.GetRequiredService<ITicketProvider>()));
builder.Services.AddSingleton<OrchestrationLoop>();
builder.Services.AddSingleton<ConversationStore>();

var app = builder.Build();

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

app.MapPost("/api/conversations", (ConversationStore store) =>
    Results.Ok(new { conversationId = store.Create() }));

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
        loop.ResumeAfterConfirmationAsync(messages, request.CallId, request.Approved, ct),
        ct);
});

app.Run();

static async Task WriteSseAsync(
    HttpResponse response,
    IAsyncEnumerable<OrchestrationEvent> events,
    CancellationToken ct)
{
    response.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";

    await foreach (var evt in events.WithCancellation(ct))
    {
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

internal sealed record ChatRequest(string Text);

internal sealed record ConfirmRequest(string CallId, bool Approved);
