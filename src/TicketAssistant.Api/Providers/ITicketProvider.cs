using TicketAssistant.Api.Models;

namespace TicketAssistant.Api.Providers;

/// <summary>
/// One implementation per ticketing system (Jira, Zendesk, ...). The orchestration
/// loop and the AI tool wrappers only ever depend on this interface, never on a
/// concrete provider.
/// </summary>
public interface ITicketProvider
{
    string Name { get; }

    Task<CanonicalTicket> GetTicketAsync(string ticketId, CancellationToken ct = default);

    Task<IReadOnlyList<CanonicalTicket>> SearchTicketsAsync(string query, CancellationToken ct = default);

    Task<CanonicalTicket> CreateTicketAsync(CreateTicketRequest request, CancellationToken ct = default);

    Task<CanonicalTicket> UpdateTicketStatusAsync(string ticketId, TicketStatus status, CancellationToken ct = default);

    Task<TicketComment> AddCommentAsync(string ticketId, string body, CancellationToken ct = default);
}
