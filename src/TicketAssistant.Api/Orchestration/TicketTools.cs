using Microsoft.Extensions.AI;
using TicketAssistant.Api.Models;
using TicketAssistant.Api.Providers;

namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// Builds the "menu" of actions the model is allowed to take. A tool (an
/// <c>AIFunction</c>) is just a named C# function plus a plain-English description; we hand
/// this list to the model, and it decides which to call and with what arguments. Each tool
/// here simply forwards to the matching <see cref="ITicketProvider"/> method.
///
/// The write tools listed in <see cref="RequiresConfirmation"/> are special-cased by
/// OrchestrationLoop: they're described here like any other tool, but the loop intercepts
/// them before running and asks the user to confirm (and optionally edit) first. Read tools
/// (get/search) run immediately.
/// </summary>
public static class TicketTools
{
    // Tool names are referenced in several places (here, the loop, the browser dialog), so
    // they live as constants to avoid typos and keep everything in sync.
    public const string CreateTicketToolName = "create_ticket";
    public const string UpdateStatusToolName = "update_ticket_status";
    public const string AddCommentToolName = "add_comment";

    // The tools that change a ticket and therefore need explicit user approval.
    private static readonly HashSet<string> ConfirmationRequiredTools =
        new(StringComparer.Ordinal) { CreateTicketToolName, UpdateStatusToolName, AddCommentToolName };

    /// <summary>Whether a tool mutates a ticket and so needs the user to confirm it first.</summary>
    public static bool RequiresConfirmation(string toolName) => ConfirmationRequiredTools.Contains(toolName);

    /// <summary>
    /// Wraps each provider method as an AIFunction the model can call. AIFunctionFactory.Create
    /// inspects the lambda's parameters to generate the schema the model sees, so parameter
    /// names/types and the description text are effectively the model's API documentation.
    /// </summary>
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
