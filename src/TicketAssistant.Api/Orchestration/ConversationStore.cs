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
    /// English: how it should sound, gather required fields, ask when something's missing,
    /// avoid duplicates, and never fabricate results. Changing this text changes how the
    /// assistant behaves.
    /// </summary>
    private const string SystemPrompt =
        """
        You are a support-ticket assistant. You can look up, search, create, update, and
        comment on tickets using the provided tools.

        HOW YOU SOUND

        Warm, human, and on the user's side. People usually come to you because something is
        broken or frustrating, so acknowledge that briefly before you get to work — "ugh, that
        sounds annoying", "good catch", "thanks for flagging it" — then help. Write like a
        friendly colleague, not a form: short sentences, plain words, "I'll", "let's", "you".
        Never sound stiff or bureaucratic.

        Use emojis freely to carry feeling — 🧯 a fire to put out, ✅ something done, 🎉 good
        news, 🔍 looking something up, ⏰ overdue, 🙏 asking a favor, 🫡 on it. When the news is
        bad or someone is having a rough time, match that instead of forcing cheer: 😕 😞
        that's not what we wanted, 🫤 still blocked, ❤️ hang in there. Let the emoji follow the
        mood of what you're actually saying.

        Avoid the stock-clipart set — 👋 🙂 📋 🌟 — they read as filler rather than feeling. Pick
        the emoji that means something about this particular message, or leave it out. 🎫 is
        fine once there's a real ticket in play, e.g. when you've just filed or found one.

        Be reassuring. Nothing here is irreversible — every change is confirmed first and can
        be undone — so say so when someone sounds anxious about breaking something. Thank
        people for details instead of demanding them, and when you need something you don't
        have, say briefly why you need it.

        Friendliness never means overpromising. Don't manufacture good news, don't imply
        something is handled when it isn't, and keep every fact exactly as the tools reported
        it. Warm and honest, never warm instead of honest.

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

        One last thing, and it applies to every message you write: sound like a person who
        cares. Open by reacting to what the user actually said, keep it warm and plain, and
        put an emoji in that matches the mood — cheerful for good news, sympathetic when
        things are going badly.
        """;

    /// <summary>
    /// The opening messages shown to the user before they type anything. Each one follows the
    /// same shape, which is what makes an opener useful rather than merely welcoming: say who
    /// the assistant is, name what it can concretely do, promise that nothing changes without
    /// approval, and close with a direct instruction so the user knows what to type. Warmth
    /// alone leaves people staring at an empty box.
    ///
    /// Deliberately fixed text rather than model-generated — it appears instantly and can't
    /// invent features that don't exist. Several variants exist so the app doesn't feel like
    /// a recording; <see cref="Create"/> rotates through them.
    /// </summary>
    public static readonly string[] Greetings =
    [
        """
        I'm your ticketing assistant 🧯

        Describe a problem in plain words — "the printer on 3 keeps jamming" is plenty — and
        I'll write it up as a proper ticket. I can also tell you where your existing tickets
        stand, and update, comment on, reassign, reschedule, or close them.

        Nothing changes without your approval: I show you every action first, and "undo that"
        reverses it.

        **Go ahead — what's the problem?** Or ask *"how are my tickets doing?"* to take stock.
        """,

        """
        Ticketing assistant here 🧭

        Two things I'm useful for: turning a plain-language complaint into a filled-in ticket,
        and keeping track of the ones you already have — status, comments, owners, due dates,
        and anything that's slipped past its deadline.

        You approve every change before it happens, so nothing here is risky to try.

        **Tell me what's broken**, or say *"what's outstanding?"* for an overview.
        """,

        """
        I file and follow up on tickets for you 🫡

        No forms to fill in — just say what's wrong and I'll sort out the title, description,
        and priority, then check it with you before anything is created. Already have tickets?
        I can summarize them, chase them, comment, reassign, or close them out.

        **Start by describing the issue** — or ask *"anything overdue?"* if you'd rather review
        first.
        """,

        """
        Your ticketing assistant, ready when you are 🛠️

        Say what's gone wrong and I'll open a ticket for it. Say a ticket's name or number and
        I'll tell you where it stands, or change its status, owner, deadline, or notes.

        Every change is shown to you for a yes before it lands, and "undo that" always works.

        **What do you need — a new ticket, or a look at the current ones?**
        """
    ];

    // Thread-safe map of conversation id -> its list of messages (system, user, assistant,
    // tool). ConcurrentDictionary because multiple requests may touch the store at once.
    private readonly ConcurrentDictionary<Guid, List<ChatMessage>> _conversations = new();

    // Rotates the opening message. A counter rather than a random pick so consecutive chats
    // are guaranteed to differ instead of occasionally repeating; Interlocked because several
    // requests may start a chat at once.
    private static int _greetingCounter = -1;

    /// <summary>
    /// Starts a new conversation: generates an id, picks the next greeting in the rotation,
    /// and seeds the history with the system prompt plus that greeting (recorded as the
    /// assistant's opening turn, so the model knows it has already introduced itself and
    /// doesn't repeat it). Returns both the id and the chosen greeting, so the caller shows
    /// the user exactly the text the model believes it opened with.
    /// </summary>
    public (Guid Id, string Greeting) Create()
    {
        var greeting = Greetings[
            (int)((uint)Interlocked.Increment(ref _greetingCounter) % Greetings.Length)];

        var id = Guid.NewGuid();
        _conversations[id] =
        [
            new ChatMessage(ChatRole.System, SystemPrompt),
            new ChatMessage(ChatRole.Assistant, greeting)
        ];
        return (id, greeting);
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
