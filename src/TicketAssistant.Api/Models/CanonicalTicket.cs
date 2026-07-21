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
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
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
