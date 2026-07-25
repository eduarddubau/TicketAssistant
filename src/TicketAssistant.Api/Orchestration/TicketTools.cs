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
    public const string ResolveTicketToolName = "resolve_ticket";
    public const string AssignTicketToolName = "assign_ticket";
    public const string UndoToolName = "undo_last_action";
    public const string SetDueDateToolName = "set_due_date";

    // The tools that change a ticket and therefore need explicit user approval.
    private static readonly HashSet<string> ConfirmationRequiredTools =
        new(StringComparer.Ordinal)
        {
            CreateTicketToolName, UpdateStatusToolName, AddCommentToolName,
            ResolveTicketToolName, AssignTicketToolName, SetDueDateToolName
        };

    /// <summary>Whether a tool mutates a ticket and so needs the user to confirm it first.</summary>
    public static bool RequiresConfirmation(string toolName) => ConfirmationRequiredTools.Contains(toolName);

    /// <summary>
    /// Parses a loose string into an enum, case-insensitively, returning null for empty or
    /// unrecognized values instead of throwing — so a weak model's "open"/"URGENT"/"" doesn't
    /// crash a read tool during argument binding.
    /// </summary>
    private static TEnum? ParseEnum<TEnum>(string? value) where TEnum : struct, Enum =>
        !string.IsNullOrWhiteSpace(value) && Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : null;

    /// <summary>
    /// Wraps each provider method as an AIFunction the model can call. AIFunctionFactory.Create
    /// inspects the lambda's parameters to generate the schema the model sees, so parameter
    /// names/types and the description text are effectively the model's API documentation.
    /// </summary>
    public static IReadOnlyList<AIFunction> Build(ITicketProvider provider, UndoStore undo)
    {
        return
        [
            AIFunctionFactory.Create(
                // Not in ConfirmationRequiredTools: asking to undo *is* the confirmation, so
                // making the user approve a second dialog would just be friction.
                async (CancellationToken ct) =>
                {
                    if (undo.Take() is not { } action)
                    {
                        return "There is nothing to undo.";
                    }

                    await action.Revert(provider, ct);
                    return $"Undone: {action.Description}";
                },
                name: UndoToolName,
                description: "Reverse the most recent change you made for this user — deletes a ticket " +
                              "you just created, restores a previous status or assignee, or removes a " +
                              "comment you just added. Only the single most recent change can be undone. " +
                              "Use when the user says something like 'undo that' or 'never mind'."),

            AIFunctionFactory.Create(
                (string ticketId, CancellationToken ct) => provider.GetTicketAsync(ticketId, ct),
                name: "get_ticket",
                description: "Fetch a single ticket by its provider-native ID (e.g. 'PROJ-1001')."),

            AIFunctionFactory.Create(
                (string query, CancellationToken ct) => provider.SearchTicketsAsync(query, ct),
                name: "search_tickets",
                description: "Search tickets by words appearing in their title, description or labels " +
                              "(e.g. 'login', 'printer'). This matches text only — to filter by status " +
                              "or priority use list_tickets instead. Results span every connected system; " +
                              "mention each match's project so the user knows where it lives."),

            AIFunctionFactory.Create(
                // status/priority are taken as loose strings and parsed case-insensitively rather
                // than as strict enums: a small model that sends "open" or an empty string would
                // otherwise fail argument binding before the tool even runs.
                // Defaults matter as much as nullability here: a parameter without one is treated
                // as *required*, so a model omitting it fails argument binding before the tool runs.
                (string? status = null, string? priority = null, CancellationToken ct = default) =>
                    provider.ListTicketsAsync(ParseEnum<TicketStatus>(status), ParseEnum<TicketPriority>(priority), ct),
                name: "list_tickets",
                description: "List the user's tickets, optionally filtered by status (Open, InProgress, " +
                              "Blocked, Resolved, Closed) and/or priority (Low, Medium, High, Urgent). " +
                              "Use this for questions like 'my open tickets', 'anything urgent?', or " +
                              "'all my tickets' — omit both filters to list everything. Results span " +
                              "every connected system, so each ticket carries a 'project' and a " +
                              "'providerName': when you list tickets, always say which project each one " +
                              "belongs to, since the same user can have tickets in several."),

            AIFunctionFactory.Create(
                (CancellationToken ct) => provider.ListProjectsAsync(ct),
                name: "list_projects",
                description: "List the projects the user can file tickets in — each has a key (e.g. 'SUP') " +
                              "and a name (e.g. 'Support'). Use this to find the right project key before " +
                              "creating a ticket in a particular area, or to answer 'what projects do I have?'. " +
                              "Backends without a project concept return an empty list."),

            AIFunctionFactory.Create(
                // createAnyway is read by OrchestrationLoop (not used here): the loop blocks a
                // create when a similar ticket already exists for the user unless it is set true.
                // relatedTo is filled in by OrchestrationLoop when a near-duplicate is created
                // anyway, so the two tickets stay linked rather than drifting apart silently.
                (string title, string? description, TicketPriority priority,
                 string? project = null, string? assignee = null, string[]? labels = null,
                 bool createAnyway = false, string[]? relatedTo = null, CancellationToken ct = default) =>
                    provider.CreateTicketAsync(
                        new CreateTicketRequest
                        {
                            Title = title,
                            Description = description,
                            Project = project,
                            Priority = priority,
                            Assignee = assignee,
                            Labels = labels ?? [],
                            RelatedTo = relatedTo ?? []
                        },
                        ct),
                name: CreateTicketToolName,
                description: "Create a new ticket. Only call this once you have a title, a description, " +
                              "and a priority — if any is missing, ask the user for it rather than guessing. " +
                              "project is the project key to create it in (e.g. 'SUP'); when the user could " +
                              "have several, call list_projects and use the right one, or omit it to let the " +
                              "user pick on the confirmation card. assignee and labels are optional. If a " +
                              "ticket for the same issue already exists for this user, you'll be told and asked " +
                              "to check with them first; set createAnyway=true only after the user explicitly " +
                              "chooses to create a separate new ticket. The user approves this in a confirmation " +
                              "card before it runs — a returned result means they already approved and the " +
                              "ticket is fully created."),

            AIFunctionFactory.Create(
                (string ticketId, TicketStatus status, CancellationToken ct) =>
                    provider.UpdateTicketStatusAsync(ticketId, status, ct),
                name: UpdateStatusToolName,
                description: "Change an existing ticket's status. The user approves this in a confirmation " +
                              "card before it runs — a returned result means they already approved and the " +
                              "change is fully applied."),

            AIFunctionFactory.Create(
                (string ticketId, DateTimeOffset? dueAt = null, CancellationToken ct = default) =>
                    provider.SetDueDateAsync(ticketId, dueAt, ct),
                name: SetDueDateToolName,
                description: "Set when a ticket is due, as a date (e.g. 2026-08-01). Omit dueAt to clear " +
                              "the deadline. The user approves this in a confirmation card before it runs — " +
                              "a returned result means they already approved and the date is set."),

            AIFunctionFactory.Create(
                (string ticketId, string? assignee = null, CancellationToken ct = default) =>
                    provider.AssignTicketAsync(ticketId, assignee, ct),
                name: AssignTicketToolName,
                description: "Assign a ticket to someone, or reassign it. Pass an empty assignee to " +
                              "leave it unassigned. The user approves this in a confirmation card before " +
                              "it runs — a returned result means they already approved and it is applied."),

            AIFunctionFactory.Create(
                // Closing a ticket almost always comes with a "why", so this does both in one
                // action (and therefore one confirmation) instead of a status change + comment.
                async (string ticketId, string note, CancellationToken ct) =>
                {
                    var ticket = await provider.UpdateTicketStatusAsync(ticketId, TicketStatus.Resolved, ct);
                    await provider.AddCommentAsync(ticketId, note, ct);
                    return ticket;
                },
                name: ResolveTicketToolName,
                description: "Resolve a ticket and record why in one step: sets the status to Resolved " +
                              "and adds the note as a comment. Prefer this over update_ticket_status when " +
                              "the user is closing something out. Ask for a short resolution note if they " +
                              "haven't given one."),

            AIFunctionFactory.Create(
                (string ticketId, string body, CancellationToken ct) =>
                    provider.AddCommentAsync(ticketId, body, ct),
                name: AddCommentToolName,
                description: "Add a comment to an existing ticket. The user approves this in a confirmation " +
                              "card before it runs — a returned result means they already approved and the " +
                              "comment is saved.")
        ];
    }
}
