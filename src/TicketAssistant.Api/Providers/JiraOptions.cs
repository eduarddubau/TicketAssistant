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

    /// <summary>Issue type created tickets use. "Task" exists in most projects; "Bug"/"Story" also common.</summary>
    public string IssueType { get; init; } = "Task";

    /// <summary>
    /// When true, reads add <c>reporter = currentUser()</c> so a user only sees tickets they
    /// raised — now genuinely per-user, since each session acts as its own logged-in Jira account.
    /// Set false to browse the whole project.
    /// </summary>
    public bool ScopeToReporter { get; init; } = true;
}
