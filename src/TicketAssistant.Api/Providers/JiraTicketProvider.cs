using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TicketAssistant.Api.Auth;
using TicketAssistant.Api.Models;

namespace TicketAssistant.Api.Providers;

/// <summary>
/// Talks to a real Jira Cloud site over its REST v3 API — the production-grade sibling of
/// <see cref="HttpTicketProvider"/> (which targets this repo's mock). Selected by
/// <c>Tickets:Backend=Jira</c>.
///
/// Acts as *the logged-in user*: rather than one shared credential, every call resolves the
/// current request's OAuth access token and cloud id from <see cref="JiraAccessTokenResolver"/>
/// (which reads the bearer session) and targets <c>https://api.atlassian.com/ex/jira/{cloudId}</c>.
/// The provider stays a singleton — the per-request identity comes from the resolver, exactly
/// like the LLM client is chosen per request. Not connected yet? The resolver throws
/// <see cref="JiraNotConnectedException"/>, which surfaces to the user as "connect Jira first".
///
/// Jira's model differs from the app's in a few ways this class hides behind the
/// <see cref="ITicketProvider"/> seam:
///   • bodies are ADF (Atlassian Document Format, a JSON doc), not plain strings — see
///     <see cref="AdfFromText"/> / <see cref="TextFromAdf"/>;
///   • status can't be set directly — you POST a workflow *transition*, so
///     <see cref="UpdateTicketStatusAsync"/> looks up an available transition to the target;
///   • assignee is an opaque accountId, so a name/email is resolved via user search first;
///   • our five statuses collapse onto Jira's three status *categories*, so list/search fetch
///     the project's tickets and filter client-side rather than encoding statuses into JQL.
/// </summary>
public sealed class JiraTicketProvider(HttpClient http, JiraAccessTokenResolver resolver, JiraOptions options)
    : ITicketProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The fields every read asks Jira for. Requesting them explicitly keeps responses small and
    // means the /search/jql endpoint returns full issues instead of bare keys.
    private const string Fields =
        "summary,description,status,priority,assignee,reporter,labels,created,updated,duedate,issuelinks";

    public string Name => "jira";

    // ----- Reads -----

    public async Task<CanonicalTicket> GetTicketAsync(string ticketId, CancellationToken ct = default)
    {
        var (_, _, siteUrl) = await resolver.ResolveAsync(ct);
        using var doc = await GetJsonAsync($"/rest/api/3/issue/{ticketId}?fields={Fields}", ct)
                        ?? throw new KeyNotFoundException($"No ticket '{ticketId}' in Jira.");
        return MapIssue(doc.RootElement, siteUrl);
    }

    public Task<IReadOnlyList<CanonicalTicket>> SearchTicketsAsync(string query, CancellationToken ct = default)
        => RunJqlAsync(query, status: null, priority: null, ct);

    public Task<IReadOnlyList<CanonicalTicket>> ListTicketsAsync(
        TicketStatus? status = null, TicketPriority? priority = null, CancellationToken ct = default)
        => RunJqlAsync(query: null, status, priority, ct);

    // Both list and search route through here. JQL narrows to the project (and reporter, when
    // scoped) and — for search — a free-text match; status/priority are filtered in memory
    // afterwards, since the app's five statuses don't line up 1:1 with Jira's workflow states.
    private async Task<IReadOnlyList<CanonicalTicket>> RunJqlAsync(
        string? query, TicketStatus? status, TicketPriority? priority, CancellationToken ct)
    {
        var (_, _, siteUrl) = await resolver.ResolveAsync(ct);

        var clauses = new List<string> { $"project = \"{options.ProjectKey}\"" };
        if (options.ScopeToReporter) clauses.Add("reporter = currentUser()");
        if (!string.IsNullOrWhiteSpace(query)) clauses.Add($"text ~ \"{EscapeJql(query)}\"");
        var jql = string.Join(" AND ", clauses) + " ORDER BY created DESC";

        var url = $"/rest/api/3/search/jql?jql={Uri.EscapeDataString(jql)}&fields={Fields}&maxResults=100";
        using var doc = await GetJsonAsync(url, ct);
        if (doc is null) return [];

        var tickets = doc.RootElement.GetProperty("issues").EnumerateArray()
            .Select(i => MapIssue(i, siteUrl))
            .Where(t => status is null || t.Status == status)
            .Where(t => priority is null || t.Priority == priority)
            .ToList();
        return tickets;
    }

    // ----- Writes -----

    public async Task<CanonicalTicket> CreateTicketAsync(CreateTicketRequest request, CancellationToken ct = default)
    {
        var fields = new Dictionary<string, object?>
        {
            ["project"] = new { key = options.ProjectKey },
            ["issuetype"] = new { name = options.IssueType },
            ["summary"] = request.Title,
            ["priority"] = new { name = MapPriorityToJira(request.Priority) },
            ["labels"] = request.Labels.Select(SanitizeLabel).ToArray(),
        };
        if (!string.IsNullOrWhiteSpace(request.Description))
            fields["description"] = AdfFromText(request.Description);
        if (!string.IsNullOrWhiteSpace(request.Assignee)
            && await ResolveAccountIdAsync(request.Assignee, ct) is { } accountId)
            fields["assignee"] = new { accountId };

        using var created = await PostJsonAsync("/rest/api/3/issue", new { fields }, ct);
        var key = created.RootElement.GetProperty("key").GetString()!;

        // Jira has no "related to" field; model it as best-effort "Relates" issue links so a
        // near-duplicate stays connected. A missing link type or permission shouldn't fail the
        // create the user already approved, so each link is swallowed on error.
        foreach (var related in request.RelatedTo)
        {
            try
            {
                using var link = await SendAsync(HttpMethod.Post, "/rest/api/3/issueLink", new
                {
                    type = new { name = "Relates" },
                    inwardIssue = new { key },
                    outwardIssue = new { key = related }
                }, ct);
                link.EnsureSuccessStatusCode();
            }
            catch { /* linking is a nicety, not a requirement */ }
        }

        return await GetTicketAsync(key, ct);
    }

    public async Task<CanonicalTicket> UpdateTicketStatusAsync(string ticketId, TicketStatus status, CancellationToken ct = default)
    {
        var transitionId = await FindTransitionIdAsync(ticketId, status, ct)
            ?? throw new InvalidOperationException(
                $"No workflow transition on '{ticketId}' reaches a {status} state. " +
                "Jira only allows the moves its workflow defines from the ticket's current status.");

        using var response = await SendAsync(HttpMethod.Post,
            $"/rest/api/3/issue/{ticketId}/transitions", new { transition = new { id = transitionId } }, ct);
        await EnsureSuccessAsync(response, ct);
        return await GetTicketAsync(ticketId, ct);
    }

    public async Task<CanonicalTicket> AssignTicketAsync(string ticketId, string? assignee, CancellationToken ct = default)
    {
        // A null accountId unassigns; a non-empty name/email must resolve to a real account.
        string? accountId = null;
        if (!string.IsNullOrWhiteSpace(assignee))
            accountId = await ResolveAccountIdAsync(assignee, ct)
                ?? throw new InvalidOperationException($"No Jira user matches '{assignee}'.");

        using var response = await SendAsync(HttpMethod.Put,
            $"/rest/api/3/issue/{ticketId}/assignee", new { accountId }, ct);
        await EnsureSuccessAsync(response, ct);
        return await GetTicketAsync(ticketId, ct);
    }

    public async Task<CanonicalTicket> SetDueDateAsync(string ticketId, DateTimeOffset? dueAt, CancellationToken ct = default)
    {
        // Jira's duedate is a bare calendar date; null clears it.
        var duedate = dueAt?.ToString("yyyy-MM-dd");
        using var response = await SendAsync(HttpMethod.Put,
            $"/rest/api/3/issue/{ticketId}", new { fields = new { duedate } }, ct);
        await EnsureSuccessAsync(response, ct);
        return await GetTicketAsync(ticketId, ct);
    }

    public async Task<TicketComment> AddCommentAsync(string ticketId, string body, CancellationToken ct = default)
    {
        using var created = await PostJsonAsync($"/rest/api/3/issue/{ticketId}/comment", new { body = AdfFromText(body) }, ct);
        return MapComment(created.RootElement);
    }

    public async Task DeleteTicketAsync(string ticketId, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Delete, $"/rest/api/3/issue/{ticketId}", null, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task DeleteLastCommentAsync(string ticketId, CancellationToken ct = default)
    {
        // Jira has no "delete last" — read the comments (ascending) and delete the final one.
        using var doc = await GetJsonAsync($"/rest/api/3/issue/{ticketId}/comment", ct);
        var last = doc?.RootElement.GetProperty("comments").EnumerateArray().LastOrDefault();
        if (last is not { ValueKind: JsonValueKind.Object }) return;

        var commentId = last.Value.GetProperty("id").GetString();
        using var response = await SendAsync(HttpMethod.Delete, $"/rest/api/3/issue/{ticketId}/comment/{commentId}", null, ct);
        await EnsureSuccessAsync(response, ct);
    }

    // ----- Helpers: HTTP -----

    // Resolves the current session's access token + cloud id, then builds an authenticated
    // request against /ex/jira/{cloudId}{path}. This is the one place per-user identity enters
    // the transport, so every call above stays oblivious to whose Jira it's hitting.
    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var (token, cloudId, _) = await resolver.ResolveAsync(ct);
        using var request = new HttpRequestMessage(method, $"/ex/jira/{cloudId}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        return await http.SendAsync(request, ct);
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task<JsonDocument> PostJsonAsync(string path, object body, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, ct);
        await EnsureSuccessAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    // Surfaces Jira's own error text instead of a bare status code, so a bad field or missing
    // permission shows the user something actionable rather than "Response status code 400".
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Jira returned {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }

    // Resolves a display name or email to an accountId via user search; null when nothing matches.
    private async Task<string?> ResolveAccountIdAsync(string query, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"/rest/api/3/user/search?query={Uri.EscapeDataString(query)}", ct);
        var first = doc?.RootElement.EnumerateArray().FirstOrDefault();
        return first is { ValueKind: JsonValueKind.Object } && first.Value.TryGetProperty("accountId", out var id)
            ? id.GetString()
            : null;
    }

    // Picks the workflow transition whose target status best matches the desired canonical
    // status, or null when none is available from the ticket's current state.
    private async Task<string?> FindTransitionIdAsync(string ticketId, TicketStatus target, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"/rest/api/3/issue/{ticketId}/transitions", ct);
        if (doc is null) return null;

        string? bestId = null;
        var bestScore = 0;
        foreach (var t in doc.RootElement.GetProperty("transitions").EnumerateArray())
        {
            var to = t.GetProperty("to");
            var toName = to.GetProperty("name").GetString() ?? "";
            var toCategory = to.GetProperty("statusCategory").GetProperty("key").GetString() ?? "";
            var score = ScoreTransition(target, toName, toCategory);
            if (score > bestScore)
            {
                bestScore = score;
                bestId = t.GetProperty("id").GetString();
            }
        }
        return bestId;
    }

    // ----- Helpers: mapping -----

    private CanonicalTicket MapIssue(JsonElement issue, string siteUrl)
    {
        var f = issue.GetProperty("fields");
        var key = issue.GetProperty("key").GetString()!;
        return new CanonicalTicket
        {
            Id = key,
            ProviderName = Name,
            Title = f.GetProperty("summary").GetString() ?? "",
            Description = f.TryGetProperty("description", out var d) ? TextFromAdf(d) : null,
            Status = MapStatusFromJira(f.GetProperty("status")),
            Priority = MapPriorityFromJira(GetNested(f, "priority", "name")),
            Assignee = GetNested(f, "assignee", "displayName"),
            Reporter = GetNested(f, "reporter", "displayName"),
            Labels = f.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : [],
            RelatedTo = ExtractLinkedKeys(f, key),
            CreatedAt = GetDate(f, "created") ?? DateTimeOffset.UtcNow,
            UpdatedAt = GetDate(f, "updated"),
            DueAt = GetDate(f, "duedate"),
            Url = new Uri($"{siteUrl}/browse/{key}")
        };
    }

    private static TicketComment MapComment(JsonElement comment) => new()
    {
        Author = GetNested(comment, "author", "displayName") ?? "unknown",
        Body = comment.TryGetProperty("body", out var b) ? TextFromAdf(b) : "",
        CreatedAt = comment.TryGetProperty("created", out var c) && c.TryGetDateTimeOffset(out var dt)
            ? dt : DateTimeOffset.UtcNow
    };

    // Jira workflows are per-project and freely named, so read status by its category first
    // (new / indeterminate / done — the one stable axis), refining with the status name where
    // the app draws finer distinctions than a category can (Blocked, Closed vs Resolved).
    private static TicketStatus MapStatusFromJira(JsonElement statusField)
    {
        var name = (statusField.GetProperty("name").GetString() ?? "").ToLowerInvariant();
        var category = statusField.GetProperty("statusCategory").GetProperty("key").GetString() ?? "";

        if (name.Contains("block")) return TicketStatus.Blocked;
        return category switch
        {
            "new" => TicketStatus.Open,
            "indeterminate" => TicketStatus.InProgress,
            "done" => name.Contains("close") ? TicketStatus.Closed : TicketStatus.Resolved,
            _ => TicketStatus.Open
        };
    }

    // Higher score = better target for `target`. 0 means "don't use this transition".
    private static int ScoreTransition(TicketStatus target, string toName, string toCategory)
    {
        toName = toName.ToLowerInvariant();
        return target switch
        {
            TicketStatus.Open => toCategory == "new" ? 2 : 0,
            TicketStatus.InProgress => (toName.Contains("progress") ? 2 : 0) + (toCategory == "indeterminate" ? 1 : 0),
            TicketStatus.Blocked => toName.Contains("block") ? 3 : 0,
            TicketStatus.Resolved => (toName.Contains("resolve") || toName.Contains("done") ? 2 : 0)
                                     + (toCategory == "done" && !toName.Contains("close") ? 1 : 0),
            TicketStatus.Closed => (toName.Contains("close") ? 2 : 0) + (toCategory == "done" ? 1 : 0),
            _ => 0
        };
    }

    private static string MapPriorityToJira(TicketPriority p) => p switch
    {
        TicketPriority.Urgent => "Highest",
        TicketPriority.High => "High",
        TicketPriority.Medium => "Medium",
        TicketPriority.Low => "Low",
        _ => "Medium"
    };

    private static TicketPriority MapPriorityFromJira(string? name) => name switch
    {
        "Highest" => TicketPriority.Urgent,
        "High" => TicketPriority.High,
        "Medium" => TicketPriority.Medium,
        "Low" or "Lowest" => TicketPriority.Low,
        _ => TicketPriority.Medium
    };

    // Jira labels can't contain whitespace; collapse runs to hyphens so a multi-word label
    // survives instead of being rejected.
    private static string SanitizeLabel(string label) =>
        string.Join('-', label.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Pulls the other end's key out of each issue link (a link stores the *other* issue under
    // inwardIssue or outwardIssue depending on direction).
    private static IReadOnlyList<string> ExtractLinkedKeys(JsonElement fields, string selfKey)
    {
        if (!fields.TryGetProperty("issuelinks", out var links) || links.ValueKind != JsonValueKind.Array)
            return [];

        var keys = new List<string>();
        foreach (var link in links.EnumerateArray())
        {
            foreach (var side in (ReadOnlySpan<string>)["inwardIssue", "outwardIssue"])
                if (link.TryGetProperty(side, out var issue) && issue.TryGetProperty("key", out var k)
                    && k.GetString() is { } key && key != selfKey)
                    keys.Add(key);
        }
        return keys;
    }

    // ----- Helpers: ADF (Atlassian Document Format) -----

    // Wraps plain text in the minimal ADF document Jira accepts for description/comment bodies:
    // one paragraph per line (blank lines become empty paragraphs, preserving spacing).
    private static object AdfFromText(string text)
    {
        var paragraphs = text.Replace("\r\n", "\n").Split('\n').Select(line => line.Length == 0
            ? new { type = "paragraph", content = Array.Empty<object>() }
            : new { type = "paragraph", content = new object[] { new { type = "text", text = line } } });
        return new { type = "doc", version = 1, content = paragraphs.ToArray() };
    }

    // Flattens an ADF document back to plain text by collecting every "text" node, joining the
    // top-level blocks (paragraphs, list items…) with newlines. Good enough to show the model
    // and the user what a ticket says without rendering rich formatting.
    private static string TextFromAdf(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return "";
        if (!node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";

        var lines = new List<string>();
        foreach (var block in content.EnumerateArray())
            lines.Add(CollectText(block));
        return string.Join("\n", lines).Trim();
    }

    private static string CollectText(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return "";
        if (node.TryGetProperty("type", out var type) && type.GetString() == "text"
            && node.TryGetProperty("text", out var text))
            return text.GetString() ?? "";

        if (!node.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return "";
        return string.Concat(content.EnumerateArray().Select(CollectText));
    }

    // ----- Helpers: JSON navigation -----

    private static string? GetNested(JsonElement parent, string obj, string prop) =>
        parent.TryGetProperty(obj, out var o) && o.ValueKind == JsonValueKind.Object
            && o.TryGetProperty(prop, out var v) ? v.GetString() : null;

    // Handles both Jira's full timestamps (created/updated, ISO 8601 with offset) and its
    // date-only fields (duedate, "yyyy-MM-dd") — the latter don't satisfy TryGetDateTimeOffset,
    // so fall back to a plain parse that does accept a bare date.
    private static DateTimeOffset? GetDate(JsonElement parent, string prop)
    {
        if (!parent.TryGetProperty(prop, out var v) || v.ValueKind != JsonValueKind.String) return null;
        if (v.TryGetDateTimeOffset(out var dt)) return dt;
        return DateTimeOffset.TryParse(v.GetString(), out var parsed) ? parsed : null;
    }

    private static string EscapeJql(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
