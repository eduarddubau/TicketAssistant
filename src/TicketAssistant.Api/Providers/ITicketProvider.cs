using TicketAssistant.Api.Models;

namespace TicketAssistant.Api.Providers;

/// <summary>
/// The seam between the assistant and whatever ticketing system sits behind it. One
/// implementation exists per backend (Jira, Zendesk, an in-memory stub, our mock service…),
/// but the orchestration loop and AI tools only ever depend on this interface — so swapping
/// backends never touches the assistant's logic. Every method speaks in the app's own
/// <see cref="CanonicalTicket"/> model, so callers never see backend-specific shapes.
/// </summary>
public interface ITicketProvider
{
    /// <summary>Short identifier for this backend (e.g. "in-memory", "mock-ticketing").</summary>
    string Name { get; }

    /// <summary>Fetch one ticket by its id; throws/absent if the caller isn't allowed to see it.</summary>
    Task<CanonicalTicket> GetTicketAsync(string ticketId, CancellationToken ct = default);

    /// <summary>Find tickets matching free text (empty query = all the caller's tickets).</summary>
    Task<IReadOnlyList<CanonicalTicket>> SearchTicketsAsync(string query, CancellationToken ct = default);

    /// <summary>Create a new ticket and return it (with its assigned id/URL).</summary>
    Task<CanonicalTicket> CreateTicketAsync(CreateTicketRequest request, CancellationToken ct = default);

    /// <summary>Change an existing ticket's status and return the updated ticket.</summary>
    Task<CanonicalTicket> UpdateTicketStatusAsync(string ticketId, TicketStatus status, CancellationToken ct = default);

    /// <summary>Append a comment to a ticket and return the stored comment.</summary>
    Task<TicketComment> AddCommentAsync(string ticketId, string body, CancellationToken ct = default);
}
