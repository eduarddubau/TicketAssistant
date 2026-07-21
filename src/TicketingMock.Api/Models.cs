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
    public List<string> Labels { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UpdatedAt { get; set; }
    public List<MockComment> Comments { get; set; } = [];
    public string Url { get; set; } = "";
}

public sealed record MockComment(string Author, string Body, DateTimeOffset CreatedAt);

// Request bodies accepted by the API.
public sealed record CreateTicketBody(string Title, string? Description, string? Priority, string? Assignee, List<string>? Labels);
public sealed record UpdateStatusBody(string Status);
public sealed record AddCommentBody(string? Author, string Body);
