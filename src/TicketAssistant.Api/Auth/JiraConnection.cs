namespace TicketAssistant.Api.Auth;

/// <summary>
/// A user's live Jira connection, obtained through the OAuth popup and attached to their
/// <see cref="Session"/>. Held entirely server-side — the browser never sees these tokens,
/// only the opaque session id that points here. Refreshed in place by
/// <see cref="JiraAccessTokenResolver"/> when the access token nears expiry.
/// </summary>
/// <param name="AccessToken">Bearer token for api.atlassian.com (short-lived, ~1h).</param>
/// <param name="RefreshToken">Used to mint a fresh access token without another login.</param>
/// <param name="ExpiresAt">When <paramref name="AccessToken"/> stops being accepted.</param>
/// <param name="CloudId">Identifies the Jira site in the /ex/jira/{cloudId} API base.</param>
/// <param name="SiteUrl">The site's browser URL (e.g. https://acme.atlassian.net) for ticket links.</param>
/// <param name="AccountEmail">The connected account, shown in the "connected as" status.</param>
public sealed record JiraConnection(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string CloudId,
    string SiteUrl,
    string? AccountEmail);
