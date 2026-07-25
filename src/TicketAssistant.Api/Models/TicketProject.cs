namespace TicketAssistant.Api.Models;

/// <summary>
/// A project a ticket can belong to. The three identifying parts are kept separate rather than
/// blended into one label, because a user picking where a ticket goes needs to see each of them:
/// which <paramref name="Provider"/> (backend), which <paramref name="SiteName"/> (workspace —
/// only some providers have them; Jira Cloud calls them sites), and which project.
/// </summary>
/// <param name="Key">Project key, e.g. "SUP" — also the prefix of its ticket ids.</param>
/// <param name="Name">Human name, e.g. "Support".</param>
/// <param name="Provider">Backend that owns it: "jira", "mock-ticketing", "in-memory".</param>
/// <param name="SiteName">Workspace/site name, when the provider has that concept; else null.</param>
/// <param name="SiteUrl">That site's browser URL, used to build ticket links.</param>
/// <param name="ItemTypes">
/// The kinds of item this project accepts — "Task", "Bug", "Story"… — in the backend's own words.
/// Per project, not per backend: a Jira project only accepts the issue types its scheme defines,
/// so this is what lets the assistant (and the create card) offer "a task" or "a bug" without
/// guessing at names the project would reject.
/// </param>
public sealed record TicketProject(
    string Key,
    string Name,
    string Provider,
    string? SiteName = null,
    string? SiteUrl = null,
    IReadOnlyList<string>? ItemTypes = null);
