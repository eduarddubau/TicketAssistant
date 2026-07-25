using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// Builds the <see cref="OrchestrationEvent.Debug"/> events that feed the console's debug
/// console. Each factory here answers one question a reader of the trace will have — what
/// exactly went to the model, what came back, what a tool was called with, why a guardrail
/// fired — and packs it as a <see cref="JsonNode"/> the browser renders as-is.
///
/// Two rules keep this honest: nothing is summarized away (the full message list, including
/// the system prompt, travels on every model call), and building a trace can never break a
/// turn — anything that refuses to serialize is reported as a note instead of thrown.
/// </summary>
internal static class DebugEvents
{
    // A single string longer than this is truncated: a runaway tool result shouldn't be able to
    // wedge the browser. Generous enough that real prompts and ticket lists arrive whole.
    private const int MaxTextLength = 60_000;

    // How many streamed fragments to keep per reply. Enough to watch the model type; a cap so a
    // very long answer doesn't turn into thousands of array entries.
    private const int MaxDeltas = 600;

    // Relaxed escaping because this is read by a person, not a browser: the default encoder turns
    // every apostrophe in a prompt or tool result into ', which makes the trace harder to read
    // than the thing it's tracing. Safe here — the panel escapes HTML itself before rendering.
    private static readonly JsonSerializerOptions SnapshotOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ----- the model call -----

    /// <summary>Everything that is about to be sent to the model on this turn.</summary>
    public static OrchestrationEvent.Debug LlmRequest(
        int turn,
        string provider,
        string model,
        bool cpuOnly,
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<AIFunction> tools,
        ChatOptions options) =>
        new("llm_request",
            $"→ {provider} · {model} · turn {turn + 1} · {messages.Count} message(s), {tools.Count} tool(s)",
            new JsonObject
            {
                ["turn"] = turn + 1,
                ["provider"] = provider,
                ["model"] = model,
                ["cpuOnly"] = cpuOnly,
                ["messageCount"] = messages.Count,
                ["approxPromptChars"] = messages.Sum(m => m.Text?.Length ?? 0),
                ["options"] = new JsonObject
                {
                    ["toolNames"] = Array(tools.Select(t => (JsonNode?)JsonValue.Create(t.Name))),
                    ["additionalProperties"] = Value(options.AdditionalProperties)
                },
                ["messages"] = Array(messages.Select(Message)),
                ["tools"] = Array(tools.Select(Tool))
            });

    /// <summary>What the model actually replied with, raw: text, tool calls, usage, timing.</summary>
    public static OrchestrationEvent.Debug LlmResponse(
        int turn,
        ChatResponse response,
        IReadOnlyList<ChatResponseUpdate> updates,
        IReadOnlyList<FunctionCallContent> calls,
        long elapsedMs)
    {
        var deltas = updates
            .Where(u => !string.IsNullOrEmpty(u.Text))
            .Select(u => u.Text)
            .ToList();

        var toolPart = calls.Count == 0
            ? "no tool calls"
            : $"{calls.Count} tool call(s): {string.Join(", ", calls.Select(c => c.Name))}";

        return new OrchestrationEvent.Debug(
            "llm_response",
            $"← {response.Text?.Length ?? 0} char(s) of text · {toolPart}",
            new JsonObject
            {
                ["turn"] = turn + 1,
                ["elapsedMs"] = elapsedMs,
                ["modelId"] = response.ModelId,
                ["responseId"] = response.ResponseId,
                ["finishReason"] = response.FinishReason?.Value,
                ["usage"] = Value(response.Usage),
                ["text"] = Text(response.Text),
                ["toolCalls"] = Array(calls.Select(ToolCall)),
                ["streamedFragments"] = deltas.Count,
                ["deltas"] = Array(deltas.Take(MaxDeltas).Select(d => (JsonNode?)JsonValue.Create(d))),
                ["messages"] = Array(response.Messages.Select(Message))
            },
            elapsedMs);
    }

    // ----- tools -----

    /// <summary>A tool is about to run, with the arguments the model chose.</summary>
    public static OrchestrationEvent.Debug ToolCall(string toolName, FunctionCallContent call, bool requiresConfirmation) =>
        new("tool_call",
            $"⚙ {toolName}({FormatArguments(call.Arguments)})",
            new JsonObject
            {
                ["callId"] = call.CallId,
                ["tool"] = toolName,
                ["requiresConfirmation"] = requiresConfirmation,
                ["arguments"] = Value(call.Arguments)
            });

