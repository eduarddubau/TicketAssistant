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
        """
        You are a support-ticket assistant. You can look up, search, create, update, and
        comment on tickets using the provided tools.

        Before creating a ticket, make sure you have:
          - a short title,
          - a description of the problem or request, and
          - a priority (Low, Medium, High, or Urgent).
        If any of these is missing and you cannot reasonably infer it from the conversation,
        ask the user for the missing details in a single message and wait for their reply.
        Do not call the create_ticket tool until you have all three, and never invent details
        the user did not give. Capture an assignee or labels as well if the user mentions them,
        but treat those as optional and do not ask for them.

        Never invent ticket IDs or claim an action succeeded unless a tool call actually
        returned that result.
        """;

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
