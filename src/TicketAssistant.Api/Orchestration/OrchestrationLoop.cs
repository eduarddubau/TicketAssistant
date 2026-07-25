using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
/// <param name="debug">Whether this request also wants that trace streamed to the browser, for
/// the console's debug console. When it's off, no snapshot is built at all.</param>
public sealed class OrchestrationLoop(
    ChatClientFactory chatClients,
    IReadOnlyList<AIFunction> tools,
    ITicketProvider provider,
    UndoStore undo,
    ILogger<OrchestrationLoop> logger,
    DebugTrace debug)
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

            if (debug.Enabled)
            {
                yield return DebugEvents.Confirmation(
                    $"✔ user approved {call.Name}",
                    new JsonObject
                    {
                        ["callId"] = callId,
                        ["tool"] = call.Name,
                        ["approved"] = true,
                        ["edits"] = DebugEvents.Value(edits),
                        ["finalArguments"] = DebugEvents.Value(arguments)
                    });
                yield return DebugEvents.ToolCall(call.Name, call, requiresConfirmation: true);
            }

            // Snapshot the ticket before we change it, so "undo that" can restore the old value.
            var ticketId = ArgString(arguments, "ticketId");
            var before = ticketId is null ? null : await TryGetTicketAsync(ticketId, ct);

            var writeStopwatch = Stopwatch.StartNew();
            result = await InvokeToolAsync(_toolsByName[call.Name], call, ct);
            writeStopwatch.Stop();

            if (debug.Enabled)
            {
                yield return DebugEvents.ToolResult(
                    call.Name, callId, result,
                    succeeded: result is not string s || !s.StartsWith("Error:", StringComparison.Ordinal),
                    writeStopwatch.ElapsedMilliseconds);
            }

            var undoBefore = undo.Peek();
            RecordUndo(call.Name, before, result);
            if (debug.Enabled)
            {
                var undoAfter = undo.Peek();
                yield return DebugEvents.Undo(
                    undoAfter is null ? "nothing recorded to undo" : $"recorded: {undoAfter.Description}",
                    new JsonObject
                    {
                        ["tool"] = call.Name,
                        ["previousUndo"] = undoBefore?.Description,
                        ["currentUndo"] = undoAfter?.Description,
                        ["ticketBefore"] = DebugEvents.Value(before)
                    });
            }
        }
        else
        {
            logger.LogInformation("User declined {Tool}", call.Name);
            result = DeclineResult(call.Name);

            if (debug.Enabled)
            {
                yield return DebugEvents.Confirmation(
                    $"✖ user declined {call.Name}",
                    new JsonObject
                    {
                        ["callId"] = callId,
                        ["tool"] = call.Name,
                        ["approved"] = false,
                        ["proposedArguments"] = DebugEvents.Value(call.Arguments),
                        ["toolResultGivenToModel"] = result?.ToString()
                    });
            }
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

        // Ollama decides GPU vs CPU per request via num_gpu (how many model layers to offload
        // to the GPU): 0 forces pure CPU. When the console asks for CPU we pass that through;
        // otherwise Ollama's default applies — GPU when the container has one, CPU when not.
        if (chatClients.CpuOnlyRequested())
        {
            options.AdditionalProperties = new AdditionalPropertiesDictionary { ["num_gpu"] = 0 };
        }

        // Bounds runaway tool loops (e.g. a weak model repeatedly calling create_ticket
        // with empty fields even after being told what's missing).
        const int maxTurns = 8;

        // A weak model sometimes writes a tool call as plain text instead of calling it; we
        // retry once on that, then apologise rather than loop forever.
        const int maxBotchedAttempts = 2;
        var botchedAttempts = 0;

        // Nudge a replayed-after-decline create only once per run; if the model insists a
        // second time, let it through — the confirmation card is the real safety net.
        var nudgedRepeatedCreate = false;

        // A model that answers with nothing gets one nudge, then a graceful apology.
        const int maxEmptyReplies = 2;
        var emptyReplies = 0;

        for (var turn = 0; ; turn++)
        {
            if (turn >= maxTurns)
            {
                logger.LogWarning("Giving up after {MaxTurns} turns — the model kept calling tools without finishing", maxTurns);
                if (debug.Enabled)
                {
                    yield return DebugEvents.Guardrail(
                        $"gave up after {maxTurns} turns — the model kept calling tools without finishing",
                        new JsonObject { ["maxTurns"] = maxTurns });
                }

                yield return new OrchestrationEvent.AssistantText(
                    "I wasn't able to complete that. Could you rephrase or give me a bit more detail?");
                yield break;
            }

            // The trace's first entry for this turn: the exact context the model is about to
            // see — system prompt, every message so far, and the tool menu with its schemas.
            if (debug.Enabled)
            {
                var (traceProvider, traceModel) = chatClients.Current();
                yield return DebugEvents.LlmRequest(
                    turn, traceProvider, traceModel, chatClients.CpuOnlyRequested(), messages, tools, options);
            }

            // 1. Ask the model what to do next, given the whole conversation so far. Streaming
            // so its words reach the user as they're generated rather than after the full
            // reply is ready; the fragments are then reassembled into one response for history.
            var stopwatch = Stopwatch.StartNew();
            var updates = new List<ChatResponseUpdate>();

            // Buffer the reply's opening text before streaming it: a weak model sometimes writes
            // a tool call as plain text (e.g. {"name":"create_ticket",...}) instead of calling
            // the tool. We don't want that raw blob shown, so we hold the first few characters,
            // decide whether it's genuine prose, and only then start streaming to the user.
            var buffer = new StringBuilder();
            var streaming = false;     // committed to streaming this reply as prose
            var streamedText = false;  // actually streamed at least one delta

            // Enumerate manually so a failure from the model (rate limit, network, bad key) can
            // be turned into a friendly message instead of bubbling up as a raw stack trace.
            // (yield return isn't allowed inside try/catch, so only MoveNext is guarded.)
            await using var stream = chatClient
                .GetStreamingResponseAsync(messages, options, ct)
                .GetAsyncEnumerator(ct);

            while (true)
            {
                bool hasUpdate;
                var failed = false;
                try
                {
                    hasUpdate = await stream.MoveNextAsync();
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "The language model request failed on turn {Turn}", turn);
                    hasUpdate = false;
                    failed = true;
                }

                if (failed)
                {
                    if (debug.Enabled)
                    {
                        yield return DebugEvents.Guardrail(
                            "the language model request failed — see the server log for the exception",
                            new JsonObject { ["turn"] = turn + 1 });
                    }

                    // yield can't live inside a catch, so we surface the error out here.
                    yield return new OrchestrationEvent.AssistantText(
                        "Ah, sorry — I can't reach the language model at the moment (it may be busy or " +
                        "temporarily unavailable). Nothing was lost, so give it a moment and try again 🙏");
                    yield break;
                }

                if (!hasUpdate)
                {
                    break;
                }

                var update = stream.Current;
                updates.Add(update);
                if (string.IsNullOrEmpty(update.Text))
                {
                    continue;
                }

                if (streaming)
                {
                    streamedText = true;
                    yield return new OrchestrationEvent.AssistantTextDelta(update.Text);
                    continue;
                }

                buffer.Append(update.Text);
                // Once there's enough to judge, stream it — unless it looks like a tool-call
                // blob, which we keep buffered and deal with after the stream ends.
                if (buffer.Length >= 24 && !LooksLikeToolCallText(buffer.ToString()))
                {
                    streaming = true;
                    streamedText = true;
                    yield return new OrchestrationEvent.AssistantTextDelta(buffer.ToString());
                }
            }

            stopwatch.Stop();
            var response = updates.ToChatResponse();

            // 2. Did it ask to call any tools? Pull those out of the reply.
            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            logger.LogInformation(
                "Turn {Turn}: model replied in {ElapsedMs}ms with {ToolCallCount} tool call(s) [{Tools}]",
                turn, stopwatch.ElapsedMilliseconds, calls.Count, string.Join(", ", calls.Select(c => c.Name)));

            if (debug.Enabled)
            {
                yield return DebugEvents.LlmResponse(turn, response, updates, calls, stopwatch.ElapsedMilliseconds);
            }

            // A short genuine reply may have ended before reaching the streaming threshold — flush it.
            if (!streaming && !streamedText && calls.Count == 0 && buffer.Length > 0
                && !LooksLikeToolCallText(response.Text))
            {
                streamedText = true;
                yield return new OrchestrationEvent.AssistantTextDelta(buffer.ToString());
            }

            // Malformed tool call: the model wrote a tool call as text instead of calling it —
            // either the whole reply is a JSON blob (caught before streaming by the buffer) or
            // the blob is embedded mid-sentence in prose that already streamed. Either way,
            // nudge the model to try again; if it keeps failing, apologise rather than surface
            // the blob. The botched reply is left out of history to avoid modelling it.
            var blobEmbeddedInStreamedText = streamedText && ContainsToolCallBlob(response.Text);
            if (calls.Count == 0
                && ((!streamedText && LooksLikeToolCallText(response.Text)) || blobEmbeddedInStreamedText))
            {
                botchedAttempts++;
                logger.LogWarning("Model wrote a malformed tool call as text (attempt {Attempt}, embedded: {Embedded}): {Preview}",
                    botchedAttempts, blobEmbeddedInStreamedText, Truncate(response.Text, 120));

                if (debug.Enabled)
                {
                    yield return DebugEvents.Guardrail(
                        $"the model wrote a tool call as text instead of calling it (attempt {botchedAttempts} of {maxBotchedAttempts})",
                        new JsonObject
                        {
                            ["attempt"] = botchedAttempts,
                            ["maxAttempts"] = maxBotchedAttempts,
                            ["embeddedInStreamedProse"] = blobEmbeddedInStreamedText,
                            ["replyText"] = response.Text
                        });
                }

                if (botchedAttempts >= maxBotchedAttempts)
                {
                    const string apology =
                        "Sorry, that one got away from me — nothing happened on your tickets. Could you " +
                        "put it another way, or give me a little more detail?";
                    // If junk already streamed, rewrite the bubble instead of adding a new one.
                    if (blobEmbeddedInStreamedText)
                    {
                        yield return new OrchestrationEvent.AssistantReplace(apology);
                    }
                    else
                    {
                        yield return new OrchestrationEvent.AssistantText(apology);
                    }

                    yield break;
                }

                if (blobEmbeddedInStreamedText)
                {
                    // The junk is already on screen — wipe the streamed bubble; the retry will
                    // stream a fresh reply in its place.
                    yield return new OrchestrationEvent.AssistantReplace("");
                }

                messages.Add(new ChatMessage(ChatRole.User,
                    "Your previous reply was a malformed tool call and was not shown to me. If you need " +
                    "to perform an action, call the tool properly; otherwise just reply in plain language " +
                    "or ask me for any details you need."));
                continue;
            }

            // An empty reply with no tool calls is a dead end — the user sees a blank bubble and
            // has to prod the assistant ("hello?") to get anything. Small models do this most
            // often right after a guardrail hands them a tool result to explain. Nudge once for
            // a real answer; if it still comes back empty, say something rather than nothing.
            if (calls.Count == 0 && !streamedText && string.IsNullOrWhiteSpace(response.Text))
            {
                emptyReplies++;
                logger.LogWarning("Model returned an empty reply (attempt {Attempt})", emptyReplies);

                if (debug.Enabled)
                {
                    yield return DebugEvents.Guardrail(
                        $"the model replied with nothing at all (attempt {emptyReplies} of {maxEmptyReplies})",
                        new JsonObject { ["attempt"] = emptyReplies, ["maxAttempts"] = maxEmptyReplies });
                }

                if (emptyReplies < maxEmptyReplies)
                {
                    messages.Add(new ChatMessage(ChatRole.User,
                        "Your last reply was empty and nothing was shown to me. Please respond in plain " +
                        "language now — summarize what you found or did, and ask me for whatever you need " +
                        "to continue."));
                    continue;
                }

                yield return new OrchestrationEvent.AssistantText(
                    "Sorry — I lost my train of thought there. Could you say that again, or tell me what " +
                    "you'd like me to do next? I'm still here 🙂");
                yield break;
            }

            messages.AddMessages(response); // remember its reply (text and/or tool calls)

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

                    if (debug.Enabled)
                    {
                        yield return DebugEvents.ToolCall(call.Name, call, requiresConfirmation: true);
                    }

                    // Only one confirmation dialog at a time — tell the model to re-request
                    // any further writes once this one is resolved.
                    if (pendingConfirmation is not null)
                    {
                        logger.LogInformation(
                            "Deferred {Tool}: {PendingTool} is already awaiting confirmation",
                            call.Name, pendingConfirmation.Name);

                        if (debug.Enabled)
                        {
                            yield return DebugEvents.Guardrail(
                                $"deferred {call.Name} — {pendingConfirmation.Name} is already awaiting confirmation",
                                new JsonObject
                                {
                                    ["tool"] = call.Name,
                                    ["callId"] = call.CallId,
                                    ["alreadyPending"] = pendingConfirmation.Name
                                });
                        }

                        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(
                            call.CallId,
                            $"Not executed — '{pendingConfirmation.Name}' is already waiting for the user's " +
                            "confirmation. Ask for this action again once that one has been resolved.")]));
                        continue;
                    }

                    // create-specific guardrails, checked before surfacing a confirmation card.
                    if (call.Name == TicketTools.CreateTicketToolName)
                    {
                        // Replay of a just-declined ticket: after a decline the model sometimes
                        // re-emits its previous create_ticket verbatim instead of reading the
                        // user's newest message. If that message describes a *different* problem
                        // (declined a screen ticket, then said "internet is down"), bounce the
                        // call back once with the message quoted so the model re-decides. A pure
                        // consent message ("yes, go ahead") is left alone — re-proposing the
                        // declined ticket is exactly right then.
                        if (!nudgedRepeatedCreate
                            && IsRepeatOfDeclinedCreate(messages, arguments, out var declinedTitle, out var declinedDescription))
                        {
                            var latestUserText = LatestUserText(messages);
                            if (DescribesSomethingNew(latestUserText, declinedTitle, declinedDescription))
                            {
                                nudgedRepeatedCreate = true;
                                logger.LogInformation(
                                    "Nudged create_ticket: replay of declined ticket \"{Title}\"; latest user message: \"{UserText}\"",
                                    declinedTitle, Truncate(latestUserText, 80));

                                if (debug.Enabled)
                                {
                                    yield return DebugEvents.Guardrail(
                                        $"bounced create_ticket — it replays the ticket the user just declined (\"{declinedTitle}\")",
                                        new JsonObject
                                        {
                                            ["tool"] = call.Name,
                                            ["callId"] = call.CallId,
                                            ["declinedTitle"] = declinedTitle,
                                            ["declinedDescription"] = declinedDescription,
                                            ["latestUserMessage"] = latestUserText,
                                            ["proposedArguments"] = DebugEvents.Value(arguments)
                                        });
                                }

                                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(
                                    call.CallId,
                                    $"Not executed — this is the same ticket (\"{declinedTitle}\") the user just " +
                                    "declined. That ticket was never created and does not exist, so never refer to " +
                                    $"it as an existing or open ticket. The user's latest message was: " +
                                    $"\"{latestUserText}\". If that describes a different problem, call create_ticket " +
                                    "with a title and description based on that message. Only re-propose the declined " +
                                    "ticket if the user explicitly asked you to go ahead with it after declining.")]));
                                continue;
                            }
                        }

                        var missing = MissingCreateTicketFields(arguments);
                        if (missing.Count > 0)
                        {
                            logger.LogInformation("Blocked create_ticket: missing {MissingFields}", string.Join(", ", missing));

                            if (debug.Enabled)
                            {
                                yield return DebugEvents.Guardrail(
                                    $"blocked create_ticket — still missing {string.Join(", ", missing)}",
                                    new JsonObject
                                    {
                                        ["tool"] = call.Name,
                                        ["callId"] = call.CallId,
                                        ["missingFields"] = DebugEvents.Value(missing),
                                        ["proposedArguments"] = DebugEvents.Value(arguments)
                                    });
                            }

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

                                if (debug.Enabled)
                                {
                                    yield return DebugEvents.Guardrail(
                                        $"createAnyway was set — linking the new ticket to {string.Join(", ", related.Select(t => t.Id))}",
                                        new JsonObject
                                        {
                                            ["tool"] = call.Name,
                                            ["callId"] = call.CallId,
                                            ["relatedTo"] = DebugEvents.Value(related.Select(t => t.Id).ToArray()),
                                            ["matches"] = DebugEvents.Value(related)
                                        });
                                }
                            }
                        }
                        else
                        {
                            var duplicates = await FindSimilarTicketsAsync(ArgString(arguments, "title"), ct);
                            if (duplicates.Count > 0)
                            {
                                // The kind is part of the identity here: "you already have a Task
                                // for that" is a different conversation from "a Bug", and the user
                                // may well want the other kind anyway.
                                var list = string.Join("; ", duplicates.Select(
                                    t => $"{t.Id} \"{t.Title}\" ({t.Type} in {t.Source}, status {t.Status})"));
                                logger.LogInformation(
                                    "Blocked create_ticket: {DuplicateCount} possible duplicate(s) of \"{Title}\": {Duplicates}",
                                    duplicates.Count, ArgString(arguments, "title"), list);

                                if (debug.Enabled)
                                {
                                    yield return DebugEvents.Guardrail(
                                        $"blocked create_ticket — {duplicates.Count} possible duplicate(s) of \"{ArgString(arguments, "title")}\"",
                                        new JsonObject
                                        {
                                            ["tool"] = call.Name,
                                            ["callId"] = call.CallId,
                                            ["proposedTitle"] = ArgString(arguments, "title"),
                                            ["duplicates"] = DebugEvents.Value(duplicates)
                                        });
                                }
                                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(
                                    call.CallId,
                                    $"This user already has something for what looks like the same issue: {list}. " +
                                    "Do not create a duplicate. Tell the user what already exists — naming what kind " +
                                    "of item it is and which system it is in — and ask whether they want to reopen it " +
                                    "(set its status to Open), add an update/comment to it, or create a separate new " +
                                    "one. Only if they choose a new one, call create_ticket again with createAnyway " +
                                    "set to true.")]));
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
                    if (debug.Enabled)
                    {
                        yield return DebugEvents.Guardrail(
                            $"the model asked for a tool that doesn't exist: {call.Name}",
                            new JsonObject
                            {
                                ["tool"] = call.Name,
                                ["callId"] = call.CallId,
                                ["arguments"] = DebugEvents.Value(call.Arguments),
                                ["knownTools"] = DebugEvents.Value(_toolsByName.Keys)
                            });
                    }

                    messages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(call.CallId, $"Unknown tool '{call.Name}'.")]));
                    yield return new OrchestrationEvent.ToolExecuted(call.Name, Succeeded: false);
                    continue;
                }

                if (debug.Enabled)
                {
                    yield return DebugEvents.ToolCall(call.Name, call, requiresConfirmation: false);
                }

                // Run it, feed the result back into the conversation, and tell the UI it ran.
                // The loop then goes around again so the model can use that result.
                var toolStopwatch = Stopwatch.StartNew();
                var result = await InvokeToolAsync(tool, call, ct);
                toolStopwatch.Stop();
                var failed = result is string s && s.StartsWith("Error:", StringComparison.Ordinal);
                logger.LogInformation("Ran {Tool} (succeeded: {Succeeded})", call.Name, !failed);
                if (failed)
                {
                    logger.LogWarning("Tool {Tool} returned an error: {Error}", call.Name, result);
                }

                if (debug.Enabled)
                {
                    yield return DebugEvents.ToolResult(
                        call.Name, call.CallId, result, !failed, toolStopwatch.ElapsedMilliseconds);
                }

                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));
                yield return new OrchestrationEvent.ToolExecuted(call.Name, Succeeded: !failed);
            }

            // 5. If one of the calls was a write that passed its guardrails, pause here and let
            // the caller show a confirmation card. Everything else in the batch already ran.
            if (pendingConfirmation is not null)
            {
                logger.LogInformation("Awaiting user confirmation for {Tool}", pendingConfirmation.Name);
                if (debug.Enabled)
                {
                    yield return DebugEvents.Confirmation(
                        $"⏸ paused — waiting for the user to approve {pendingConfirmation.Name}",
                        new JsonObject
                        {
                            ["tool"] = pendingConfirmation.Name,
                            ["callId"] = pendingConfirmation.CallId,
                            ["arguments"] = DebugEvents.Value(pendingArguments)
                        });
                }

                yield return new OrchestrationEvent.ConfirmationRequired(
                    pendingConfirmation.CallId, pendingConfirmation.Name, pendingArguments!);
                yield break;
            }
        }
    }

    /// <summary>
    /// The tool result recorded when the user declines a confirmation card. Spelled out at
    /// length on purpose: a terse "user declined" reads as ambiguous to smaller models, which
    /// then invent an explanation (e.g. claiming a duplicate ticket exists). State exactly
    /// what happened and what to do next.
    /// </summary>
    private static string DeclineResult(string toolName) =>
        $"The user declined the {toolName} action in the confirmation card, so it was NOT run " +
        "and nothing was created or changed. This does not mean a duplicate exists or that " +
        "anything failed — the user simply chose not to proceed. Briefly acknowledge that you " +
        "did not go ahead, and ask what they would like to do instead. Do not mention any other " +
        "ticket. If their next message describes a different problem, build a brand-new ticket " +
        "from that message — do not reuse this declined ticket's title or description.";

    /// <summary>Whether a recorded tool result is the decline note for <paramref name="toolName"/>.</summary>
    private static bool IsDeclineResultFor(string toolName, object? result) =>
        result?.ToString()?.StartsWith($"The user declined the {toolName} action", StringComparison.Ordinal) == true;

    /// <summary>
    /// Detects the model replaying a create_ticket the user just declined: after a decline,
    /// weak models sometimes re-emit their previous tool call verbatim instead of reading the
    /// user's newest message (so "internet is down" gets a confirmation card for the declined
    /// "screen is broken" ticket). True when the most recently declined create in this
    /// conversation has the same title as the newly proposed one.
    /// </summary>
    private static bool IsRepeatOfDeclinedCreate(
        List<ChatMessage> messages,
        IDictionary<string, object?> arguments,
        out string declinedTitle,
        out string declinedDescription)
    {
        declinedTitle = string.Empty;
        declinedDescription = string.Empty;

        // Find the last declined create_ticket result, then the call it belonged to.
        var declinedResult = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .LastOrDefault(r => IsDeclineResultFor(TicketTools.CreateTicketToolName, r.Result));
        if (declinedResult is null)
        {
            return false;
        }

        var declinedCall = messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .LastOrDefault(c => c.CallId == declinedResult.CallId);
        if (declinedCall?.Arguments is null)
        {
            return false;
        }

        var oldTitle = ArgString(declinedCall.Arguments, "title")?.Trim();
        var newTitle = ArgString(arguments, "title")?.Trim();
        if (string.IsNullOrEmpty(oldTitle) || !string.Equals(oldTitle, newTitle, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        declinedTitle = oldTitle;
        declinedDescription = ArgString(declinedCall.Arguments, "description") ?? "";
        return true;
    }

    // Words that signal "go ahead" rather than describing a new problem. Stripped from the
    // user's message before deciding whether it introduces new content (see below).
    private static readonly HashSet<string> ConsentWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "yeah", "okay", "sure", "please", "ahead", "proceed", "create", "anyway",
        "actually", "fine", "confirm", "correct", "right", "still", "want", "need", "just"
    };

    /// <summary>
    /// Whether the user's latest message brings up something new, unrelated to the ticket they
    /// just declined. True = they've moved on (e.g. declined a screen ticket, then said
    /// "internet is down") so a replay of the declined ticket is wrong. False = the message is
    /// pure consent ("yes, go ahead") or still about the declined ticket, so re-proposing it
    /// is legitimate.
    /// </summary>
    private static bool DescribesSomethingNew(string latestUserText, string declinedTitle, string declinedDescription)
    {
        var latest = MeaningfulTokens(latestUserText);
        latest.ExceptWith(ConsentWords);
        if (latest.Count == 0)
        {
            return false; // nothing but consent/filler — the user isn't describing a new issue
        }

        var declined = MeaningfulTokens(declinedTitle);
        declined.UnionWith(MeaningfulTokens(declinedDescription));
        return !latest.Overlaps(declined);
    }

    /// <summary>The text of the user's most recent message, used to re-focus a distracted model.</summary>
    private static string LatestUserText(List<ChatMessage> messages) =>
        messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text?.Trim() ?? "";

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

    /// <summary>
    /// Heuristic for text that is really a botched tool call — the model printing a JSON call
    /// (e.g. {"name":"create_ticket","parameters":{…}}) instead of invoking the tool. We look
    /// for a leading brace plus a "name" key and either a parameters/arguments key or one of our
    /// tool names. Kept strict (must start with '{') so normal prose is never mistaken for one.
    /// </summary>
    private bool LooksLikeToolCallText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] != '{'
            || !trimmed.Contains("\"name\"", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return trimmed.Contains("\"parameters\"", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("\"arguments\"", StringComparison.OrdinalIgnoreCase)
            || _toolsByName.Keys.Any(name => trimmed.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Like <see cref="LooksLikeToolCallText"/>, but finds a tool-call blob buried anywhere in
    /// prose (e.g. "I have a new ticket for you. {"name": "create_ticket", …}") rather than
    /// only at the start. Tries each '{' as a candidate start of a blob.
    /// </summary>
    private bool ContainsToolCallBlob(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        for (var i = text.IndexOf('{'); i >= 0; i = text.IndexOf('{', i + 1))
        {
            if (LooksLikeToolCallText(text[i..]))
            {
                return true;
            }
        }

        return false;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

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
