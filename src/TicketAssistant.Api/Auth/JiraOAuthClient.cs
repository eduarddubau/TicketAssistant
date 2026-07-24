using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;

namespace TicketAssistant.Api.Auth;

/// <summary>
/// Speaks the Atlassian OAuth 2.0 (3LO) protocol: builds the authorize URL the popup opens,
/// exchanges the returned code for tokens (and refreshes them later), and looks up which Jira
/// site the tokens can reach. A thin wrapper over two Atlassian hosts — <c>auth.atlassian.com</c>
/// for the token dance and <c>api.atlassian.com</c> for accessible-resources — so it uses
/// absolute URLs rather than a single BaseAddress.
/// </summary>
public sealed class JiraOAuthClient(HttpClient http, AtlassianOAuthOptions options)
{
    private const string AuthorizeUrl = "https://auth.atlassian.com/authorize";
    private const string TokenUrl = "https://auth.atlassian.com/oauth/token";
    private const string ResourcesUrl = "https://api.atlassian.com/oauth/token/accessible-resources";

    /// <summary>The URL the login popup navigates to; <paramref name="state"/> is our CSRF nonce.</summary>
    public string BuildAuthorizeUrl(string state) => QueryHelpers.AddQueryString(AuthorizeUrl, new Dictionary<string, string?>
    {
        ["audience"] = "api.atlassian.com",
        ["client_id"] = options.ClientId,
        ["scope"] = options.Scopes,
        ["redirect_uri"] = options.RedirectUri,
        ["state"] = state,
        ["response_type"] = "code",
        ["prompt"] = "consent"
    });

    /// <summary>Trades the authorization code from the callback for access + refresh tokens.</summary>
    public Task<JiraTokenResponse> ExchangeCodeAsync(string code, CancellationToken ct) => PostTokenAsync(new
    {
        grant_type = "authorization_code",
        client_id = options.ClientId,
        client_secret = options.ClientSecret,
        code,
        redirect_uri = options.RedirectUri
    }, ct);

    /// <summary>Uses a refresh token to obtain a fresh access token (Atlassian rotates the refresh token too).</summary>
    public Task<JiraTokenResponse> RefreshAsync(string refreshToken, CancellationToken ct) => PostTokenAsync(new
    {
        grant_type = "refresh_token",
        client_id = options.ClientId,
        client_secret = options.ClientSecret,
        refresh_token = refreshToken
    }, ct);

    private async Task<JiraTokenResponse> PostTokenAsync(object body, CancellationToken ct)
    {
        var response = await http.PostAsJsonAsync(TokenUrl, body, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JiraTokenResponse>(ct)
               ?? throw new InvalidOperationException("Empty token response from Atlassian.");
    }

    /// <summary>
    /// Finds the Jira site the tokens grant access to. An account may have several; this PoC
    /// takes the first (see the plan's out-of-scope notes).
    /// </summary>
    public async Task<(string CloudId, string SiteUrl)> GetAccessibleSiteAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ResourcesUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var resources = await response.Content.ReadFromJsonAsync<List<AccessibleResource>>(ct) ?? [];
        var site = resources.FirstOrDefault()
                   ?? throw new InvalidOperationException("The Atlassian account has no accessible Jira sites.");
        return (site.Id, site.Url.TrimEnd('/'));
    }

    /// <summary>
    /// A human label for the connected account (email, or display name if the email is hidden by
    /// privacy settings), for the "connected as" status. Best-effort — returns null on any error.
    /// </summary>
    public async Task<string?> GetAccountLabelAsync(string accessToken, string cloudId, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://api.atlassian.com/ex/jira/{cloudId}/rest/api/3/myself");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            var me = await response.Content.ReadFromJsonAsync<MyselfDto>(ct);
            return me?.EmailAddress ?? me?.DisplayName;
        }
        catch
        {
            return null;
        }
    }

    private sealed record MyselfDto(
        [property: JsonPropertyName("emailAddress")] string? EmailAddress,
        [property: JsonPropertyName("displayName")] string? DisplayName);
}

/// <summary>Atlassian's snake_case token response. Refresh omits refresh_token when it isn't rotated.</summary>
public sealed record JiraTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn);

/// <summary>One entry from accessible-resources: the cloud id and the site's browser URL.</summary>
public sealed record AccessibleResource(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("name")] string? Name);
