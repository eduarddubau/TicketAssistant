namespace TicketAssistant.Api.Providers;

/// <summary>
/// Per-site behaviour for <see cref="JiraTicketProvider"/>, bound from <c>Tickets:Jira:*</c>
/// (env vars <c>Tickets__Jira__*</c>). Authentication is not here — that comes per user from the
/// OAuth flow (see <c>Auth/</c>); this is only the project to work in and how to shape tickets.
/// </summary>
public sealed class JiraOptions
{
    /// <summary>Project key new tickets land in and every read is scoped to, e.g. <c>SUP</c>.</summary>
    public string ProjectKey { get; init; } = "";

    /// <summary>
    /// Issue type used when a create doesn't name one ("file a ticket for…" with no kind given).
    /// "Task" exists in most projects; "Bug"/"Story" also common. A create that *does* name a kind
    /// wins over this — see <see cref="Models.CreateTicketRequest.Type"/>.
    /// </summary>
    public string IssueType { get; init; } = "Task";

    /// <summary>
    /// When true, reads add <c>reporter = currentUser() OR assignee = currentUser()</c>, so a user
    /// sees everything they raised *or* had put on their plate — genuinely per-user, since each
    /// session acts as its own logged-in Jira account. Set false to browse everything the account
    /// can see.
    /// </summary>
    public bool ScopeToCurrentUser { get; init; } = true;
}
