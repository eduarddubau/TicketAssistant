namespace TicketAssistant.Api.Models;

public sealed class CreateTicketRequest
{
    public required string Title { get; init; }
    public string? Description { get; init; }

    /// <summary>Which project to create the ticket in (backends with projects, e.g. Jira). Null =
    /// use the backend's default. The key, e.g. "SUP".</summary>
    public string? Project { get; init; }

    /// <summary>
    /// What kind of item to create — "Task", "Bug", "Story", "Ticket" — as named by the backend
    /// (see <see cref="TicketProject.ItemTypes"/> for a project's options). Null = the backend's
    /// configured default, so a caller that doesn't care doesn't have to choose.
    /// </summary>
    public string? Type { get; init; }

    public TicketPriority Priority { get; init; } = TicketPriority.Medium;
    public string? Assignee { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Ids of existing tickets covering a similar issue, so near-duplicates stay linked.</summary>
    public IReadOnlyList<string> RelatedTo { get; init; } = [];
}
