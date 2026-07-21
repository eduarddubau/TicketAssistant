using System.Net.Http.Json;
using System.Text.Json;
using TicketAssistant.Api.Models;

namespace TicketAssistant.Api.Providers;

/// <summary>
/// Talks to an external ticketing system over its REST API (the TicketingMock.Api service
/// stands in for a real Jira/Zendesk during testing). Maps between the assistant's
/// CanonicalTicket/enum model and the remote system's string-based JSON. The HttpClient's
/// BaseAddress is configured at registration time in Program.cs.
/// </summary>
public sealed class HttpTicketProvider(HttpClient http) : ITicketProvider
{
    // Web defaults = camelCase property names + case-insensitive matching, to line up with
    // what ASP.NET Core minimal APIs emit on the mock side.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Name => "mock-ticketing";

    public async Task<CanonicalTicket> GetTicketAsync(string ticketId, CancellationToken ct = default)
    {
        var dto = await http.GetFromJsonAsync<TicketDto>($"/api/tickets/{ticketId}", Json, ct)
                  ?? throw new KeyNotFoundException($"No ticket '{ticketId}'.");
        return Map(dto);
    }

    public async Task<IReadOnlyList<CanonicalTicket>> SearchTicketsAsync(string query, CancellationToken ct = default)
    {
        var results = await http.GetFromJsonAsync<List<TicketDto>>(
            $"/api/tickets/search?q={Uri.EscapeDataString(query)}", Json, ct) ?? [];
        return results.Select(Map).ToList();
    }

    public async Task<IReadOnlyList<CanonicalTicket>> ListTicketsAsync(
        TicketStatus? status = null, TicketPriority? priority = null, CancellationToken ct = default)
    {
        var query = new List<string>();
        if (status is not null) query.Add($"status={status}");
        if (priority is not null) query.Add($"priority={priority}");
        var url = "/api/tickets" + (query.Count > 0 ? "?" + string.Join("&", query) : "");

        var results = await http.GetFromJsonAsync<List<TicketDto>>(url, Json, ct) ?? [];
        return results.Select(Map).ToList();
    }

    public async Task<CanonicalTicket> CreateTicketAsync(CreateTicketRequest request, CancellationToken ct = default)
    {
        var body = new
        {
            title = request.Title,
            description = request.Description,
            priority = request.Priority.ToString(),
            assignee = request.Assignee,
            labels = request.Labels,
            relatedTo = request.RelatedTo
        };

        var response = await http.PostAsJsonAsync("/api/tickets", body, Json, ct);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<TicketDto>(Json, ct)
                  ?? throw new InvalidOperationException("Empty response creating ticket.");
        return Map(dto);
    }

    public async Task<CanonicalTicket> UpdateTicketStatusAsync(string ticketId, TicketStatus status, CancellationToken ct = default)
    {
        var response = await http.PatchAsJsonAsync(
            $"/api/tickets/{ticketId}/status", new { status = status.ToString() }, Json, ct);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<TicketDto>(Json, ct)
                  ?? throw new InvalidOperationException("Empty response updating ticket.");
        return Map(dto);
    }

    public async Task<CanonicalTicket> AssignTicketAsync(string ticketId, string? assignee, CancellationToken ct = default)
    {
        var response = await http.PatchAsJsonAsync(
            $"/api/tickets/{ticketId}/assignee", new { assignee }, Json, ct);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<TicketDto>(Json, ct)
                  ?? throw new InvalidOperationException("Empty response assigning ticket.");
        return Map(dto);
    }

    public async Task<TicketComment> AddCommentAsync(string ticketId, string body, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync(
            $"/api/tickets/{ticketId}/comments", new { author = "assistant", body }, Json, ct);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<CommentDto>(Json, ct)
                  ?? throw new InvalidOperationException("Empty response adding comment.");
        return new TicketComment { Author = dto.Author, Body = dto.Body, CreatedAt = dto.CreatedAt };
    }

    public async Task DeleteTicketAsync(string ticketId, CancellationToken ct = default)
    {
        var response = await http.DeleteAsync($"/api/tickets/{ticketId}", ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteLastCommentAsync(string ticketId, CancellationToken ct = default)
    {
        var response = await http.DeleteAsync($"/api/tickets/{ticketId}/comments/last", ct);
        response.EnsureSuccessStatusCode();
    }

    // Converts the remote system's wire shape (strings for status/priority) into the app's
    // strongly-typed CanonicalTicket. Unknown status/priority text falls back to a sensible
    // default rather than throwing, so an unexpected value from the backend can't break a read.
    private CanonicalTicket Map(TicketDto d) => new()
    {
        Id = d.Id,
        ProviderName = Name,
        Title = d.Title,
        Description = d.Description,
        Status = Enum.TryParse<TicketStatus>(d.Status, ignoreCase: true, out var s) ? s : TicketStatus.Open,
        Priority = Enum.TryParse<TicketPriority>(d.Priority, ignoreCase: true, out var p) ? p : TicketPriority.Medium,
        Assignee = d.Assignee,
        Reporter = d.Reporter,
        Labels = d.Labels ?? [],
        RelatedTo = d.RelatedTo ?? [],
        CreatedAt = d.CreatedAt,
        UpdatedAt = d.UpdatedAt,
        Url = new Uri(d.Url)
    };

    // Private DTOs mirroring TicketingMock.Api's JSON so System.Text.Json can deserialize
    // into them; Map() then translates a DTO into the app's CanonicalTicket/TicketComment.
    private sealed record TicketDto(
        string Id, string Title, string? Description, string Status, string Priority,
        string? Assignee, string? Reporter, List<string>? Labels, List<string>? RelatedTo,
        DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt, string Url);

    private sealed record CommentDto(string Author, string Body, DateTimeOffset CreatedAt);
}
