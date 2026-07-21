using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.AI;
using Scalar.AspNetCore;
using TicketAssistant.Api.Orchestration;
using TicketAssistant.Api.Providers;

var builder = WebApplication.CreateBuilder(args);

const string AngularDevCorsPolicy = "AngularDev";
builder.Services.AddCors(options => options.AddPolicy(AngularDevCorsPolicy, policy =>
    policy.WithOrigins("http://localhost:4200").AllowAnyMethod().AllowAnyHeader()));

builder.Services.AddOpenApi();

// One provider today; register a sibling ITicketProvider (Zendesk, ...) and resolve by
// name once there's more than one to choose between.
builder.Services.AddSingleton<ITicketProvider, JiraTicketProvider>();

builder.Services.AddSingleton<IChatClient>(sp =>
{
    // AnthropicClient() reads ANTHROPIC_API_KEY from the environment.
    var anthropic = new AnthropicClient();
    var model = sp.GetRequiredService<IConfiguration>()["Anthropic:Model"] ?? "claude-sonnet-5";

    // Deliberately not .UseFunctionInvocation() here — OrchestrationLoop drives the
    // tool-call loop by hand so it can intercept create_ticket before it runs.
    return anthropic.AsIChatClient(model);
});

builder.Services.AddSingleton(sp => TicketTools.Build(sp.GetRequiredService<ITicketProvider>()));
builder.Services.AddSingleton<OrchestrationLoop>();
builder.Services.AddSingleton<ConversationStore>();

var app = builder.Build();

app.UseCors(AngularDevCorsPolicy);

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
