using Microsoft.Extensions.AI;
using TicketAssistant.Api.Models;
using TicketAssistant.Api.Providers;

namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// Exposes an ITicketProvider as AIFunctions the model can call. The write tools listed
/// in <see cref="RequiresConfirmation"/> are special-cased by OrchestrationLoop: they are
/// described here like any other tool, but the loop intercepts them before invocation and
/// asks the user to confirm (and optionally edit) instead of running them automatically.
/// Read tools (get/search) run immediately.
/// </summary>
public static class TicketTools
{
    public const string CreateTicketToolName = "create_ticket";
    public const string UpdateStatusToolName = "update_ticket_status";
    public const string AddCommentToolName = "add_comment";

    private static readonly HashSet<string> ConfirmationRequiredTools =
        new(StringComparer.Ordinal) { CreateTicketToolName, UpdateStatusToolName, AddCommentToolName };

    /// <summary>Whether a tool mutates a ticket and so needs the user to confirm it first.</summary>
    public static bool RequiresConfirmation(string toolName) => ConfirmationRequiredTools.Contains(toolName);

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
                // createAnyway is read by OrchestrationLoop (not used here): the loop blocks a
                // create when a similar ticket already exists for the user unless it is set true.
                (string title, string? description, TicketPriority priority,
                 string? assignee = null, string[]? labels = null, bool createAnyway = false,
                 CancellationToken ct = default) =>
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
                              "assignee and labels are optional. If a ticket for the same issue already " +
                              "exists for this user, you'll be told and asked to check with them first; set " +
                              "createAnyway=true only after the user explicitly chooses to create a separate " +
                              "new ticket. Never executed automatically — the caller shows the user a " +
                              "confirmation card and gets explicit approval first."),

            AIFunctionFactory.Create(
                (string ticketId, TicketStatus status, CancellationToken ct) =>
                    provider.UpdateTicketStatusAsync(ticketId, status, ct),
                name: UpdateStatusToolName,
                description: "Change an existing ticket's status. Never executed automatically — the " +
                              "caller shows the user a confirmation card and gets explicit approval first."),

            AIFunctionFactory.Create(
                (string ticketId, string body, CancellationToken ct) =>
                    provider.AddCommentAsync(ticketId, body, ct),
                name: AddCommentToolName,
                description: "Add a comment to an existing ticket. Never executed automatically — the " +
                              "caller shows the user a confirmation card and gets explicit approval first.")
        ];
    }
}
