namespace TicketingMock.Api;

/// <summary>
/// A ticket as this mock ticketing system stores it. Status/priority are plain strings
/// (as a real external system's API would expose them) rather than the assistant's enums;
/// the assistant's HttpTicketProvider is responsible for mapping between the two.
/// </summary>
public sealed class MockTicket
{
    public required string Id { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }

    /// <summary>
    /// What kind of item this is — "Ticket" or "Task" on this board (a real system would have its
    /// own set, e.g. Jira's issue types). Kept as a string for the same reason status is: it's
    /// the external system's vocabulary, and the assistant maps it.
    /// </summary>
    public string Type { get; set; } = "Ticket";

    public string Status { get; set; } = "Open";
    public string Priority { get; set; } = "Medium";
    public string? Assignee { get; set; }
    public string? Reporter { get; set; }

    /// <summary>Identifier of the user who created the ticket; reads are scoped to this.</summary>
    public string? Owner { get; set; }

    /// <summary>Ids of tickets covering a similar issue, recorded when a near-duplicate is created anyway.</summary>
    public List<string> RelatedTo { get; set; } = [];
    public List<string> Labels { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public List<MockComment> Comments { get; set; } = [];

    /// <summary>What has happened to this ticket, oldest first — created, status changes, etc.</summary>
    public List<MockEvent> History { get; set; } = [];

    /// <summary>Optional deadline. A ticket past this date and not yet resolved/closed is overdue.</summary>
    public DateTimeOffset? DueAt { get; set; }
    public string Url { get; set; } = "";
}

public sealed record MockComment(string Author, string Body, DateTimeOffset CreatedAt);

/// <summary>A single entry in a ticket's audit trail.</summary>
public sealed record MockEvent(string Description, DateTimeOffset At);

// Request bodies accepted by the API.
public sealed record CreateTicketBody(
    string Title, string? Description, string? Priority, string? Assignee, List<string>? Labels,
    List<string>? RelatedTo = null, string? Type = null);
public sealed record UpdateStatusBody(string Status);
public sealed record UpdateAssigneeBody(string? Assignee);
public sealed record UpdateDueBody(DateTimeOffset? DueAt);
public sealed record AddCommentBody(string? Author, string Body);
