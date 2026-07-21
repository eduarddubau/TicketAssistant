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
    public string Url { get; set; } = "";
}

public sealed record MockComment(string Author, string Body, DateTimeOffset CreatedAt);

/// <summary>A single entry in a ticket's audit trail.</summary>
public sealed record MockEvent(string Description, DateTimeOffset At);

// Request bodies accepted by the API.
public sealed record CreateTicketBody(
    string Title, string? Description, string? Priority, string? Assignee, List<string>? Labels,
    List<string>? RelatedTo = null);
public sealed record UpdateStatusBody(string Status);
public sealed record UpdateAssigneeBody(string? Assignee);
public sealed record AddCommentBody(string? Author, string Body);
