using TicketAssistant.Api.Models;

namespace TicketAssistant.Api.Providers;

/// <summary>
/// Presents several ticket backends (the mock, Jira, and whatever we add next) to the assistant
/// as one. Reads query every backend and merge; writes route to whichever backend owns the target
/// — the <b>project</b> for a create, the <b>issue id</b> for everything else — found by asking
/// each backend. Backends that error or aren't connected are skipped for reads, so one being down
/// (or a Jira account not yet logged in) never sinks the others.
///
/// This is just another <see cref="ITicketProvider"/>, so the orchestration loop and tools are
/// unchanged — they still depend on a single provider; that provider now happens to be a fan-out.
/// Adding a backend is one more entry in the list built in Program.cs.
/// </summary>
public sealed class CompositeTicketProvider(
    IReadOnlyList<ITicketProvider> providers,
    ILogger<CompositeTicketProvider> logger) : ITicketProvider
{
    public string Name => "composite";

    // ----- Reads: fan out and merge -----

    public async Task<CanonicalTicket> GetTicketAsync(string ticketId, CancellationToken ct = default)
    {
        foreach (var p in providers)
        {
            try { return await p.GetTicketAsync(ticketId, ct); }
            catch (Exception ex) { logger.LogDebug(ex, "get_ticket '{Id}' not served by {Provider}", ticketId, p.Name); }
        }
        throw new KeyNotFoundException($"No ticket '{ticketId}' in any backend.");
    }

    public Task<IReadOnlyList<CanonicalTicket>> SearchTicketsAsync(string query, CancellationToken ct = default)
        => FanReadAsync(p => p.SearchTicketsAsync(query, ct), ct);

    public Task<IReadOnlyList<CanonicalTicket>> ListTicketsAsync(
        TicketStatus? status = null, TicketPriority? priority = null, string? type = null,
        CancellationToken ct = default)
        => FanReadAsync(p => p.ListTicketsAsync(status, priority, type, ct), ct);

    public async Task<IReadOnlyList<TicketProject>> ListProjectsAsync(CancellationToken ct = default)
    {
        var all = new List<TicketProject>();
        foreach (var p in providers)
        {
            try { all.AddRange(await p.ListProjectsAsync(ct)); }
            catch (Exception ex) { logger.LogDebug(ex, "list_projects skipped {Provider}", p.Name); }
        }
        return all;
    }

    private async Task<IReadOnlyList<CanonicalTicket>> FanReadAsync(
        Func<ITicketProvider, Task<IReadOnlyList<CanonicalTicket>>> read, CancellationToken ct)
    {
        var all = new List<CanonicalTicket>();
        foreach (var p in providers)
        {
            try { all.AddRange(await read(p)); }
            catch (Exception ex) { logger.LogWarning(ex, "Read skipped backend {Provider}", p.Name); }
        }
        return all.OrderByDescending(t => t.CreatedAt).ToList();
    }

    // ----- Writes: route to the owning backend -----

    public async Task<CanonicalTicket> CreateTicketAsync(CreateTicketRequest request, CancellationToken ct = default)
        => await (await ProviderForProjectAsync(request.Project, ct)).CreateTicketAsync(request, ct);

    public async Task<CanonicalTicket> UpdateTicketStatusAsync(string ticketId, TicketStatus status, CancellationToken ct = default)
        => await (await ProviderForTicketAsync(ticketId, ct)).UpdateTicketStatusAsync(ticketId, status, ct);

    public async Task<CanonicalTicket> AssignTicketAsync(string ticketId, string? assignee, CancellationToken ct = default)
        => await (await ProviderForTicketAsync(ticketId, ct)).AssignTicketAsync(ticketId, assignee, ct);

    public async Task<CanonicalTicket> SetDueDateAsync(string ticketId, DateTimeOffset? dueAt, CancellationToken ct = default)
        => await (await ProviderForTicketAsync(ticketId, ct)).SetDueDateAsync(ticketId, dueAt, ct);

    public async Task<TicketComment> AddCommentAsync(string ticketId, string body, CancellationToken ct = default)
        => await (await ProviderForTicketAsync(ticketId, ct)).AddCommentAsync(ticketId, body, ct);

    public async Task DeleteTicketAsync(string ticketId, CancellationToken ct = default)
        => await (await ProviderForTicketAsync(ticketId, ct)).DeleteTicketAsync(ticketId, ct);

    public async Task DeleteLastCommentAsync(string ticketId, CancellationToken ct = default)
        => await (await ProviderForTicketAsync(ticketId, ct)).DeleteLastCommentAsync(ticketId, ct);

    // ----- Routing -----

    // Create goes to the backend that owns the requested project; with no/unknown project, the
    // first configured backend is the default (usually the mock).
    private async Task<ITicketProvider> ProviderForProjectAsync(string? projectKey, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(projectKey))
        {
            foreach (var p in providers)
            {
                try
                {
                    if ((await p.ListProjectsAsync(ct)).Any(pr => string.Equals(pr.Key, projectKey, StringComparison.OrdinalIgnoreCase)))
                        return p;
                }
                catch (Exception ex) { logger.LogDebug(ex, "project routing skipped {Provider}", p.Name); }
            }
        }
        return providers[0];
    }

    // Everything keyed by an existing ticket goes to whichever backend actually has that ticket.
    private async Task<ITicketProvider> ProviderForTicketAsync(string ticketId, CancellationToken ct)
    {
        if (providers.Count == 1) return providers[0];
        foreach (var p in providers)
        {
            try { await p.GetTicketAsync(ticketId, ct); return p; }
            catch (Exception ex) { logger.LogDebug(ex, "'{Id}' not owned by {Provider}", ticketId, p.Name); }
        }
        throw new KeyNotFoundException($"No ticket '{ticketId}' in any backend.");
    }
}
