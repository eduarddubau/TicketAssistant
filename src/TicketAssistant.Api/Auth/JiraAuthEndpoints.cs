using System.Text.Json;

namespace TicketAssistant.Api.Auth;

/// <summary>
/// The Jira OAuth popup endpoints. Mapped only when the Jira backend is active. The flow:
/// the SPA (already holding its bearer session) calls <c>/login</c> to get an authorize URL and
/// opens it in a popup; Atlassian sends the user back to <c>/callback</c>, which finishes the
/// token exchange, attaches the resulting <see cref="JiraConnection"/> to the session, and posts
/// a "connected" message back to the SPA before closing itself.
/// </summary>
public static class JiraAuthEndpoints
{
    public static void MapJiraAuth(this WebApplication app)
    {
        var group = app.MapGroup("/api/auth/jira");

        // Start a login: mint a CSRF state tied to this session and hand back the authorize URL.
        group.MapGet("/login", (CurrentSession current, SessionStore store, JiraOAuthClient oauth) =>
        {
            var sessionId = current.SessionId();
            if (store.TryGet(sessionId) is null)
            {
                return Results.Unauthorized(); // no session bearer — the SPA must mint one first
            }

            var state = store.NewOAuthState(sessionId!);
            return Results.Ok(new { authorizeUrl = oauth.BuildAuthorizeUrl(state) });
        });

        // Atlassian redirects the popup here with either a code or an error. This is a bare
        // browser navigation (no bearer header), so we recover the session from the state nonce.
        group.MapGet("/callback", async (
            string? code, string? state, string? error, string? error_description,
            SessionStore store, JiraOAuthClient oauth, AtlassianOAuthOptions options,
            ILoggerFactory loggers, CancellationToken ct) =>
        {
            var origin = options.FrontendOrigin;

            if (!string.IsNullOrEmpty(error))
            {
                return CallbackPage(origin, ok: false, message: error_description ?? error);
            }

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state))
            {
                return CallbackPage(origin, false, "Missing authorization code or state.");
            }

            var session = store.TryGet(store.ConsumeOAuthState(state));
            if (session is null)
            {
                return CallbackPage(origin, false, "This login session expired. Please try connecting again.");
            }

            try
            {
                var tokens = await oauth.ExchangeCodeAsync(code, ct);
                var (cloudId, siteUrl) = await oauth.GetAccessibleSiteAsync(tokens.AccessToken, ct);
                var label = await oauth.GetAccountLabelAsync(tokens.AccessToken, cloudId, ct);

                session.Jira = new JiraConnection(
                    tokens.AccessToken,
                    tokens.RefreshToken ?? "",
                    DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn),
                    cloudId,
                    siteUrl,
                    label);

                return CallbackPage(origin, true, null);
            }
            catch (Exception ex)
            {
                loggers.CreateLogger("JiraAuth").LogError(ex, "Jira OAuth callback failed");
                return CallbackPage(origin, false, "Could not complete the Jira connection. Please try again.");
            }
        });

        // Whether the current session is connected, and to what — powers the SPA's status badge.
        group.MapGet("/status", (CurrentSession current) =>
        {
            var jira = current.Get()?.Jira;
            return Results.Ok(new { connected = jira is not null, siteUrl = jira?.SiteUrl, accountEmail = jira?.AccountEmail });
        });

        // Disconnect: drop the Jira tokens from the session (the session itself lives on).
        group.MapPost("/logout", (CurrentSession current) =>
        {
            if (current.Get() is { } session)
            {
                session.Jira = null;
            }

            return Results.NoContent();
        });
    }

    // A tiny popup page that tells the opener (our SPA, by exact origin) how the login went and
    // then closes itself. Values are JSON-encoded so they can't break out of the script context.
    private static IResult CallbackPage(string origin, bool ok, string? message)
    {
        var payload = JsonSerializer.Serialize(new
        {
            type = ok ? "jira-connected" : "jira-error",
            message
        });
        var originJson = JsonSerializer.Serialize(origin);
        var heading = ok ? "Connected to Jira" : "Jira connection failed";

        var html = $$"""
            <!doctype html>
            <html>
            <head><meta charset="utf-8"><title>{{heading}}</title></head>
            <body style="font-family: system-ui, sans-serif; padding: 2rem; text-align: center;">
              <p>{{heading}}. You can close this window.</p>
              <script>
                try { window.opener && window.opener.postMessage({{payload}}, {{originJson}}); } catch (e) {}
                window.close();
              </script>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html");
    }
}
