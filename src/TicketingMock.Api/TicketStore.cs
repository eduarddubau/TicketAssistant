using System.Collections.Concurrent;

namespace TicketingMock.Api;

/// <summary>
/// The whole "database": an in-memory dictionary of tickets. Not persisted — restarting
/// the app resets it to the seed data. Public base URL is used to build shareable ticket
/// links so the assistant can hand the user a URL that opens this app's board.
///
/// Reads and writes are scoped by <c>owner</c> (the X-User-Id passed by the caller): a
/// null/empty owner sees everything (the admin board), otherwise only that user's tickets.
/// This is a test-only stand-in for real per-user authorization.
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

    private static bool OwnedBy(MockTicket ticket, string? owner) =>
        string.IsNullOrEmpty(owner) || string.Equals(ticket.Owner, owner, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The owner's tickets, optionally filtered by status and/or priority (case-insensitive;
    /// null/empty means "don't filter on this"). Backs both the board and the assistant's
    /// list_tickets tool — the structured alternative to free-text search.
    /// </summary>
    public IReadOnlyList<MockTicket> All(string? owner, string? status = null, string? priority = null) =>
        _tickets.Values
            .Where(t => OwnedBy(t, owner))
            .Where(t => string.IsNullOrWhiteSpace(status)
                        || string.Equals(t.Status, status, StringComparison.OrdinalIgnoreCase))
            .Where(t => string.IsNullOrWhiteSpace(priority)
                        || string.Equals(t.Priority, priority, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.CreatedAt)
            .ToList();

    public MockTicket? Get(string id, string? owner) =>
        _tickets.TryGetValue(id, out var ticket) && OwnedBy(ticket, owner) ? ticket : null;

    public IReadOnlyList<MockTicket> Search(string query, string? owner)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return All(owner);
        }

        return _tickets.Values
            .Where(t => OwnedBy(t, owner))
            .Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (t.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                        || t.Labels.Any(l => l.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(t => t.CreatedAt)
            .ToList();
    }

    public MockTicket Create(CreateTicketBody body, string? owner)
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
            Owner = owner,
            Labels = body.Labels ?? [],
            CreatedAt = DateTimeOffset.UtcNow,
            Url = $"{_publicBaseUrl}/#{id}"
        };

        _tickets[id] = ticket;
        return ticket;
    }

    public MockTicket? UpdateStatus(string id, string status, string? owner)
    {
        if (Get(id, owner) is not { } ticket)
        {
            return null;
        }

        ticket.Status = status;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return ticket;
    }

    /// <summary>Sets (or clears, when null/empty) who the ticket is assigned to.</summary>
    public MockTicket? UpdateAssignee(string id, string? assignee, string? owner)
    {
        if (Get(id, owner) is not { } ticket)
        {
            return null;
        }

        ticket.Assignee = string.IsNullOrWhiteSpace(assignee) ? null : assignee;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return ticket;
    }

    public MockComment? AddComment(string id, string author, string body, string? owner)
    {
        if (Get(id, owner) is not { } ticket)
        {
            return null;
        }

        var comment = new MockComment(author, body, DateTimeOffset.UtcNow);
        ticket.Comments.Add(comment);
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        return comment;
    }

    // Demo data owned by "alice" so the default console user sees a populated board;
    // switch the user in the console to see isolation (a different user starts empty).
    private void Seed()
    {
        Create(new CreateTicketBody(
            "Login page returns 500 on submit",
            "Users report an intermittent 500 error when submitting the login form.",
            "High", "alice", ["bug", "auth"]), owner: "alice");

        Create(new CreateTicketBody(
            "Add dark mode to settings",
            "Feature request: a dark theme toggle on the settings screen.",
            "Low", null, ["enhancement", "ui"]), owner: "alice");
    }
}
