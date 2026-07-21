namespace TicketAssistant.Api.Models;

public sealed class TicketComment
{
    public required string Author { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
