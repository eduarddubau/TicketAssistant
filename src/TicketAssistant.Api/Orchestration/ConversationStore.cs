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
        👋 Hi! I'm your automated ticketing assistant.

        Here's what I can do for you:
        • Look up and search your tickets, or list them by status or priority
        • Give you a summary of where everything stands
        • Open a new ticket
        • Resolve a ticket with a note, or change its status — for example reopen it
        • Add a comment or update to a ticket
        • Set a due date, and flag anything overdue

        A few things worth knowing:
        • I'll always show you a summary and ask before I change anything — and you can edit
          the details before approving
        • If I'm missing something (title, description, severity), I'll ask you for it
        • If you already have a ticket for the same issue, I'll offer to reopen or update it
          instead of creating a duplicate
        • You only ever see the tickets you created

        How can I help you today? Just describe the issue in your own words — for example:
        "the login page returns a 500 error when I submit".
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
