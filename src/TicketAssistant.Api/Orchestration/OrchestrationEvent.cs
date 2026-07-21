namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// What the caller (an SSE endpoint, eventually) needs to react to as the loop runs.
/// </summary>
public abstract record OrchestrationEvent
{
    public sealed record AssistantText(string Text) : OrchestrationEvent;

    public sealed record ToolExecuted(string ToolName, bool Succeeded) : OrchestrationEvent;

    /// <summary>
    /// Raised instead of executing create_ticket. The loop stops here; the caller
    /// must render a confirmation card and call OrchestrationLoop.ResumeAfterConfirmationAsync
    /// with the user's decision to continue.
    /// </summary>
    public sealed record ConfirmationRequired(
        string CallId,
        string ToolName,
        IDictionary<string, object?> Arguments) : OrchestrationEvent;
}
