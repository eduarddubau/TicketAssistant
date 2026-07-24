using TicketAssistant.Api.Auth;

namespace TicketAssistant.Api.Providers;

/// <summary>
/// Puts the current session's user identity onto every outbound call HttpTicketProvider makes,
/// so the mock can scope tickets to the user who is chatting. The identity comes from the
/// server-side <see cref="Session.UserKey"/> (resolved from the request's bearer token), not from
/// a client-supplied header — so a caller can't scope to another user without their session id.
/// The mock still reads a plain X-User-Id, so that's what we forward. Registered on the
/// provider's typed HttpClient.
/// </summary>
public sealed class UserIdForwardingHandler(CurrentSession current) : DelegatingHandler
{
    public const string UserHeader = "X-User-Id";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (current.Get()?.UserKey is { Length: > 0 } userKey)
        {
            request.Headers.Remove(UserHeader);
            request.Headers.Add(UserHeader, userKey);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
