using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using TicketAssistant.Api.Models;
using TicketAssistant.Api.Providers;

namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// The tool-calling loop, written by hand rather than via ChatClientBuilder's
/// UseFunctionInvocation(): send messages + tools, if the response carries function
/// calls execute them and append the results, call the model again, repeat until
/// plain text comes back. The one deviation from the automatic version is
/// create_ticket, which is never invoked here — it's surfaced as a
/// ConfirmationRequired event so the caller can show the user a confirmation card
/// first.
/// </summary>
public sealed class OrchestrationLoop(IChatClient chatClient, IReadOnlyList<AIFunction> tools, ITicketProvider provider)
{
    private readonly Dictionary<string, AIFunction> _toolsByName = tools.ToDictionary(t => t.Name);

    public IAsyncEnumerable<OrchestrationEvent> RunAsync(
        List<ChatMessage> messages,
        CancellationToken ct = default)
        => StepAsync(messages, ct);

    /// <summary>
    /// Called after the user approves or declines a create_ticket confirmation card.
    /// Appends the tool result (or a decline notice) for the given call and resumes
    /// the loop.
    /// </summary>
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

    private async IAsyncEnumerable<OrchestrationEvent> StepAsync(
        List<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
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

            var response = await chatClient.GetResponseAsync(messages, options, ct);
            messages.AddMessages(response);

            var calls = response.Messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (calls.Count == 0)
            {
                yield return new OrchestrationEvent.AssistantText(response.Text);
                yield break;
            }

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

                if (!_toolsByName.TryGetValue(call.Name, out var tool))
                {
                    messages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(call.CallId, $"Unknown tool '{call.Name}'.")]));
                    yield return new OrchestrationEvent.ToolExecuted(call.Name, Succeeded: false);
                    continue;
                }

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

    private static string? ArgString(IDictionary<string, object?> arguments, string key)
        => arguments.TryGetValue(key, out var value) ? value?.ToString() : null;

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
