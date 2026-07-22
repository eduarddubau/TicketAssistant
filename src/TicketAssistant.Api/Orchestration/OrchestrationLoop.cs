using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using TicketAssistant.Api.Models;
using TicketAssistant.Api.Providers;

namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// The tool-calling loop — the heart of the orchestration. Written by hand rather than via
/// the framework's automatic UseFunctionInvocation() so we can insert our own rules. The
/// basic cycle: send the conversation + the tool menu to the model; if the model asks to
/// call tools, run them and append the results; call the model again; repeat until it
/// replies with plain text. The deviation from the automatic version: write tools
/// (create/update/comment) are never run automatically — the loop stops and raises a
/// ConfirmationRequired event so the caller can show a confirmation card first.
/// </summary>
/// <param name="chatClients">Supplies the LLM for this request (provider/model can be overridden per call).</param>
/// <param name="tools">The AIFunctions the model may call, built by <see cref="TicketTools.Build"/>.</param>
/// <param name="provider">The ticket backend, used directly for the duplicate-detection lookup.</param>
/// <param name="logger">Traces what the model asked for and which guardrails fired — the main
/// way to see why a turn behaved the way it did without reproducing it live.</param>
public sealed class OrchestrationLoop(
    ChatClientFactory chatClients,
    IReadOnlyList<AIFunction> tools,
    ITicketProvider provider,
    UndoStore undo,
    ILogger<OrchestrationLoop> logger)
{
    // Same tools, indexed by name so we can look up the one the model asked for in O(1).
    private readonly Dictionary<string, AIFunction> _toolsByName = tools.ToDictionary(t => t.Name);

    /// <summary>
    /// Entry point for a fresh user message. The caller has already appended the user's text
    /// to <paramref name="messages"/>; this just kicks off the loop and streams back events
    /// (assistant text, tools that ran, or a confirmation request) as they happen.
    /// </summary>
    public IAsyncEnumerable<OrchestrationEvent> RunAsync(
        List<ChatMessage> messages,
        CancellationToken ct = default)
        => StepAsync(messages, ct);

    /// <summary>
    /// Called after the user approves or declines a confirmation card for any write tool.
    /// If approved, it merges the user's edits into the paused tool call, actually runs the
    /// tool, and records the result; if declined, it records a "declined" note instead.
    /// Either way it then resumes the loop so the model can react (e.g. summarize what it did).
    /// </summary>
    /// <param name="messages">The conversation, which still contains the paused tool call.</param>
    /// <param name="callId">Identifies which paused call this decision is for.</param>
    /// <param name="approved">True = run it; false = skip it.</param>
    /// <param name="edits">Field values the user changed in the dialog, applied over the model's originals.</param>
    public async IAsyncEnumerable<OrchestrationEvent> ResumeAfterConfirmationAsync(
        List<ChatMessage> messages,
        string callId,
        bool approved,
        IReadOnlyDictionary<string, object?>? edits = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var call = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .Last(c => c.CallId == callId);

        object? result;

        if (approved)
        {
            // Start from what the model proposed, then apply any fields the user edited in
            // the confirmation dialog so the action runs with the values they approved.
            var arguments = new Dictionary<string, object?>(call.Arguments ?? new Dictionary<string, object?>());
            if (edits is not null)
            {
                foreach (var (key, value) in edits)
                {
                    if (value is not null)
                    {
                        arguments[key] = value;
                    }
                }
            }

            // Overwrite the call's arguments in history too, so the model summarizes what it
            // actually did (edited values), not the values it first proposed.
            call.Arguments = arguments;
            logger.LogInformation(
                "User approved {Tool}{EditedFields}",
                call.Name,
                edits is { Count: > 0 } ? $" with edits to: {string.Join(", ", edits.Keys)}" : " unchanged");

            // Snapshot the ticket before we change it, so "undo that" can restore the old value.
            var ticketId = ArgString(arguments, "ticketId");
            var before = ticketId is null ? null : await TryGetTicketAsync(ticketId, ct);

            result = await InvokeToolAsync(_toolsByName[call.Name], call, ct);

            RecordUndo(call.Name, before, result);
        }
        else
        {
            logger.LogInformation("User declined {Tool}", call.Name);
            result = $"User declined the {call.Name} action.";
        }

        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(callId, result)]));

        await foreach (var evt in StepAsync(messages, ct))
        {
            yield return evt;
        }
    }

    /// <summary>
    /// The actual send-model → run-tools → repeat loop, shared by RunAsync and
    /// ResumeAfterConfirmationAsync. It's an async stream (IAsyncEnumerable): each `yield
    /// return` hands one event to the caller immediately, and the loop keeps going until it
    /// yields a final assistant reply or pauses for a confirmation. [EnumeratorCancellation]
    /// wires the cancellation token into that streaming iterator.
    /// </summary>
    private async IAsyncEnumerable<OrchestrationEvent> StepAsync(
        List<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Tell the model which tools it may call this turn. The client is resolved per run so
        // the caller can switch provider/model between requests.
        var chatClient = chatClients.Resolve();
        var options = new ChatOptions { Tools = [.. tools] };

        // Bounds runaway tool loops (e.g. a weak model repeatedly calling create_ticket
        // with empty fields even after being told what's missing).
        const int maxTurns = 8;

        for (var turn = 0; ; turn++)
        {
            if (turn >= maxTurns)
            {
                logger.LogWarning("Giving up after {MaxTurns} turns — the model kept calling tools without finishing", maxTurns);
                yield return new OrchestrationEvent.AssistantText(
                    "I wasn't able to complete that. Could you rephrase or give me a bit more detail?");
                yield break;
            }

            // 1. Ask the model what to do next, given the whole conversation so far. Streaming
            // so its words reach the user as they're generated rather than after the full
            // reply is ready; the fragments are then reassembled into one response for history.
            var stopwatch = Stopwatch.StartNew();
            var updates = new List<ChatResponseUpdate>();
            var streamedText = false;

            await foreach (var update in chatClient.GetStreamingResponseAsync(messages, options, ct))
            {
                updates.Add(update);
                if (!string.IsNullOrEmpty(update.Text))
                {
                    streamedText = true;
                    yield return new OrchestrationEvent.AssistantTextDelta(update.Text);
                }
            }

            stopwatch.Stop();
            var response = updates.ToChatResponse();
            messages.AddMessages(response); // remember its reply (text and/or tool calls)

            // 2. Did it ask to call any tools? Pull those out of the reply.
            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            logger.LogInformation(
                "Turn {Turn}: model replied in {ElapsedMs}ms with {ToolCallCount} tool call(s) [{Tools}]",
                turn, stopwatch.ElapsedMilliseconds, calls.Count, string.Join(", ", calls.Select(c => c.Name)));

            // 3. No tool calls => it's a plain answer for the user. We're done this turn.
            // If the reply already went out as deltas the UI has it; only send the whole text
            // when nothing streamed (some providers return it in one piece).
            if (calls.Count == 0)
            {
                if (!streamedText)
                {
                    yield return new OrchestrationEvent.AssistantText(response.Text);
                }

                yield break;
            }

            // 4. Otherwise handle each requested tool call.
            //
            // A model can request several tools at once. At most one write can be confirmed at
            // a time, so we remember the first one that needs approval and keep processing the
            // rest — every other call still gets a result. That matters: a tool call left
            // without a result would dangle in the history and confuse (or error) the next
            // model turn. The pending call gets its result later, in ResumeAfterConfirmationAsync.
            FunctionCallContent? pendingConfirmation = null;
            IDictionary<string, object?>? pendingArguments = null;

            foreach (var call in calls)
            {
                if (TicketTools.RequiresConfirmation(call.Name))
                {
                    var arguments = call.Arguments ?? new Dictionary<string, object?>();

                    // Only one confirmation dialog at a time — tell the model to re-request
                    // any further writes once this one is resolved.
                    if (pendingConfirmation is not null)
                    {
                        logger.LogInformation(
                            "Deferred {Tool}: {PendingTool} is already awaiting confirmation",
                            call.Name, pendingConfirmation.Name);
                        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(
                            call.CallId,
                            $"Not executed — '{pendingConfirmation.Name}' is already waiting for the user's " +
                            "confirmation. Ask for this action again once that one has been resolved.")]));
                        continue;
                    }

                    // create-specific guardrails, checked before surfacing a confirmation card.
                    if (call.Name == TicketTools.CreateTicketToolName)
                    {
                        var missing = MissingCreateTicketFields(arguments);
                        if (missing.Count > 0)
                        {
                            logger.LogInformation("Blocked create_ticket: missing {MissingFields}", string.Join(", ", missing));
                            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(
                                call.CallId,
                                $"Ticket not created — required fields still missing: {string.Join("; ", missing)}. " +
                                "Ask the user to provide them; do not call create_ticket again until they have.")]));
                            continue;
                        }

                        // Dedup: if the user already has a ticket for the same issue, don't create a
                        // duplicate — hand the matches back so the model offers to reopen/update instead.
                        if (ArgBool(arguments, "createAnyway"))
                        {
                            // They've chosen a separate ticket anyway — still record which existing
                            // tickets it resembles so the two stay linked instead of drifting apart.
                            var related = await FindSimilarTicketsAsync(ArgString(arguments, "title"), ct);
                            if (related.Count > 0)
                            {
                                arguments["relatedTo"] = related.Select(t => t.Id).ToArray();
                                logger.LogInformation(
                                    "Linking new ticket to existing {Related}", string.Join(", ", related.Select(t => t.Id)));
                            }
                        }
                        else
                        {
                            var duplicates = await FindSimilarTicketsAsync(ArgString(arguments, "title"), ct);
                            if (duplicates.Count > 0)
                            {
                                var list = string.Join("; ", duplicates.Select(t => $"{t.Id} \"{t.Title}\" (status {t.Status})"));
                                logger.LogInformation(
                                    "Blocked create_ticket: {DuplicateCount} possible duplicate(s) of \"{Title}\": {Duplicates}",
                                    duplicates.Count, ArgString(arguments, "title"), list);
                                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(
                                    call.CallId,
                                    $"This user already has a ticket for what looks like the same issue: {list}. " +
                                    "Do not create a duplicate. Tell the user about the existing ticket and ask whether " +
                                    "they want to reopen it (set its status to Open), add an update/comment to it, or " +
                                    "create a separate new ticket. Only if they choose a new one, call create_ticket " +
                                    "again with createAnyway set to true.")]));
                                continue;
                            }
                        }
                    }

                    // Passed the guardrails: this is the write we'll ask the user about, once
                    // the remaining calls in this batch have been dealt with.
                    pendingConfirmation = call;
                    pendingArguments = arguments;
                    continue;
                }

                // A read tool (get/search) — safe to run right away. First guard against the
                // model hallucinating a tool name we don't actually have.
                if (!_toolsByName.TryGetValue(call.Name, out var tool))
                {
                    logger.LogWarning("Model requested unknown tool {Tool}", call.Name);
                    messages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(call.CallId, $"Unknown tool '{call.Name}'.")]));
                    yield return new OrchestrationEvent.ToolExecuted(call.Name, Succeeded: false);
                    continue;
                }

                // Run it, feed the result back into the conversation, and tell the UI it ran.
                // The loop then goes around again so the model can use that result.
                var result = await InvokeToolAsync(tool, call, ct);
                var failed = result is string s && s.StartsWith("Error:", StringComparison.Ordinal);
                logger.LogInformation("Ran {Tool} (succeeded: {Succeeded})", call.Name, !failed);
                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));
                yield return new OrchestrationEvent.ToolExecuted(call.Name, Succeeded: !failed);
            }

            // 5. If one of the calls was a write that passed its guardrails, pause here and let
            // the caller show a confirmation card. Everything else in the batch already ran.
            if (pendingConfirmation is not null)
            {
                logger.LogInformation("Awaiting user confirmation for {Tool}", pendingConfirmation.Name);
                yield return new OrchestrationEvent.ConfirmationRequired(
                    pendingConfirmation.CallId, pendingConfirmation.Name, pendingArguments!);
                yield break;
            }
        }
    }

    /// <summary>
    /// Pulls a ticket id out of whatever a tool returned. AIFunction.InvokeAsync hands back the
    /// result already serialized (a JsonElement) rather than the original CanonicalTicket, so we
    /// accept either shape instead of assuming one.
    /// </summary>
    private static string? ExtractTicketId(object? result) => result switch
    {
        CanonicalTicket ticket => ticket.Id,
        JsonElement { ValueKind: JsonValueKind.Object } json =>
            json.TryGetProperty("id", out var lower) ? lower.GetString()
            : json.TryGetProperty("Id", out var upper) ? upper.GetString()
            : null,
        _ => null
    };

    /// <summary>Fetches a ticket for the "before" snapshot, returning null if it can't be read.</summary>
    private async Task<CanonicalTicket?> TryGetTicketAsync(string ticketId, CancellationToken ct)
    {
        try
        {
            return await provider.GetTicketAsync(ticketId, ct);
        }
        catch
        {
            return null; // undo is best-effort; never fail the real action over it
        }
    }

    /// <summary>
    /// After a successful write, remember how to reverse it so undo_last_action can offer it.
    /// A write we don't know how to reverse clears any stale undo rather than leaving one that
    /// would revert the wrong thing.
    /// </summary>
    private void RecordUndo(string toolName, CanonicalTicket? before, object? result)
    {
        // A failed tool call returns an "Error: ..." string — nothing changed, nothing to undo.
        if (result is string)
        {
            return;
        }

        logger.LogDebug("RecordUndo for {Tool}: result is {ResultType}", toolName, result?.GetType().Name ?? "null");

        switch (toolName)
        {
            case TicketTools.CreateTicketToolName when ExtractTicketId(result) is { } createdId:
                undo.Record(new UndoAction(
                    $"created ticket {createdId} (it will be deleted)",
                    (p, c) => p.DeleteTicketAsync(createdId, c)));
                break;

            case TicketTools.UpdateStatusToolName when before is not null:
                undo.Record(new UndoAction(
                    $"status change on {before.Id} (restoring {before.Status})",
                    (p, c) => p.UpdateTicketStatusAsync(before.Id, before.Status, c)));
                break;

            case TicketTools.AssignTicketToolName when before is not null:
                undo.Record(new UndoAction(
                    $"assignment on {before.Id} (restoring {before.Assignee ?? "unassigned"})",
                    (p, c) => p.AssignTicketAsync(before.Id, before.Assignee, c)));
                break;

            case TicketTools.ResolveTicketToolName when before is not null:
                // resolve = status change + comment, so undo both.
                undo.Record(new UndoAction(
                    $"resolution of {before.Id} (restoring {before.Status} and removing the note)",
                    async (p, c) =>
                    {
                        await p.UpdateTicketStatusAsync(before.Id, before.Status, c);
                        await p.DeleteLastCommentAsync(before.Id, c);
                    }));
                break;

            case TicketTools.AddCommentToolName when before is not null:
                undo.Record(new UndoAction(
                    $"comment on {before.Id} (it will be removed)",
                    (p, c) => p.DeleteLastCommentAsync(before.Id, c)));
                break;

            default:
                undo.Clear();
                break;
        }
    }

    /// <summary>
    /// Returns the human-readable names of required create_ticket fields the model left
    /// empty or invalid. Empty list means the arguments are complete enough to create.
    /// </summary>
    private static List<string> MissingCreateTicketFields(IDictionary<string, object?> arguments)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(ArgString(arguments, "title")))
        {
            missing.Add("title");
        }

        if (string.IsNullOrWhiteSpace(ArgString(arguments, "description")))
        {
            missing.Add("description");
        }

        var priority = ArgString(arguments, "priority");
        if (string.IsNullOrWhiteSpace(priority) || !Enum.TryParse<TicketPriority>(priority, ignoreCase: true, out _))
        {
            missing.Add("priority (Low, Medium, High, or Urgent)");
        }

        return missing;
    }

    // The model's tool arguments arrive as a loosely-typed name -> value bag (values are
    // usually JsonElements). These two helpers safely pull one argument out as a string or a
    // bool without throwing when the key is absent or the value is an unexpected shape.

    /// <summary>Reads argument <paramref name="key"/> as text, or null if it isn't present.</summary>
    private static string? ArgString(IDictionary<string, object?> arguments, string key)
        => arguments.TryGetValue(key, out var value) ? value?.ToString() : null;

    /// <summary>
    /// Reads argument <paramref name="key"/> as a bool. Handles both a real boolean and the
    /// string "true"/"false" (models often send booleans as strings). Missing => false.
    /// </summary>
    private static bool ArgBool(IDictionary<string, object?> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }

        return value is bool b ? b : bool.TryParse(value.ToString(), out var parsed) && parsed;
    }

    // Generic/short words that shouldn't count toward "same issue" similarity on their own.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "when", "after", "from", "that", "this", "page", "issue",
        "ticket", "error", "problem", "request", "please", "some", "does", "cannot", "have"
    };

    /// <summary>
    /// Breaks a title into its distinctive words for the duplicate check: splits on anything
    /// that isn't a letter or digit, drops very short words and common stopwords, lowercases
    /// the rest, and returns the unique set. E.g. "Login page returns 500!" -> {login, returns}.
    /// Two titles are "similar" when these sets overlap enough (see FindSimilarTicketsAsync).
    /// </summary>
    private static HashSet<string> MeaningfulTokens(string? text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return tokens;
        }

        foreach (var token in Regex.Split(text, "[^A-Za-z0-9]+"))
        {
            if (token.Length >= 4 && !StopWords.Contains(token))
            {
                tokens.Add(token.ToLowerInvariant());
            }
        }

        return tokens;
    }

    // A ticket created within this window counts as part of the current interaction, not an
    // "older" ticket to warn against — so a create that fires twice (a double-firing model or
    // the user re-asking) doesn't flag the ticket it just made as its own duplicate.
    private static readonly TimeSpan JustCreatedWindow = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Returns the user's *older* tickets whose title strongly overlaps the proposed title — a
    /// deliberately simple, deterministic "same issue" heuristic (keyword overlap) so dedup
    /// works regardless of the model. Tickets created just now (see <see cref="JustCreatedWindow"/>)
    /// are ignored so the ticket being created isn't matched against itself. User-scoped by the provider.
    /// </summary>
    private async Task<IReadOnlyList<CanonicalTicket>> FindSimilarTicketsAsync(string? proposedTitle, CancellationToken ct)
    {
        var proposed = MeaningfulTokens(proposedTitle);
        if (proposed.Count == 0)
        {
            return [];
        }

        IReadOnlyList<CanonicalTicket> existing;
        try
        {
            existing = await provider.SearchTicketsAsync(string.Empty, ct); // all of this user's tickets
        }
        catch
        {
            return []; // never block a create just because the dedup lookup failed
        }

        var justCreatedCutoff = DateTimeOffset.UtcNow - JustCreatedWindow;

        return existing.Where(t =>
        {
            if (t.CreatedAt >= justCreatedCutoff)
            {
                return false; // brand-new — this is (or is part of) the current create, not an older duplicate
            }

            var other = MeaningfulTokens(t.Title);
            if (other.Count == 0)
            {
                return false;
            }

            var shared = proposed.Count(other.Contains);
            return shared >= 2 || (shared >= 1 && (double)shared / Math.Min(proposed.Count, other.Count) >= 0.5);
        }).ToList();
    }

    /// <summary>
    /// Actually runs one tool with the model's arguments and returns whatever it produced
    /// (e.g. the created/looked-up ticket), which becomes the tool result the model reads
    /// next. Any exception is caught and returned as an "Error: ..." string rather than
    /// thrown, so one failed tool call turns into feedback for the model instead of crashing
    /// the whole turn.
    /// </summary>
    private static async Task<object?> InvokeToolAsync(AIFunction tool, FunctionCallContent call, CancellationToken ct)
    {
        try
        {
            var arguments = new AIFunctionArguments(call.Arguments ?? new Dictionary<string, object?>());
            return await tool.InvokeAsync(arguments, ct);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }
}
