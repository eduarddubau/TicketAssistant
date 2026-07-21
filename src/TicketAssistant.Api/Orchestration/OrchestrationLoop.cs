using System.Runtime.CompilerServices;
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
/// <param name="chatClient">The LLM (any provider) behind Microsoft.Extensions.AI's interface.</param>
/// <param name="tools">The AIFunctions the model may call, built by <see cref="TicketTools.Build"/>.</param>
/// <param name="provider">The ticket backend, used directly for the duplicate-detection lookup.</param>
public sealed class OrchestrationLoop(IChatClient chatClient, IReadOnlyList<AIFunction> tools, ITicketProvider provider)
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
            result = await InvokeToolAsync(_toolsByName[call.Name], call, ct);
        }
        else
        {
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
        // Tell the model which tools it may call this turn.
        var options = new ChatOptions { Tools = [.. tools] };

        // Bounds runaway tool loops (e.g. a weak model repeatedly calling create_ticket
        // with empty fields even after being told what's missing).
        const int maxTurns = 8;

        for (var turn = 0; ; turn++)
        {
            if (turn >= maxTurns)
            {
                yield return new OrchestrationEvent.AssistantText(
                    "I wasn't able to complete that. Could you rephrase or give me a bit more detail?");
                yield break;
            }

            // 1. Ask the model what to do next, given the whole conversation so far.
            var response = await chatClient.GetResponseAsync(messages, options, ct);
            messages.AddMessages(response); // remember its reply (text and/or tool calls)

            // 2. Did it ask to call any tools? Pull those out of the reply.
            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            // 3. No tool calls => it's a plain answer for the user. We're done this turn.
            if (calls.Count == 0)
            {
                yield return new OrchestrationEvent.AssistantText(response.Text);
                yield break;
            }

            // 4. Otherwise handle each requested tool call.
            foreach (var call in calls)
            {
                if (TicketTools.RequiresConfirmation(call.Name))
                {
                    var arguments = call.Arguments ?? new Dictionary<string, object?>();

                    // create-specific guardrails, checked before surfacing a confirmation card.
                    if (call.Name == TicketTools.CreateTicketToolName)
                    {
                        var missing = MissingCreateTicketFields(arguments);
                        if (missing.Count > 0)
                        {
                            messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(
                                call.CallId,
                                $"Ticket not created — required fields still missing: {string.Join("; ", missing)}. " +
                                "Ask the user to provide them; do not call create_ticket again until they have.")]));
                            continue;
                        }

                        // Dedup: if the user already has a ticket for the same issue, don't create a
                        // duplicate — hand the matches back so the model offers to reopen/update instead.
                        if (!ArgBool(arguments, "createAnyway"))
                        {
                            var duplicates = await FindSimilarTicketsAsync(ArgString(arguments, "title"), ct);
                            if (duplicates.Count > 0)
                            {
                                var list = string.Join("; ", duplicates.Select(t => $"{t.Id} \"{t.Title}\" (status {t.Status})"));
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

                    // Every write tool pauses here for the user to approve (and optionally edit).
                    yield return new OrchestrationEvent.ConfirmationRequired(call.CallId, call.Name, arguments);
                    yield break;
                }

                // A read tool (get/search) — safe to run right away. First guard against the
                // model hallucinating a tool name we don't actually have.
                if (!_toolsByName.TryGetValue(call.Name, out var tool))
                {
                    messages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(call.CallId, $"Unknown tool '{call.Name}'.")]));
                    yield return new OrchestrationEvent.ToolExecuted(call.Name, Succeeded: false);
                    continue;
                }

                // Run it, feed the result back into the conversation, and tell the UI it ran.
                // The loop then goes around again so the model can use that result.
                var result = await InvokeToolAsync(tool, call, ct);
                messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(call.CallId, result)]));
                yield return new OrchestrationEvent.ToolExecuted(call.Name, Succeeded: true);
            }
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

    /// <summary>
    /// Returns the current user's tickets whose title strongly overlaps the proposed title —
    /// a deliberately simple, deterministic "same issue" heuristic (keyword overlap) so dedup
    /// works regardless of the model. The search is user-scoped by the provider.
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

        return existing.Where(t =>
        {
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
