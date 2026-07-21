using Microsoft.Extensions.AI;
using TicketAssistant.Api.Models;
using TicketAssistant.Api.Providers;

namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// Exposes an ITicketProvider as AIFunctions the model can call. The tool named
/// CreateTicketToolName is special-cased by OrchestrationLoop: it is described here
/// like any other tool, but the loop intercepts it before invocation instead of
/// running it automatically.
/// </summary>
public static class TicketTools
{
    public const string CreateTicketToolName = "create_ticket";

    public static IReadOnlyList<AIFunction> Build(ITicketProvider provider)
    {
        return
        [
            AIFunctionFactory.Create(
                (string ticketId, CancellationToken ct) => provider.GetTicketAsync(ticketId, ct),
                name: "get_ticket",
                description: "Fetch a single ticket by its provider-native ID (e.g. 'PROJ-1001')."),

            AIFunctionFactory.Create(
                (string query, CancellationToken ct) => provider.SearchTicketsAsync(query, ct),
                name: "search_tickets",
                description: "Search tickets by title/description text."),

            AIFunctionFactory.Create(
                (string title, string? description, TicketPriority priority,
                 string? assignee = null, string[]? labels = null, CancellationToken ct = default) =>
                    provider.CreateTicketAsync(
                        new CreateTicketRequest
                        {
                            Title = title,
                            Description = description,
                            Priority = priority,
                            Assignee = assignee,
                            Labels = labels ?? []
                        },
                        ct),
                name: CreateTicketToolName,
                description: "Create a new ticket. Only call this once you have a title, a description, " +
                              "and a priority — if any is missing, ask the user for it rather than guessing. " +
                              "assignee and labels are optional. Never executed automatically — the caller " +
                              "shows the user a confirmation card and gets explicit approval first."),

            AIFunctionFactory.Create(
                (string ticketId, TicketStatus status, CancellationToken ct) =>
                    provider.UpdateTicketStatusAsync(ticketId, status, ct),
                name: "update_ticket_status",
                description: "Change an existing ticket's status."),

            AIFunctionFactory.Create(
                (string ticketId, string body, CancellationToken ct) =>
                    provider.AddCommentAsync(ticketId, body, ct),
                name: "add_comment",
                description: "Add a comment to an existing ticket.")
        ];
    }
}
