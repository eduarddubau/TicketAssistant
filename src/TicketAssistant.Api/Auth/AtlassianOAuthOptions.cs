namespace TicketAssistant.Api.Auth;

/// <summary>
/// The registered Atlassian OAuth 2.0 (3LO) app's settings, bound from the <c>Atlassian:*</c>
/// config section (env vars <c>Atlassian__*</c>). Create the app at
/// developer.atlassian.com → your apps → OAuth 2.0 (3LO); the client id/secret are required and
/// have no defaults.
/// </summary>
public sealed class AtlassianOAuthOptions
{
    public string ClientId { get; init; } = "";
    public string ClientSecret { get; init; } = "";

    /// <summary>Must match a callback URL registered on the app. Localhost is allowed for dev.</summary>
    public string RedirectUri { get; init; } = "http://localhost:5080/api/auth/jira/callback";

    /// <summary>
    /// Space-separated scopes. <c>offline_access</c> is what yields a refresh token; the rest
    /// cover reading/writing issues and looking up users (for assignment).
    /// </summary>
    public string Scopes { get; init; } = "read:jira-work write:jira-work read:jira-user offline_access";

    /// <summary>
    /// The SPA's origin, used as the <c>postMessage</c> target when the popup reports success —
    /// so the "connected" signal only goes to our own front-end, never an arbitrary opener.
    /// </summary>
    public string FrontendOrigin { get; init; } = "http://localhost:4200";
}
