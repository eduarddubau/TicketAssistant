using TicketAssistant.Api.Models;

namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// Which systems the user has narrowed the console to — the header's source toggles — read per
/// request from <c>X-Sources</c> (comma-separated provider names: <c>jira</c>,
/// <c>mock-ticketing</c>, <c>in-memory</c>; absent or empty means all of them).
///
/// The sibling of <see cref="ItemTypeScope"/>, and for the same reason: with several backends
/// answering at once, "just my real Jira work, not the demo board" is a question about the reply,
/// not about the model's judgement, so it is enforced here rather than asked for in a prompt. It
/// narrows the two fanned-out reads only — a ticket fetched by id still resolves wherever it lives,
/// and the duplicate check still sees everything.
/// </summary>
public sealed class SourceScope(IHttpContextAccessor accessor)
{
    public const string Header = "X-Sources";

    /// <summary>The provider names this request is limited to. Empty = every connected backend.</summary>
    public IReadOnlyList<string> Selected =>
        accessor.HttpContext?.Request.Headers[Header].ToString() is { Length: > 0 } raw
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

    public bool Active => Selected.Count > 0;

    /// <summary>Whether a ticket from this backend may be listed. Everything passes when no filter is on.</summary>
    public bool Allows(string providerName) =>
        Selected.Count == 0
        || Selected.Any(selected => string.Equals(selected, providerName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The filter in the same plain English the answers use ("Jira, the mock board").</summary>
    public string Description => string.Join(", ", Selected.Select(CanonicalTicket.SourceFor));
}
