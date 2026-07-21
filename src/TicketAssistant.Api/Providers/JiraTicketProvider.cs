using System.Collections.Concurrent;
using TicketAssistant.Api.Models;

namespace TicketAssistant.Api.Providers;

/// <summary>
/// Stand-in for a real Jira adapter: same shape an HTTP-backed implementation would
/// have, but backed by memory so the orchestration loop is runnable without OAuth
/// credentials. Swap the method bodies for Jira REST API calls when ready, and add a
/// sibling ZendeskTicketProvider the same way — nothing else in the app changes.
/// </summary>
public sealed class JiraTicketProvider : ITicketProvider
{
    private readonly ConcurrentDictionary<string, CanonicalTicket> _tickets = new();
    private int _nextId = 1000;

    public string Name => "jira";

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
            Url = new Uri($"https://example.atlassian.net/browse/{id}")
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
