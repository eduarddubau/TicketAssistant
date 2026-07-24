namespace TicketAssistant.Api.Auth;

/// <summary>Thrown when a Jira operation is attempted with no connected account on the session.</summary>
public sealed class JiraNotConnectedException()
    : Exception("Not connected to Jira. Ask the user to connect their Jira account before doing this.");

/// <summary>A valid access token plus the sites it can reach, for one request.</summary>
public sealed record JiraAccess(string AccessToken, IReadOnlyList<JiraSite> Sites);

/// <summary>
/// Hands the Jira provider a usable access token for the current request's session, transparently
/// refreshing it when it's about to expire. This is the seam that lets a singleton provider act
/// as whichever user is calling: it reads the session per call (via <see cref="CurrentSession"/>)
/// rather than being bound to one identity.
/// </summary>
public sealed class JiraAccessTokenResolver(
    CurrentSession current,
    JiraOAuthClient oauth,
    ILogger<JiraAccessTokenResolver> logger)
{
    // Refresh a little before the real expiry so a token doesn't lapse mid-request.
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    // Serialises refreshes so several tool calls in one turn don't each spend the (rotating)
    // refresh token. Refreshes are rare, so one shared lock is fine for this PoC.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>
    /// The current session's Jira access token and the sites it can reach.
    /// Throws <see cref="JiraNotConnectedException"/> when the session has no Jira connection.
    /// </summary>
    public async Task<JiraAccess> ResolveAsync(CancellationToken ct)
    {
        var session = current.Get() ?? throw new JiraNotConnectedException();
        var conn = session.Jira ?? throw new JiraNotConnectedException();

        if (!IsExpiring(conn))
        {
            return new JiraAccess(conn.AccessToken, conn.Sites);
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            // Re-read under the lock: another request may have just refreshed it.
            conn = session.Jira ?? throw new JiraNotConnectedException();
            if (!IsExpiring(conn))
            {
                return new JiraAccess(conn.AccessToken, conn.Sites);
            }

            var refreshed = await oauth.RefreshAsync(conn.RefreshToken, ct);
            var updated = conn with
            {
                AccessToken = refreshed.AccessToken,
                // Atlassian rotates refresh tokens, but only returns a new one when it rotates —
                // keep the old one if this response didn't carry a replacement.
                RefreshToken = refreshed.RefreshToken ?? conn.RefreshToken,
                ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(refreshed.ExpiresIn)
            };
            session.Jira = updated;
            logger.LogInformation("Refreshed the Jira access token for the current session");
            return new JiraAccess(updated.AccessToken, updated.Sites);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static bool IsExpiring(JiraConnection conn) =>
        conn.ExpiresAt - ExpirySkew <= DateTimeOffset.UtcNow;
}
