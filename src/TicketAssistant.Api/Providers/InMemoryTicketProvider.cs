using System.Collections.Concurrent;
using TicketAssistant.Api.Models;

namespace TicketAssistant.Api.Providers;

/// <summary>
/// In-memory offline stub of ITicketProvider: keeps tickets in a dictionary so the
/// orchestration loop is runnable with no external ticketing system. Selected by
/// Tickets:Backend=InMemory; HttpTicketProvider is the real HTTP-backed implementation.
/// </summary>
public sealed class InMemoryTicketProvider : ITicketProvider
{
    private readonly ConcurrentDictionary<string, CanonicalTicket> _tickets = new();
    private int _nextId = 1000;

    public string Name => "in-memory";

    // A single synthetic project so this backend is a selectable create target (ids are PROJ-*).
    public Task<IReadOnlyList<TicketProject>> ListProjectsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TicketProject>>([new TicketProject("PROJ", "In-memory board")]);

    public Task<CanonicalTicket> GetTicketAsync(string ticketId, CancellationToken ct = default)
    {
        if (!_tickets.TryGetValue(ticketId, out var ticket))
        {
            throw new KeyNotFoundException($"No ticket '{ticketId}' in {Name}.");
        }

        return Task.FromResult(ticket);
    }

    public Task<IReadOnlyList<CanonicalTicket>> SearchTicketsAsync(string query, CancellationToken ct = default)
    {
        IReadOnlyList<CanonicalTicket> matches = _tickets.Values
            .Where(t => t.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || (t.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToList();

        return Task.FromResult(matches);
    }

    public Task<IReadOnlyList<CanonicalTicket>> ListTicketsAsync(
        TicketStatus? status = null, TicketPriority? priority = null, CancellationToken ct = default)
    {
        IReadOnlyList<CanonicalTicket> matches = _tickets.Values
            .Where(t => status is null || t.Status == status)
            .Where(t => priority is null || t.Priority == priority)
            .OrderByDescending(t => t.CreatedAt)
            .ToList();

        return Task.FromResult(matches);
    }

    public Task<CanonicalTicket> CreateTicketAsync(CreateTicketRequest request, CancellationToken ct = default)
    {
        var id = $"PROJ-{Interlocked.Increment(ref _nextId)}";
        var ticket = new CanonicalTicket
        {
            Id = id,
            ProviderName = Name,
            Title = request.Title,
            Description = request.Description,
            Status = TicketStatus.Open,
            Priority = request.Priority,
            Assignee = request.Assignee,
            Labels = request.Labels,
            CreatedAt = DateTimeOffset.UtcNow,
            Url = new Uri($"https://tickets.example.com/browse/{id}")
        };

        _tickets[id] = ticket;
        return Task.FromResult(ticket);
    }

    public Task<CanonicalTicket> UpdateTicketStatusAsync(string ticketId, TicketStatus status, CancellationToken ct = default)
    {
        if (!_tickets.TryGetValue(ticketId, out var existing))
        {
            throw new KeyNotFoundException($"No ticket '{ticketId}' in {Name}.");
        }

        var next = new CanonicalTicket
        {
            Id = existing.Id,
            ProviderName = existing.ProviderName,
            Title = existing.Title,
            Description = existing.Description,
            Status = status,
            Priority = existing.Priority,
            Assignee = existing.Assignee,
            Reporter = existing.Reporter,
            Labels = existing.Labels,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            Url = existing.Url
        };

        _tickets[ticketId] = next;
        return Task.FromResult(next);
    }

    public Task<CanonicalTicket> SetDueDateAsync(string ticketId, DateTimeOffset? dueAt, CancellationToken ct = default)
    {
        if (!_tickets.TryGetValue(ticketId, out var existing))
        {
            throw new KeyNotFoundException($"No ticket '{ticketId}' in {Name}.");
        }

        var next = new CanonicalTicket
        {
            Id = existing.Id,
            ProviderName = existing.ProviderName,
            Title = existing.Title,
            Description = existing.Description,
            Status = existing.Status,
            Priority = existing.Priority,
            Assignee = existing.Assignee,
            Reporter = existing.Reporter,
            Labels = existing.Labels,
            RelatedTo = existing.RelatedTo,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            DueAt = dueAt,
            Url = existing.Url
        };

        _tickets[ticketId] = next;
        return Task.FromResult(next);
    }

    public Task DeleteTicketAsync(string ticketId, CancellationToken ct = default)
    {
        _tickets.TryRemove(ticketId, out _);
        return Task.CompletedTask;
    }

    // This stub doesn't retain comments, so there's nothing to remove.
    public Task DeleteLastCommentAsync(string ticketId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<CanonicalTicket> AssignTicketAsync(string ticketId, string? assignee, CancellationToken ct = default)
    {
        if (!_tickets.TryGetValue(ticketId, out var existing))
        {
            throw new KeyNotFoundException($"No ticket '{ticketId}' in {Name}.");
        }

        var next = new CanonicalTicket
        {
            Id = existing.Id,
            ProviderName = existing.ProviderName,
            Title = existing.Title,
            Description = existing.Description,
            Status = existing.Status,
            Priority = existing.Priority,
            Assignee = string.IsNullOrWhiteSpace(assignee) ? null : assignee,
            Reporter = existing.Reporter,
            Labels = existing.Labels,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            Url = existing.Url
        };

        _tickets[ticketId] = next;
        return Task.FromResult(next);
    }

    public Task<TicketComment> AddCommentAsync(string ticketId, string body, CancellationToken ct = default)
    {
        if (!_tickets.ContainsKey(ticketId))
        {
            throw new KeyNotFoundException($"No ticket '{ticketId}' in {Name}.");
        }

        return Task.FromResult(new TicketComment
        {
            Author = "orchestration-bot",
            Body = body,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }
}
