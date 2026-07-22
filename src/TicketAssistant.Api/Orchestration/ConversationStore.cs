using System.Collections.Concurrent;
using Microsoft.Extensions.AI;

namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// Remembers each chat's message history. The model itself is stateless — it forgets
/// everything between HTTP requests — so we must keep the full running transcript here and
/// resend it on every turn. Conversations are keyed by a Guid the client passes back each
/// time. In-memory only: fine for a single-instance dev setup, but restarting the app wipes
/// all chats; swap for a persisted store (e.g. EF Core + Postgres) for anything real.
/// </summary>
public sealed class ConversationStore
{
    /// <summary>
    /// The standing instructions given to the model at the very start of every conversation
    /// (as a "system" message). This is where the assistant's behavior is defined in plain
    /// English: gather required fields, ask when something's missing, avoid duplicates, and
    /// never fabricate results. Changing this text changes how the assistant behaves.
    /// </summary>
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

        If the user already has a ticket for the same issue, do not create a duplicate. Tell
        them about the existing ticket and ask whether they want to reopen it (set its status
        back to Open), add an update/comment to it, or create a separate new ticket. Only
        create a new one if the user explicitly asks for it.

        Tickets may have a due date. When you list or summarize them, call out anything whose
        due date has passed and that isn't Resolved or Closed as overdue, so it doesn't get
        forgotten. Set or change a deadline with set_due_date.

        If the user asks how things stand — a summary, an overview, a status report, "what's
        outstanding?" — call list_tickets and reply with a short digest rather than dumping
        every field: how many tickets there are, the breakdown by status, and anything Urgent
        or High priority that deserves attention. A few lines is plenty.

        Tool results are final. When a tool returns a result, the user has already approved
        the action in the confirmation card and it is complete — report what happened, and
        never ask the user to confirm, finalize, or approve something that has already
        returned a result.

        If a tool result says the user declined the action, nothing happened: no ticket was
        created or changed, and no duplicate was found. Do not invent a reason for the
        decline — just acknowledge you didn't proceed and ask what they'd like to do instead.

        Never invent ticket IDs or claim an action succeeded unless a tool call actually
        returned that result.
        """;

    /// <summary>
    /// The opening message shown to the user before they type anything: who the assistant is,
    /// what it can do, and how to start. Deliberately fixed text rather than model-generated —
    /// it appears instantly, reads the same every time, and can't invent features that don't
    /// exist. Returned by the create-conversation endpoint so any frontend can display it.
    /// </summary>
    public const string Greeting =
        """
        👋 Hi! I'm your ticketing assistant.

        Tell me about a problem and I'll open a ticket for it — or ask me how your existing
        tickets are doing, and I can update, resolve, or comment on them for you.

        Don't worry about getting anything wrong: I'll always check with you before changing
        anything, and you can just say "undo that" if you change your mind.

        So — what can I help you with today?
        """;

    // Thread-safe map of conversation id -> its list of messages (system, user, assistant,
    // tool). ConcurrentDictionary because multiple requests may touch the store at once.
    private readonly ConcurrentDictionary<Guid, List<ChatMessage>> _conversations = new();

    /// <summary>
    /// Starts a new conversation: generates an id and seeds the history with the system
    /// prompt plus the greeting (recorded as the assistant's opening turn, so the model knows
    /// it has already introduced itself and doesn't repeat it). Returns the id for the client
    /// to use on subsequent messages.
    /// </summary>
    public Guid Create()
    {
        var id = Guid.NewGuid();
        _conversations[id] =
        [
            new ChatMessage(ChatRole.System, SystemPrompt),
            new ChatMessage(ChatRole.Assistant, Greeting)
        ];
        return id;
    }

    /// <summary>
    /// Returns the live message list for a conversation so the caller can append to it and
    /// pass it to the loop. Throws if the id is unknown (e.g. after an app restart).
    /// </summary>
    public List<ChatMessage> Get(Guid conversationId)
    {
        if (!_conversations.TryGetValue(conversationId, out var messages))
        {
            throw new KeyNotFoundException($"No conversation '{conversationId}'.");
        }

        return messages;
    }
}
