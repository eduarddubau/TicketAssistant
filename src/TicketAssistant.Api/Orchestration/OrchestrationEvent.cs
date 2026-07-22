namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// One thing that happened while the orchestration loop was running. The loop produces a
/// stream of these as it works, and the web endpoint turns each one into a Server-Sent
/// Event the browser can react to (show text, show a tool ran, show a confirmation card).
///
/// This is an "abstract record" with nested records — a discriminated union: an
/// OrchestrationEvent is always exactly one of the three concrete kinds below, and calling
/// code uses a `switch` on the type to handle each.
/// </summary>
public abstract record OrchestrationEvent
{
    /// <summary>The model produced a normal chat reply for the user to read.</summary>
    public sealed record AssistantText(string Text) : OrchestrationEvent;

    /// <summary>
    /// A fragment of the reply as the model generates it. The UI appends consecutive deltas
    /// into one growing message, so text appears immediately instead of after the whole
    /// response is complete. Any other event ends the current message.
    /// </summary>
    public sealed record AssistantTextDelta(string Text) : OrchestrationEvent;

    /// <summary>
    /// Replace whatever has been streamed for the current reply with this text. Used when
    /// the loop discovers, only after streaming, that the reply contained junk (e.g. a tool
    /// call written out as JSON mid-sentence) — the UI rewrites the in-progress bubble
    /// instead of leaving the junk on screen.
    /// </summary>
    public sealed record AssistantReplace(string Text) : OrchestrationEvent;

    /// <summary>
    /// A tool finished running (e.g. search_tickets). Carries which tool and whether it
    /// succeeded, so the UI can show a small status line like "🔧 search_tickets ✓".
    /// </summary>
    public sealed record ToolExecuted(string ToolName, bool Succeeded) : OrchestrationEvent;

    /// <summary>
    /// The model wants to run a write tool (create/update/comment). Instead of running it,
    /// the loop stops and raises this so the caller can show a confirmation card. Once the
    /// user approves or declines, the caller calls
    /// <see cref="OrchestrationLoop.ResumeAfterConfirmationAsync"/> to continue.
    /// </summary>
    /// <param name="CallId">The model's id for this specific tool call; echoed back on resume.</param>
    /// <param name="ToolName">Which write tool was requested (decides which dialog to show).</param>
    /// <param name="Arguments">The values the model proposed (pre-filled into the dialog, editable).</param>
    public sealed record ConfirmationRequired(
        string CallId,
        string ToolName,
        IDictionary<string, object?> Arguments) : OrchestrationEvent;
}
