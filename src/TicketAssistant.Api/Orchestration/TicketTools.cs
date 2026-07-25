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
    /// One slice of a read result: everything of one kind ("Tasks", "Bugs") in one system — the
    /// heading that says so, and its items already written out as the lines the answer should show.
    /// </summary>
    private sealed record TicketGroup(string Heading, IReadOnlyList<string> Items);

    /// <summary>
    /// A read result: how to present it, the filter the user has on (absent when none), and the items
    /// grouped by what they are and where they live.
    /// </summary>
    private sealed record GroupedTickets(
        string Instructions, string? Filter, IReadOnlyList<TicketGroup> Groups);

    /// <summary>
    /// Carried at the top of every grouped read. Instructions travel with the data rather than
    /// sitting only in the tool description or the system prompt, because that is the one place a
    /// 3B model reliably reads them — the same reason the duplicate guardrail spells out what to do
    /// inside its tool result instead of trusting the standing instructions.
    ///
    /// A shape, not a worked example. Told in prose to keep lines short, a 3B model reproduced the
    /// JSON it had been handed instead — every field of every ticket, under invented headings like
    /// "Group 1" and "Section Heading". Shown a realistic sample answer, it copied the *sample*:
    /// a reply opened with "Tasks in Jira — SCRUM-1 · test task" on a session with no Jira connected,
    /// because a plausible example and real data are indistinguishable to it. So the template uses
    /// angle-bracket placeholders and no ids at all — there is nothing in it worth copying.
    /// </summary>
    private const string GroupedTicketsInstructions =
        """
        Write the answer as one bold line per entry in 'groups' followed by that group's 'items' as
        bullets, copying each string exactly and changing none of it:

        **<this group's heading>**
        - <this group's first item>
        - <this group's next item>

        **<the next group's heading>**
        - <that group's first item>

        Everything you list must come from 'groups' — the angle brackets above are placeholders, not
        content, so never write an id, title or heading that isn't in the data. Keep every group and
        every item, in the order given. At most one short sentence of your own before the list (and a
        word about anything OVERDUE after it). Never add fields, never print field names, JSON,
        timestamps or URLs, and never invent headings like "Group 1" or "Section Heading". If
        'groups' is empty, just say nothing matched.

        If a 'filter' is present, the user has narrowed the console to certain kinds of item: say so
        in that one sentence, and never suggest the kinds it hides don't exist.
        """;

    /// <summary>
    /// Groups a fanned-out read by kind *and* by the system each item came from — so an answer can
    /// neither pass demo tickets off as real ones nor blur tasks together with tickets. Shape does
    /// the work that wording couldn't: handed structured tickets, a small model either drops the
    /// fields that matter or dumps every one it was given — so a listing hands it finished lines
    /// (see <see cref="Line"/>) and a heading per group, leaving only the copying to do. Details
    /// beyond the line (description, labels, reporter, links) come from get_ticket, which still
    /// returns the whole ticket. Ordered by heading so repeated questions come back the same way.
    /// </summary>
    private static GroupedTickets GroupByKindAndSource(
        IEnumerable<CanonicalTicket> tickets, ItemTypeScope scope) =>
        new(GroupedTicketsInstructions,
            // Spelled out for the model, not enforced by it (ItemTypeScope has already dropped the
            // rest): without this note a filtered read reads exactly like an empty backlog, and the
            // assistant would cheerfully report that the user has no tickets at all.
            Filter: scope.Active
                ? $"The user has filtered the console to {scope.Description}. Other kinds of item " +
                  "exist but were not returned — mention the filter rather than saying they have none."
                : null,
            Groups: tickets.Where(t => scope.Allows(t.Type))
                   .GroupBy(t => (t.TypePlural, t.Source))
                   .Select(g => new TicketGroup(
                       Heading: $"{g.Key.TypePlural} in {g.Key.Source}",
                       Items: g.Select(Line).ToList()))
                   .OrderBy(g => g.Heading, StringComparer.Ordinal)
                   .ToList());

    /// <summary>
    /// One ticket as the single line a listing should show: id, title, status, priority, and only
    /// what else is genuinely news — the project when the id doesn't already give it away, and a due
    /// date, shouted when it has passed. Everything else is left out on purpose: whatever a listing
    /// carries is what the model will print.
    /// </summary>
    private static string Line(CanonicalTicket t)
    {
        var line = $"{t.Id} · {t.Title} — {StatusText(t.Status)}, {t.Priority}";

        if (!string.IsNullOrWhiteSpace(t.Project)
            && !t.Id.StartsWith(t.Project + "-", StringComparison.OrdinalIgnoreCase))
        {
            line += $", project {t.Project}";
        }

        if (t.DueAt is { } due)
        {
            line += IsOverdue(t) ? $", OVERDUE (was due {due:yyyy-MM-dd})" : $", due {due:yyyy-MM-dd}";
        }

        return line;
    }

    /// <summary>Past its due date and not finished — the one thing a listing should raise its voice about.</summary>
    private static bool IsOverdue(CanonicalTicket t) =>
        t.DueAt is { } due && due < DateTimeOffset.UtcNow
        && t.Status is not (TicketStatus.Resolved or TicketStatus.Closed);

    /// <summary>The enum name as a person writes it, so "InProgress" doesn't reach the user verbatim.</summary>
    private static string StatusText(TicketStatus status) =>
        status == TicketStatus.InProgress ? "In progress" : status.ToString();

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
    public static IReadOnlyList<AIFunction> Build(
        ITicketProvider provider, UndoStore undo, ItemTypeScope scope)
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
                description: "Reverse the most recent change you made for this user — deletes an item " +
                              "you just created, restores a previous status or assignee, or removes a " +
                              "comment you just added. Only the single most recent change can be undone. " +
                              "Use when the user says something like 'undo that' or 'never mind'."),

            AIFunctionFactory.Create(
                (string ticketId, CancellationToken ct) => provider.GetTicketAsync(ticketId, ct),
                name: "get_ticket",
                description: "Fetch a single ticket or task by its provider-native ID (e.g. 'PROJ-1001'). " +
                              "The result's 'type' says which kind of item it is."),

            AIFunctionFactory.Create(
                async (string query, CancellationToken ct) =>
                    GroupByKindAndSource(await provider.SearchTicketsAsync(query, ct), scope),
                name: "search_tickets",
                description: "Search everything assigned to or raised by the user — tickets, tasks, and " +
                              "any other kind of item — by words in their title, description or labels " +
                              "(e.g. 'login', 'printer'). This matches text only — to filter by status, " +
                              "priority or kind use list_tickets instead. Matches come back in 'groups', " +
                              "one per kind of item per system, each with a 'heading' and its 'items' " +
                              "already written as the lines to show. Follow the 'instructions' in the " +
                              "result exactly: copy the headings and lines, and add nothing to them."),

            AIFunctionFactory.Create(
                // status/priority are taken as loose strings and parsed case-insensitively rather
                // than as strict enums: a small model that sends "open" or an empty string would
                // otherwise fail argument binding before the tool even runs.
                // Defaults matter as much as nullability here: a parameter without one is treated
                // as *required*, so a model omitting it fails argument binding before the tool runs.
                async (string? status = null, string? priority = null, string? type = null,
                       CancellationToken ct = default) =>
                    GroupByKindAndSource(
                        await provider.ListTicketsAsync(
                            ParseEnum<TicketStatus>(status), ParseEnum<TicketPriority>(priority), type, ct),
                        scope),
                name: "list_tickets",
                description: "List everything the user raised or is assigned — tickets, tasks, and any " +
                              "other kind of item — optionally filtered by status (Open, InProgress, " +
                              "Blocked, Resolved, Closed), priority (Low, Medium, High, Urgent) and/or " +
                              "type ('Task', 'Bug', 'Ticket'…). Use it for 'my open tickets', 'anything " +
                              "urgent?', 'what are my tasks?' — omit every filter to list the lot. Pass " +
                              "type only when the user asked about one kind; omitting it returns all kinds. " +
                              "Results come back in 'groups', one per kind of item per system, each with a " +
                              "'heading' and its 'items' already written as the lines to show — because a " +
                              "task is not a ticket and some groups are only demo data, and the user cannot " +
                              "tell which from an item alone. Follow the 'instructions' in the result " +
                              "exactly: copy the headings and lines out, and add nothing to them."),

            AIFunctionFactory.Create(
                (CancellationToken ct) => provider.ListProjectsAsync(ct),
                name: "list_projects",
                description: "List the projects the user can file into — each has a key (e.g. 'SUP'), a " +
                              "name (e.g. 'Support') and 'itemTypes', the kinds of item that project " +
                              "accepts (e.g. 'Task', 'Bug', 'Story'). Use this to find the right project " +
                              "key before creating something, to check a project really has the kind the " +
                              "user asked for, or to answer 'what projects do I have?'. Backends without a " +
                              "project concept return an empty list."),

            AIFunctionFactory.Create(
                // createAnyway is read by OrchestrationLoop (not used here): the loop blocks a
                // create when a similar ticket already exists for the user unless it is set true.
                // relatedTo is filled in by OrchestrationLoop when a near-duplicate is created
                // anyway, so the two tickets stay linked rather than drifting apart silently.
                (string title, string? description, TicketPriority priority,
                 string? project = null, string? type = null, string? assignee = null, string[]? labels = null,
                 bool createAnyway = false, string[]? relatedTo = null, CancellationToken ct = default) =>
                    provider.CreateTicketAsync(
                        new CreateTicketRequest
                        {
                            Title = title,
                            Description = description,
                            Project = project,
                            Type = type,
                            Priority = priority,
                            Assignee = assignee,
                            Labels = labels ?? [],
                            RelatedTo = relatedTo ?? []
                        },
                        ct),
                name: CreateTicketToolName,
                description: "Create a new ticket, task, or whatever kind of item the user asked for. Only " +
                              "call this once you have a title, a description, and a priority — if any is " +
                              "missing, ask the user for it rather than guessing. type is the kind to create: " +
                              "pass 'Task' when they ask for a task or a to-do, 'Bug' for a defect, 'Ticket' " +
                              "for a plain support ticket — use the wording the user used, and omit it only " +
                              "when they didn't say. list_projects reports which kinds each project accepts. " +
                              "project is the project key to create it in (e.g. 'SUP'); when the user could " +
                              "have several, call list_projects and use the right one, or omit it to let the " +
                              "user pick on the confirmation card. assignee and labels are optional. If an " +
                              "item for the same issue already exists for this user, you'll be told and asked " +
                              "to check with them first; set createAnyway=true only after the user explicitly " +
                              "chooses to create a separate new one. The user approves this in a confirmation " +
                              "card before it runs — a returned result means they already approved and the " +
                              "item is fully created."),

            AIFunctionFactory.Create(
                (string ticketId, TicketStatus status, CancellationToken ct) =>
                    provider.UpdateTicketStatusAsync(ticketId, status, ct),
                name: UpdateStatusToolName,
                description: "Change an existing ticket's or task's status. The user approves this in a confirmation " +
                              "card before it runs — a returned result means they already approved and the " +
                              "change is fully applied."),

            AIFunctionFactory.Create(
                (string ticketId, DateTimeOffset? dueAt = null, CancellationToken ct = default) =>
                    provider.SetDueDateAsync(ticketId, dueAt, ct),
                name: SetDueDateToolName,
                description: "Set when a ticket or task is due, as a date (e.g. 2026-08-01). Omit dueAt to clear " +
                              "the deadline. The user approves this in a confirmation card before it runs — " +
                              "a returned result means they already approved and the date is set."),

            AIFunctionFactory.Create(
                (string ticketId, string? assignee = null, CancellationToken ct = default) =>
                    provider.AssignTicketAsync(ticketId, assignee, ct),
                name: AssignTicketToolName,
                description: "Assign a ticket or task to someone, or reassign it. Pass an empty assignee to " +
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
                description: "Resolve a ticket or task and record why in one step: sets the status to Resolved " +
                              "and adds the note as a comment. Prefer this over update_ticket_status when " +
                              "the user is closing something out. Ask for a short resolution note if they " +
                              "haven't given one."),

            AIFunctionFactory.Create(
                (string ticketId, string body, CancellationToken ct) =>
                    provider.AddCommentAsync(ticketId, body, ct),
                name: AddCommentToolName,
                description: "Add a comment to an existing ticket or task. The user approves this in a confirmation " +
                              "card before it runs — a returned result means they already approved and the " +
                              "comment is saved.")
        ];
    }
}
