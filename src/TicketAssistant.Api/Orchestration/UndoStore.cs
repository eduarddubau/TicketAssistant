using System.Collections.Concurrent;
using TicketAssistant.Api.Providers;

namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// One undoable action: a human-readable description of what would be reverted, plus the
/// work to revert it. Capturing the revert as a delegate lets the recording site close over
/// whatever "before" state it saw (e.g. the previous status), so undoing needs no extra
/// lookups later.
/// </summary>
public sealed record UndoAction(string Description, Func<ITicketProvider, CancellationToken, Task> Revert);

/// <summary>
/// Remembers the most recent undoable write per user, so "undo that" can reverse it. Only one
/// step deep — undoing again after an undo does nothing, which keeps the mental model simple.
/// Keyed by the caller's X-User-Id (same identity the tickets are scoped by), read from the
/// current request rather than threaded through the orchestration loop.
/// In memory only, like everything else in this PoC.
/// </summary>
public sealed class UndoStore(IHttpContextAccessor accessor)
{
    private readonly ConcurrentDictionary<string, UndoAction> _lastAction = new();

    private string UserKey =>
        accessor.HttpContext?.Request.Headers[UserIdForwardingHandler.UserHeader].ToString() is { Length: > 0 } user
            ? user
            : "anonymous";

    /// <summary>Records what would undo the write that just happened, replacing any earlier one.</summary>
    public void Record(UndoAction action) => _lastAction[UserKey] = action;

    /// <summary>
    /// Takes (and forgets) the pending undo for this user, or null if there's nothing to undo.
    /// Removing on read means an action can only be undone once.
    /// </summary>
    public UndoAction? Take() => _lastAction.TryRemove(UserKey, out var action) ? action : null;

    /// <summary>Drops any pending undo, e.g. once it is no longer safe to reverse.</summary>
    public void Clear() => _lastAction.TryRemove(UserKey, out _);
}
