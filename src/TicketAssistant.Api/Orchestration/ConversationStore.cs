using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// In-memory conversation history keyed by conversation id. Fine for a single-instance
/// dev setup; swap for a persisted store (e.g. EF Core + Postgres) before this needs to
/// survive a restart or run behind more than one instance.
/// </summary>
public sealed class ConversationStore
{
    private const string SystemPrompt =
        "You are a support-ticket assistant. Use the available tools to look up, search, " +
        "update, and comment on tickets. Never invent ticket IDs or claim an action " +
        "succeeded unless a tool call actually returned that result.";

    private readonly ConcurrentDictionary<Guid, List<ChatMessage>> _conversations = new();

    public Guid Create()
    {
        var id = Guid.NewGuid();
        _conversations[id] = [new ChatMessage(ChatRole.System, SystemPrompt)];
        return id;
    }

    public List<ChatMessage> Get(Guid conversationId)
    {
        if (!_conversations.TryGetValue(conversationId, out var messages))
        {
            throw new KeyNotFoundException($"No conversation '{conversationId}'.");
        }

        return messages;
    }
}
