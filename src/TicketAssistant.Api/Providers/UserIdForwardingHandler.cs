namespace TicketAssistant.Api.Providers;

/// <summary>
/// Copies the X-User-Id header from the current inbound request onto every outbound call
/// HttpTicketProvider makes to the ticketing system, so the mock can scope tickets to the
/// user who is chatting. Registered on the provider's typed HttpClient.
/// </summary>
public sealed class UserIdForwardingHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    public const string UserHeader = "X-User-Id";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var userId = accessor.HttpContext?.Request.Headers[UserHeader].ToString();
        if (!string.IsNullOrWhiteSpace(userId))
        {
            request.Headers.Remove(UserHeader);
            request.Headers.Add(UserHeader, userId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
