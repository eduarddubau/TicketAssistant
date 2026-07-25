namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// Whether the caller wants the loop's inner workings streamed alongside the normal chat
/// events. The console's debug console asks for them with an <c>X-Debug: 1</c> header on the
/// message/confirm calls, exactly like the LLM switchers ride on their own headers.
///
/// It's opt-in per request because the trace is heavy: every turn carries the whole
/// conversation, the tool menu and the raw model reply, which is the point — but only for
/// someone who is watching.
/// </summary>
public sealed class DebugTrace(IHttpContextAccessor accessor)
{
    public const string EnabledHeader = "X-Debug";

    /// <summary>True when this request asked for the trace (any value but "0"/"false").</summary>
    public bool Enabled
    {
        get
        {
            var value = accessor.HttpContext?.Request.Headers[EnabledHeader].ToString();
            return !string.IsNullOrWhiteSpace(value)
                && !value.Equals("0", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
    }
}
