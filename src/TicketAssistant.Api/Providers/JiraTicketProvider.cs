using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TicketAssistant.Api.Auth;
using TicketAssistant.Api.Models;

namespace TicketAssistant.Api.Providers;

/// <summary>
/// Talks to real Jira Cloud over its REST v3 API — the production-grade sibling of
/// <see cref="HttpTicketProvider"/> (which targets this repo's mock). Selected by
/// <c>Tickets:Backend=Jira</c>.
///
/// Acts as *the logged-in user*, across *all their sites*. Every call resolves the current
/// request's OAuth token and the set of Jira sites it can reach from
/// <see cref="JiraAccessTokenResolver"/> (which reads the bearer session), then targets
/// <c>https://api.atlassian.com/ex/jira/{cloudId}</c> for a given site. Reads fan out over every
/// site and merge; writes route to whichever site hosts the target project or issue (found by
/// probing — relying on project keys being unique across a user's sites, which is the norm).
///
/// The provider stays a singleton — per-request identity comes from the resolver, like the LLM
/// client is chosen per request. Not connected? The resolver throws
/// <see cref="JiraNotConnectedException"/>, surfaced to the user as "connect Jira first".
///
/// Jira's model differs from the app's in a few ways this class hides behind the
/// <see cref="ITicketProvider"/> seam:
///   • bodies are ADF (Atlassian Document Format, a JSON doc), not plain strings;
///   • status can't be set directly — you POST a workflow *transition*;
///   • assignee is an opaque accountId, resolved via user search;
///   • our five statuses collapse onto Jira's three status *categories*, so status/priority are
///     filtered client-side after the JQL fetch.
/// </summary>
public sealed class JiraTicketProvider(
    HttpClient http,
    JiraAccessTokenResolver resolver,
    JiraOptions options,
    ILogger<JiraTicketProvider> logger) : ITicketProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private const string Fields =
        "summary,description,status,priority,assignee,reporter,labels,created,updated,duedate,issuelinks";

    public string Name => "jira";

    // ----- Reads -----

    public async Task<CanonicalTicket> GetTicketAsync(string ticketId, CancellationToken ct = default)
    {
        var access = await resolver.ResolveAsync(ct);
        foreach (var site in access.Sites)
        {
            using var doc = await GetJsonAsync(access, site.CloudId, $"/rest/api/3/issue/{ticketId}?fields={Fields}", ct);
            if (doc is not null) return MapIssue(doc.RootElement, site.SiteUrl);
        }
        throw new KeyNotFoundException($"No ticket '{ticketId}' in any connected Jira site.");
    }

    public Task<IReadOnlyList<CanonicalTicket>> SearchTicketsAsync(string query, CancellationToken ct = default)
        => RunJqlAsync(query, status: null, priority: null, ct);

    public Task<IReadOnlyList<CanonicalTicket>> ListTicketsAsync(
        TicketStatus? status = null, TicketPriority? priority = null, CancellationToken ct = default)
        => RunJqlAsync(query: null, status, priority, ct);

    public async Task<IReadOnlyList<TicketProject>> ListProjectsAsync(CancellationToken ct = default)
    {
        var access = await resolver.ResolveAsync(ct);
        var projects = new List<TicketProject>();
        foreach (var site in access.Sites)
        {
            try
            {
                using var doc = await GetJsonAsync(access, site.CloudId, "/rest/api/3/project/search?maxResults=50&orderBy=key", ct);
                if (doc is null || !doc.RootElement.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
                    continue;
                projects.AddRange(values.EnumerateArray()
                    .Select(v => new TicketProject(
                        v.GetProperty("key").GetString() ?? "", v.GetProperty("name").GetString() ?? "", site.Name, site.SiteUrl))
                    .Where(p => p.Key.Length > 0));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not list projects on Jira site {Site}", site.Name);
            }
        }
        return projects;
    }

    // Reads span every site the token can reach, merged and sorted. Narrowed only by reporter
    // (when scoped) and — for search — a free-text match; status/priority are filtered in memory,
    // since the app's five statuses don't line up 1:1 with Jira's workflow states.
    private async Task<IReadOnlyList<CanonicalTicket>> RunJqlAsync(
        string? query, TicketStatus? status, TicketPriority? priority, CancellationToken ct)
    {
        var access = await resolver.ResolveAsync(ct);

        var clauses = new List<string>();
        if (options.ScopeToReporter) clauses.Add("reporter = currentUser()");
        if (!string.IsNullOrWhiteSpace(query)) clauses.Add($"text ~ \"{EscapeJql(query)}\"");
        var jql = (clauses.Count > 0 ? string.Join(" AND ", clauses) + " " : "") + "ORDER BY created DESC";
        var url = $"/rest/api/3/search/jql?jql={Uri.EscapeDataString(jql)}&fields={Fields}&maxResults=100";

        var tickets = new List<CanonicalTicket>();
        foreach (var site in access.Sites)
        {
            try
            {
                using var doc = await GetJsonAsync(access, site.CloudId, url, ct);
                if (doc is null || !doc.RootElement.TryGetProperty("issues", out var issues) || issues.ValueKind != JsonValueKind.Array)
                    continue;
                tickets.AddRange(issues.EnumerateArray().Select(i => MapIssue(i, site.SiteUrl)));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not search tickets on Jira site {Site}", site.Name);
            }
        }

        return tickets
            .Where(t => status is null || t.Status == status)
            .Where(t => priority is null || t.Priority == priority)
            .OrderByDescending(t => t.CreatedAt)
            .ToList();
    }

    // ----- Writes -----

    public async Task<CanonicalTicket> CreateTicketAsync(CreateTicketRequest request, CancellationToken ct = default)
    {
        var access = await resolver.ResolveAsync(ct);

        // The target project comes from the request (the model or the confirmation card chooses
        // it, so tickets can land in any accessible project), falling back to the configured
        // default. With neither, ask rather than guess.
        var projectKey = !string.IsNullOrWhiteSpace(request.Project) ? request.Project.Trim()
            : !string.IsNullOrWhiteSpace(options.ProjectKey) ? options.ProjectKey
            : throw new InvalidOperationException(
                "No project was given for the new ticket. Ask the user which project it belongs in " +
                "(use list_projects to see the options).");

        var (cloudId, siteUrl) = await FindProjectSiteAsync(access, projectKey, ct);

        var fields = new Dictionary<string, object?>
        {
            ["project"] = new { key = projectKey },
            ["issuetype"] = new { name = options.IssueType },
            ["summary"] = request.Title,
            ["priority"] = new { name = MapPriorityToJira(request.Priority) },
            ["labels"] = request.Labels.Select(SanitizeLabel).ToArray(),
        };
        if (!string.IsNullOrWhiteSpace(request.Description))
            fields["description"] = AdfFromText(request.Description);
        if (!string.IsNullOrWhiteSpace(request.Assignee)
            && await ResolveAccountIdAsync(access, cloudId, request.Assignee, ct) is { } accountId)
            fields["assignee"] = new { accountId };

        using var created = await PostJsonAsync(access, cloudId, "/rest/api/3/issue", new { fields }, ct);
        var key = created.RootElement.GetProperty("key").GetString()!;

        // Best-effort "Relates" links (same site — Jira can't link across sites anyway).
        foreach (var related in request.RelatedTo)
        {
            try
            {
                using var link = await SendAsync(access, cloudId, HttpMethod.Post, "/rest/api/3/issueLink", new
                {
                    type = new { name = "Relates" },
                    inwardIssue = new { key },
                    outwardIssue = new { key = related }
                }, ct);
                link.EnsureSuccessStatusCode();
            }
            catch { /* linking is a nicety, not a requirement */ }
        }

        return await GetIssueAsync(access, cloudId, siteUrl, key, ct);
    }

    public async Task<CanonicalTicket> UpdateTicketStatusAsync(string ticketId, TicketStatus status, CancellationToken ct = default)
    {
        var access = await resolver.ResolveAsync(ct);
        var (cloudId, siteUrl) = await FindIssueSiteAsync(access, ticketId, ct);

        var transitionId = await FindTransitionIdAsync(access, cloudId, ticketId, status, ct)
            ?? throw new InvalidOperationException(
                $"No workflow transition on '{ticketId}' reaches a {status} state. " +
                "Jira only allows the moves its workflow defines from the ticket's current status.");

        using var response = await SendAsync(access, cloudId, HttpMethod.Post,
            $"/rest/api/3/issue/{ticketId}/transitions", new { transition = new { id = transitionId } }, ct);
        await EnsureSuccessAsync(response, ct);
        return await GetIssueAsync(access, cloudId, siteUrl, ticketId, ct);
    }

    public async Task<CanonicalTicket> AssignTicketAsync(string ticketId, string? assignee, CancellationToken ct = default)
    {
        var access = await resolver.ResolveAsync(ct);
        var (cloudId, siteUrl) = await FindIssueSiteAsync(access, ticketId, ct);

        string? accountId = null;
        if (!string.IsNullOrWhiteSpace(assignee))
            accountId = await ResolveAccountIdAsync(access, cloudId, assignee, ct)
                ?? throw new InvalidOperationException($"No Jira user matches '{assignee}'.");

        using var response = await SendAsync(access, cloudId, HttpMethod.Put,
            $"/rest/api/3/issue/{ticketId}/assignee", new { accountId }, ct);
        await EnsureSuccessAsync(response, ct);
        return await GetIssueAsync(access, cloudId, siteUrl, ticketId, ct);
    }

    public async Task<CanonicalTicket> SetDueDateAsync(string ticketId, DateTimeOffset? dueAt, CancellationToken ct = default)
    {
        var access = await resolver.ResolveAsync(ct);
        var (cloudId, siteUrl) = await FindIssueSiteAsync(access, ticketId, ct);

        var duedate = dueAt?.ToString("yyyy-MM-dd");
        using var response = await SendAsync(access, cloudId, HttpMethod.Put,
            $"/rest/api/3/issue/{ticketId}", new { fields = new { duedate } }, ct);
        await EnsureSuccessAsync(response, ct);
        return await GetIssueAsync(access, cloudId, siteUrl, ticketId, ct);
    }

    public async Task<TicketComment> AddCommentAsync(string ticketId, string body, CancellationToken ct = default)
    {
        var access = await resolver.ResolveAsync(ct);
        var (cloudId, _) = await FindIssueSiteAsync(access, ticketId, ct);

        using var created = await PostJsonAsync(access, cloudId, $"/rest/api/3/issue/{ticketId}/comment", new { body = AdfFromText(body) }, ct);
        return MapComment(created.RootElement);
    }

    public async Task DeleteTicketAsync(string ticketId, CancellationToken ct = default)
    {
        var access = await resolver.ResolveAsync(ct);
        var (cloudId, _) = await FindIssueSiteAsync(access, ticketId, ct);
        using var response = await SendAsync(access, cloudId, HttpMethod.Delete, $"/rest/api/3/issue/{ticketId}", null, ct);
        await EnsureSuccessAsync(response, ct);
    }

    public async Task DeleteLastCommentAsync(string ticketId, CancellationToken ct = default)
    {
        var access = await resolver.ResolveAsync(ct);
        var (cloudId, _) = await FindIssueSiteAsync(access, ticketId, ct);

        using var doc = await GetJsonAsync(access, cloudId, $"/rest/api/3/issue/{ticketId}/comment", ct);
        var last = doc?.RootElement.GetProperty("comments").EnumerateArray().LastOrDefault();
        if (last is not { ValueKind: JsonValueKind.Object }) return;

        var commentId = last.Value.GetProperty("id").GetString();
        using var response = await SendAsync(access, cloudId, HttpMethod.Delete, $"/rest/api/3/issue/{ticketId}/comment/{commentId}", null, ct);
        await EnsureSuccessAsync(response, ct);
    }

    // ----- Site routing -----

    // Which site hosts a given project (probes each until one has it). Relies on project keys
    // being unique across a user's sites — the normal case; a collision resolves to the first.
    private async Task<(string CloudId, string SiteUrl)> FindProjectSiteAsync(JiraAccess access, string projectKey, CancellationToken ct)
    {
        foreach (var site in access.Sites)
        {
            using var doc = await GetJsonAsync(access, site.CloudId, $"/rest/api/3/project/{Uri.EscapeDataString(projectKey)}", ct);
            if (doc is not null) return (site.CloudId, site.SiteUrl);
        }
        throw new InvalidOperationException($"No accessible Jira site has a project '{projectKey}'.");
    }

    // Which site holds a given issue (probes each with a cheap fields=summary read).
    private async Task<(string CloudId, string SiteUrl)> FindIssueSiteAsync(JiraAccess access, string ticketId, CancellationToken ct)
    {
        foreach (var site in access.Sites)
        {
            using var doc = await GetJsonAsync(access, site.CloudId, $"/rest/api/3/issue/{ticketId}?fields=summary", ct);
            if (doc is not null) return (site.CloudId, site.SiteUrl);
        }
        throw new KeyNotFoundException($"No ticket '{ticketId}' in any connected Jira site.");
    }

    private async Task<CanonicalTicket> GetIssueAsync(JiraAccess access, string cloudId, string siteUrl, string ticketId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(access, cloudId, $"/rest/api/3/issue/{ticketId}?fields={Fields}", ct)
                        ?? throw new KeyNotFoundException($"No ticket '{ticketId}' in Jira.");
        return MapIssue(doc.RootElement, siteUrl);
    }

    // ----- Helpers: HTTP (per site) -----

    private async Task<HttpResponseMessage> SendAsync(JiraAccess access, string cloudId, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, $"/ex/jira/{cloudId}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.AccessToken);
        if (body is not null)
            request.Content = JsonContent.Create(body, options: Json);
        return await http.SendAsync(request, ct);
    }

    private async Task<JsonDocument?> GetJsonAsync(JiraAccess access, string cloudId, string path, CancellationToken ct)
    {
        using var response = await SendAsync(access, cloudId, HttpMethod.Get, path, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureSuccessAsync(response, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task<JsonDocument> PostJsonAsync(JiraAccess access, string cloudId, string path, object body, CancellationToken ct)
    {
        using var response = await SendAsync(access, cloudId, HttpMethod.Post, path, body, ct);
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

    // Resolves a display name or email to an accountId via user search on a specific site.
    private async Task<string?> ResolveAccountIdAsync(JiraAccess access, string cloudId, string query, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(access, cloudId, $"/rest/api/3/user/search?query={Uri.EscapeDataString(query)}", ct);
        var first = doc?.RootElement.EnumerateArray().FirstOrDefault();
        return first is { ValueKind: JsonValueKind.Object } && first.Value.TryGetProperty("accountId", out var id)
            ? id.GetString()
            : null;
    }

    // Picks the workflow transition whose target status best matches the desired canonical status.
    private async Task<string?> FindTransitionIdAsync(JiraAccess access, string cloudId, string ticketId, TicketStatus target, CancellationToken ct)
    {
        using var doc = await GetJsonAsync(access, cloudId, $"/rest/api/3/issue/{ticketId}/transitions", ct);
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
    // top-level blocks (paragraphs, list items…) with newlines.
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
