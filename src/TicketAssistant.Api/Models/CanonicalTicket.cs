namespace TicketAssistant.Api.Models;

public sealed class CanonicalTicket
{
    public required string Id { get; init; }
    public required string ProviderName { get; init; }
    public required string Title { get; init; }
    public string? Description { get; init; }
    public required TicketStatus Status { get; init; }
    public TicketPriority Priority { get; init; } = TicketPriority.Medium;
    public string? Assignee { get; init; }
    public string? Reporter { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Ids of tickets covering a similar issue (set when a near-duplicate was created anyway).</summary>
    public IReadOnlyList<string> RelatedTo { get; init; } = [];
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Optional deadline; past this date and not resolved/closed means overdue.</summary>
    public DateTimeOffset? DueAt { get; init; }
    public required Uri Url { get; init; }
}

public enum TicketStatus
{
    Open,
    InProgress,
    Blocked,
    Resolved,
    Closed
}

public enum TicketPriority
{
    Low,
    Medium,
    High,
    Urgent
}