    /// <summary>What the tool handed back — the exact value the model reads next.</summary>
    public static OrchestrationEvent.Debug ToolResult(string toolName, string? callId, object? result, bool succeeded, long elapsedMs) =>
        new("tool_result",
            $"{(succeeded ? "✓" : "✗")} {toolName} → {Describe(result)}",
            new JsonObject
            {
                ["callId"] = callId,
                ["tool"] = toolName,
                ["succeeded"] = succeeded,
                ["elapsedMs"] = elapsedMs,
                ["result"] = Value(result)
            },
            elapsedMs);

    // ----- the loop's own decisions -----

    /// <summary>
    /// A rule in the loop changed the course of the turn — a blocked create, a duplicate, a
    /// nudge, a retry after a malformed reply. The detail says what the model was told instead.
    /// </summary>
    public static OrchestrationEvent.Debug Guardrail(string label, JsonObject detail) =>
        new("guardrail", $"🛡 {label}", detail);

    /// <summary>The loop paused and handed a write to the user for approval.</summary>
    public static OrchestrationEvent.Debug Confirmation(string label, JsonObject detail) =>
        new("confirmation", label, detail);

    /// <summary>An undo entry was recorded (or cleared) after a write went through.</summary>
    public static OrchestrationEvent.Debug Undo(string label, JsonObject detail) =>
        new("undo", $"↩ {label}", detail);

    // ----- snapshot helpers -----

    /// <summary>One conversation message: its role, its plain text, and each content part.</summary>
    private static JsonNode? Message(ChatMessage message) => new JsonObject
    {
        ["role"] = message.Role.Value,
        ["authorName"] = message.AuthorName,
        ["text"] = Text(message.Text),
        ["contents"] = Array(message.Contents.Select(Content))
    };

    /// <summary>One part of a message — prose, a tool call, or a tool result.</summary>
    private static JsonNode? Content(AIContent content) => content switch
    {
        TextContent text => new JsonObject { ["kind"] = "text", ["text"] = Text(text.Text) },
        FunctionCallContent call => ToolCall(call),
        FunctionResultContent result => new JsonObject
        {
            ["kind"] = "toolResult",
            ["callId"] = result.CallId,
            ["result"] = Value(result.Result)
        },
        UsageContent usage => new JsonObject { ["kind"] = "usage", ["usage"] = Value(usage.Details) },
        _ => new JsonObject { ["kind"] = content.GetType().Name, ["value"] = Text(content.ToString()) }
    };

    private static JsonNode? ToolCall(FunctionCallContent call) => new JsonObject
    {
        ["kind"] = "toolCall",
        ["callId"] = call.CallId,
        ["name"] = call.Name,
        ["arguments"] = Value(call.Arguments)
    };

    /// <summary>One tool as the model sees it: name, description, and the schema it must fill in.</summary>
    private static JsonNode? Tool(AIFunction tool) => new JsonObject
    {
        ["name"] = tool.Name,
        ["description"] = Text(tool.Description),
        ["requiresConfirmation"] = TicketTools.RequiresConfirmation(tool.Name),
        ["schema"] = Value(tool.JsonSchema)
    };

    /// <summary>Serializes anything into a JsonNode, reporting rather than throwing on failure.</summary>
    public static JsonNode? Value(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return Cap(JsonSerializer.SerializeToNode(value, SnapshotOptions));
        }
        catch (Exception ex)
        {
            return JsonValue.Create($"<could not serialize {value.GetType().Name}: {ex.Message}>");
        }
    }

    /// <summary>Truncates an oversized string node so one giant value can't swamp the console.</summary>
    private static JsonNode? Cap(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? Text(text) : node;

    private static JsonNode? Text(string? text) => text is null
        ? null
        : JsonValue.Create(text.Length <= MaxTextLength
            ? text
            : text[..MaxTextLength] + $"… [truncated, {text.Length} chars total]");

    private static JsonArray Array(IEnumerable<JsonNode?> items) => new([.. items]);

    /// <summary>A one-line rendering of tool arguments for the entry's headline.</summary>
    private static string FormatArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null or { Count: 0 })
        {
            return "";
        }

        return string.Join(", ", arguments.Select(a =>
        {
            var value = a.Value?.ToString() ?? "null";
            return $"{a.Key}: {(value.Length > 40 ? value[..40] + "…" : value)}";
        }));
    }

    /// <summary>A one-line rendering of a tool result for the entry's headline.</summary>
    private static string Describe(object? result)
    {
        var text = result switch
        {
            null => "null",
            string s => s,
            _ => JsonSerializer.Serialize(result, SnapshotOptions)
        };

        text = text.ReplaceLineEndings(" ");
        return text.Length > 90 ? text[..90] + "…" : text;
    }
}
