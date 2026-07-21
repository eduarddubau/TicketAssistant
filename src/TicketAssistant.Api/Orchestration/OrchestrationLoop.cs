using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using TicketAssistant.Api.Models;

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
public sealed class OrchestrationLoop(IChatClient chatClient, IReadOnlyList<AIFunction> tools)
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
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        object? result = "User declined to create this ticket.";

        if (approved)
        {
            var call = messages
                .SelectMany(m => m.Contents)
                .OfType<FunctionCallContent>()
                .Last(c => c.CallId == callId);

            result = await InvokeToolAsync(_toolsByName[TicketTools.CreateTicketToolName], call, ct);
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
                if (call.Name == TicketTools.CreateTicketToolName)
                {
                    var arguments = call.Arguments ?? new Dictionary<string, object?>();

                    // Guardrail: don't surface a confirmation card for a half-empty ticket.
                    // Hand the model the list of missing fields so it asks the user instead.
                    var missing = MissingCreateTicketFields(arguments);
                    if (missing.Count > 0)
                    {
                        messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(
                            call.CallId,
                            $"Ticket not created — required fields still missing: {string.Join("; ", missing)}. " +
                            "Ask the user to provide them; do not call create_ticket again until they have.")]));
                        continue;
                    }

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
