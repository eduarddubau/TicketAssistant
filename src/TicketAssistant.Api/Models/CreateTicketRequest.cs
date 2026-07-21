namespace TicketAssistant.Api.Models;

public sealed class CreateTicketRequest
{
    public required string Title { get; init; }
    public string? Description { get; init; }
    public TicketPriority Priority { get; init; } = TicketPriority.Medium;
    public string? Assignee { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Ids of existing tickets covering a similar issue, so near-duplicates stay linked.</summary>
    public IReadOnlyList<string> RelatedTo { get; init; } = [];
}
