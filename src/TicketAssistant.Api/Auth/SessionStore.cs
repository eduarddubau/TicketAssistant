using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace TicketAssistant.Api.Auth;

/// <summary>
/// One user's server-side session. <see cref="UserKey"/> is the display-name identity used to
/// scope tickets on the mock backend (the app has no real auth — see the PoC caveats);
/// <see cref="Jira"/> is their Jira connection once they've completed the OAuth popup, or null
/// until then. Mutable only via <see cref="SessionStore"/> so all access goes through one place.
/// </summary>
public sealed class Session(string userKey)
{
    public string UserKey { get; } = userKey;
    public JiraConnection? Jira { get; set; }
}

/// <summary>
/// In-memory registry of sessions, keyed by an unguessable id the browser carries as a bearer
/// token. Replaces the old spoofable X-User-Id header as the app's identity: you can't act as
/// another user without holding their (random, server-issued) session id. Also parks the
/// short-lived OAuth <c>state → sessionId</c> mapping while a login popup is in flight.
///
/// Singleton and in-memory, like the rest of this PoC — a restart drops every session.
/// </summary>
public sealed class SessionStore
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _pendingStates = new();

    /// <summary>Creates a session for <paramref name="userKey"/> and returns its bearer id.</summary>
    public string Create(string userKey)
    {
        var id = NewToken();
        _sessions[id] = new Session(userKey);
        return id;
    }

    public Session? TryGet(string? sessionId) =>
        sessionId is not null && _sessions.TryGetValue(sessionId, out var s) ? s : null;

    public void Remove(string sessionId) => _sessions.TryRemove(sessionId, out _);

    /// <summary>
    /// Records the CSRF <c>state</c> for an in-flight OAuth login and which session started it,
    /// so the callback (a bare browser navigation carrying no bearer) can find its way home.
    /// </summary>
    public string NewOAuthState(string sessionId)
    {
        var state = NewToken();
        _pendingStates[state] = sessionId;
        return state;
    }

    /// <summary>Redeems an OAuth state exactly once, returning the session that began the login.</summary>
    public string? ConsumeOAuthState(string state) =>
        _pendingStates.TryRemove(state, out var sessionId) ? sessionId : null;

    // 256 bits of URL-safe randomness — plenty to make a session id (or OAuth state)
    // infeasible to guess.
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}
