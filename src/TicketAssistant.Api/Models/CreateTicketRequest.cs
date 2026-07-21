namespace TicketAssistant.Api.Models;

public sealed class CreateTicketRequest
{
    public required string Title { get; init; }
    public string? Description { get; init; }
    public TicketPriority Priority { get; init; } = TicketPriority.Medium;
    public string? Assignee { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = [];
}
