using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

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

        while (true)
        {
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
                    yield return new OrchestrationEvent.ConfirmationRequired(
                        call.CallId, call.Name, call.Arguments ?? new Dictionary<string, object?>());
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
