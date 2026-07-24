namespace TicketAssistant.Api.Auth;

/// <summary>One Jira site (a.k.a. workspace) the token can reach: its cloud id for the
/// <c>/ex/jira/{cloudId}</c> API base, and its browser URL for ticket links.</summary>
public sealed record JiraSite(string CloudId, string SiteUrl, string Name);

/// <summary>
/// A user's live Jira connection, obtained through the OAuth popup and attached to their
/// <see cref="Session"/>. Held entirely server-side — the browser never sees these tokens,
/// only the opaque session id that points here. Refreshed in place by
/// <see cref="JiraAccessTokenResolver"/> when the access token nears expiry.
///
/// With Account-level access the token can reach several <see cref="Sites"/>; the provider reads
/// across all of them and routes writes to whichever site hosts the target project/issue.
/// </summary>
/// <param name="AccessToken">Bearer token for api.atlassian.com (short-lived, ~1h).</param>
/// <param name="RefreshToken">Used to mint a fresh access token without another login.</param>
/// <param name="ExpiresAt">When <paramref name="AccessToken"/> stops being accepted.</param>
/// <param name="Sites">Every Jira site the token can reach.</param>
/// <param name="AccountEmail">The connected account, shown in the "connected as" status.</param>
public sealed record JiraConnection(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<JiraSite> Sites,
    string? AccountEmail);
