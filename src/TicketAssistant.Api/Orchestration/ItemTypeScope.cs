using TicketAssistant.Api.Models;

namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// The kinds of item the user has narrowed the console to — the header's kind toggles — read per
/// request from <c>X-Item-Types</c> (a comma-separated list; absent or empty means no filter). Same
/// per-request-header trick as the LLM switchers and <c>X-Debug</c>: a singleton that resolves the
/// current request rather than state threaded through the loop.
///
/// It narrows the two fanned-out reads (list/search) and nothing else. Fetching a ticket by id still
/// works for any kind — hiding an item the user just named would be baffling — and the duplicate
/// check still looks at everything, because a task covering the same issue is a duplicate whether
/// or not tasks are currently on screen.
///
/// The filter is enforced here rather than asked of the model: a filter a 3B model can forget isn't
/// a filter. What the model *is* told (see TicketTools) is that one is on, so it can say so instead
/// of reporting that nothing exists.
/// </summary>
public sealed class ItemTypeScope(IHttpContextAccessor accessor)
{
    public const string Header = "X-Item-Types";

    /// <summary>The kinds this request is limited to, as the browser named them. Empty = everything.</summary>
    public IReadOnlyList<string> Selected =>
        accessor.HttpContext?.Request.Headers[Header].ToString() is { Length: > 0 } raw
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    /// <summary>Whether a filter is on at all.</summary>
    public bool Active => Selected.Count > 0;

    /// <summary>Whether an item of this kind may be listed. Everything passes when no filter is on.</summary>
    public bool Allows(string? type) =>
        Selected.Count == 0 || Selected.Any(selected => ItemTypes.Matches(type, selected));

    /// <summary>The filter in words, for the note the model is handed ("Task, Bug").</summary>
    public string Description => string.Join(", ", Selected);
}
