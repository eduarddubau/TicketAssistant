using System.Collections.Concurrent;

namespace TicketingMock.Api;

/// <summary>
/// The whole "database": an in-memory dictionary of tickets. Not persisted — restarting
/// the app resets it to the seed data. Public base URL is used to build shareable ticket
/// links so the assistant can hand the user a URL that opens this app's board.
/// </summary>
public sealed class TicketStore
{
    private readonly ConcurrentDictionary<string, MockTicket> _tickets = new();
    private readonly string _publicBaseUrl;
    private int _sequence = 1000;

    public TicketStore(IConfiguration configuration)
    {
        _publicBaseUrl = (configuration["PublicBaseUrl"] ?? "http://localhost:5090").TrimEnd('/');
        Seed();
    }

    public IReadOnlyList<MockTicket> All() =>
        _tickets.Values.OrderByDescending(t => t.CreatedAt).ToList();

    public MockTicket? Get(string id) =>
        _tickets.GetValueOrDefault(id);

    public IReadOnlyList<MockTicket> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return All();
        }

        return _tickets.Values
            .Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (t.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                        || t.Labels.Any(l => l.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(t => t.CreatedAt)
            .ToList();
    }

    public MockTicket Create(CreateTicketBody body)
    {
        var id = $"PROJ-{Interlocked.Increment(ref _sequence)}";
        var ticket = new MockTicket
        {
            Id = id,
            Title = body.Title,
            Description = body.Description,
            Status = "Open",
            Priority = string.IsNullOrWhiteSpace(body.Priority) ? "Medium" : body.Priority,
            Assignee = body.Assignee,
            Reporter = "ticket-assistant",
            Labels = body.Labels ?? [],
            CreatedAt = DateTimeOffset.UtcNow,
            Url = $"{_publicBaseUrl}/#{id}"
        };

        _tickets[id] = ticket;
        return ticket;
    }

    public MockTicket? UpdateStatus(string id, string status)
    {
        if (_tickets.TryGetValue(id, out var ticket))
        {
            ticket.Status = status;
            ticket.UpdatedAt = DateTimeOffset.UtcNow;
        }

        return ticket;
    }

    public MockComment? AddComment(string id, string author, string body)
    {
        if (!_tickets.TryGetValue(id, out var ticket))
        {
            return null;
        }

        var comment = new MockComment(author, body, DateTimeOffset.UtcNow);
        ticket.Comments.Add(comment);
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return comment;
    }

    private void Seed()
    {
        Create(new CreateTicketBody(
            "Login page returns 500 on submit",
            "Users report an intermittent 500 error when submitting the login form.",
            "High", "alice", ["bug", "auth"]));

        Create(new CreateTicketBody(
            "Add dark mode to settings",
            "Feature request: a dark theme toggle on the settings screen.",
            "Low", null, ["enhancement", "ui"]));
    }
}
