namespace TicketAssistant.Api.Auth;

/// <summary>
/// Resolves the current request's session from its <c>Authorization: Bearer &lt;id&gt;</c>
/// header. Registered as a singleton but reads <see cref="IHttpContextAccessor"/> on each call,
/// the same per-request-header trick the LLM's ChatClientFactory uses — so singletons like the
/// Jira provider and the forwarding handler can find "who is this" without being request-scoped.
/// </summary>
public sealed class CurrentSession(IHttpContextAccessor accessor, SessionStore store)
{
    private const string BearerPrefix = "Bearer ";

    /// <summary>The bearer session id on this request, or null if none was sent.</summary>
    public string? SessionId()
    {
        var header = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        return !string.IsNullOrEmpty(header) && header.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? header[BearerPrefix.Length..].Trim()
            : null;
    }

    /// <summary>The <see cref="Session"/> for this request, or null if unauthenticated/expired.</summary>
    public Session? Get() => store.TryGet(SessionId());
}
