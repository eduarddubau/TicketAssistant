using System.Collections.Concurrent;

namespace TicketingMock.Api;

/// <summary>
/// The whole "database": an in-memory dictionary of tickets. Not persisted — restarting
/// the app resets it to the seed data. Public base URL is used to build shareable ticket
/// links so the assistant can hand the user a URL that opens this app's board.
///
/// Reads and writes are scoped by <c>owner</c> (the X-User-Id passed by the caller): a
/// null/empty owner sees everything (the admin board), otherwise the tickets that user
/// <i>created or is assigned</i> — work landed on someone's plate is theirs to see too.
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

    // A user's own work is what they created *or* what is assigned to them — someone else filing a
    // task and putting your name on it makes it yours to see. An empty owner is the admin board.
    private static bool VisibleTo(MockTicket ticket, string? owner) =>
        string.IsNullOrEmpty(owner)
        || string.Equals(ticket.Owner, owner, StringComparison.OrdinalIgnoreCase)
        || string.Equals(ticket.Assignee, owner, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The user's tickets, optionally filtered by status, priority and/or type (case-insensitive;
    /// null/empty means "don't filter on this"). Backs both the board and the assistant's
    /// list_tickets tool — the structured alternative to free-text search.
    /// </summary>
    public IReadOnlyList<MockTicket> All(
        string? owner, string? status = null, string? priority = null, string? type = null) =>
        _tickets.Values
            .Where(t => VisibleTo(t, owner))
            .Where(t => string.IsNullOrWhiteSpace(status)
                        || string.Equals(t.Status, status, StringComparison.OrdinalIgnoreCase))
            .Where(t => string.IsNullOrWhiteSpace(priority)
                        || string.Equals(t.Priority, priority, StringComparison.OrdinalIgnoreCase))
            .Where(t => string.IsNullOrWhiteSpace(type)
                        || string.Equals(t.Type, type, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => t.CreatedAt)
            .ToList();

    public MockTicket? Get(string id, string? owner) =>
        _tickets.TryGetValue(id, out var ticket) && VisibleTo(ticket, owner) ? ticket : null;

    public IReadOnlyList<MockTicket> Search(string query, string? owner)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return All(owner);
        }

        return _tickets.Values
            .Where(t => VisibleTo(t, owner))
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
            Type = string.IsNullOrWhiteSpace(body.Type) ? "Ticket" : body.Type.Trim(),
            Status = "Open",
            Priority = string.IsNullOrWhiteSpace(body.Priority) ? "Medium" : body.Priority,
            Assignee = body.Assignee,
            // The creator is the reporter here; with reads scoped to creator-or-assignee, saying
            // "ticket-assistant" raised everything would hide who actually asked for it.
            Reporter = string.IsNullOrWhiteSpace(owner) ? "ticket-assistant" : owner,
            Owner = owner,
            Labels = body.Labels ?? [],
            RelatedTo = body.RelatedTo ?? [],
            CreatedAt = DateTimeOffset.UtcNow,
            Url = $"{_publicBaseUrl}/#{id}"
        };

        ticket.History.Add(new MockEvent(
            $"created as {ticket.Type.ToLowerInvariant()} with priority {ticket.Priority}", ticket.CreatedAt));
        _tickets[id] = ticket;
        return ticket;
    }

    public MockTicket? UpdateStatus(string id, string status, string? owner)
    {
        if (Get(id, owner) is not { } ticket)
        {
            return null;
        }

        var previous = ticket.Status;
        ticket.Status = status;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        ticket.History.Add(new MockEvent($"status {previous} -> {status}", ticket.UpdatedAt.Value));
        return ticket;
    }

    /// <summary>Sets (or clears, when null/empty) who the ticket is assigned to.</summary>
    public MockTicket? UpdateAssignee(string id, string? assignee, string? owner)
    {
        if (Get(id, owner) is not { } ticket)
        {
            return null;
        }

        var previous = ticket.Assignee ?? "unassigned";
        ticket.Assignee = string.IsNullOrWhiteSpace(assignee) ? null : assignee;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        ticket.History.Add(new MockEvent($"assignee {previous} -> {ticket.Assignee ?? "unassigned"}", ticket.UpdatedAt.Value));
        return ticket;
    }

    /// <summary>Sets (or clears) the ticket's deadline.</summary>
    public MockTicket? UpdateDue(string id, DateTimeOffset? dueAt, string? owner)
    {
        if (Get(id, owner) is not { } ticket)
        {
            return null;
        }

        ticket.DueAt = dueAt;
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        ticket.History.Add(new MockEvent(
            dueAt is null ? "due date cleared" : $"due date set to {dueAt:yyyy-MM-dd}", ticket.UpdatedAt.Value));
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
        ticket.History.Add(new MockEvent($"comment added by {author}", ticket.UpdatedAt.Value));
        return comment;
    }

    /// <summary>Removes a ticket entirely. Used to undo a create.</summary>
    public bool Delete(string id, string? owner)
    {
        if (Get(id, owner) is null)
        {
            return false;
        }

        return _tickets.TryRemove(id, out _);
    }

    /// <summary>Removes the most recently added comment. Used to undo a comment.</summary>
    public bool RemoveLastComment(string id, string? owner)
    {
        if (Get(id, owner) is not { } ticket || ticket.Comments.Count == 0)
        {
            return false;
        }

        ticket.Comments.RemoveAt(ticket.Comments.Count - 1);
        ticket.UpdatedAt = DateTimeOffset.UtcNow;
        ticket.History.Add(new MockEvent("comment removed (undo)", ticket.UpdatedAt.Value));
        return true;
    }

    // Demo data "alice" (the default console user) can see, so the board isn't empty; switch the
    // user in the console to see isolation. Deliberately a mix: tickets and tasks, so the two kinds
    // are distinguishable in a listing, and one task alice didn't create but is assigned — the case
    // creator-only scoping used to hide.
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

        Create(new CreateTicketBody(
            "Write the release notes for 2.4",
            "Summarize what shipped in 2.4 and post them to the changelog.",
            "Medium", "alice", ["release"], Type: "Task"), owner: "alice");

        Create(new CreateTicketBody(
            "Review the new on-call rota",
            "Morgan drafted next quarter's rota and needs a second pair of eyes.",
            "Low", "alice", ["process"], Type: "Task"), owner: "morgan");
    }
}
