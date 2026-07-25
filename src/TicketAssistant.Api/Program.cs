// Application entry point. This file has two halves:
//   1. Service registration (dependency injection) — wire up the LLM client, the ticket
//      backend, and the orchestration pieces.
//   2. The HTTP pipeline — middleware and the endpoints the console calls.
// .NET "top-level statements": the code below runs directly, no Main method needed.
using System.Text.Json;
using Microsoft.Extensions.AI;
using Scalar.AspNetCore;
using TicketAssistant.Api.Auth;
using TicketAssistant.Api.Models;
using TicketAssistant.Api.Orchestration;
using TicketAssistant.Api.Providers;

var builder = WebApplication.CreateBuilder(args);

// The Angular console (the front-end, src/web) is served from its own origin, so it needs CORS
// to call this API. It's the only client — nothing is served from here but the API itself.
const string AngularDevCorsPolicy = "AngularDev";
builder.Services.AddCors(options => options.AddPolicy(AngularDevCorsPolicy, policy =>
    policy.WithOrigins("http://localhost:4200").AllowAnyMethod().AllowAnyHeader()));

builder.Services.AddOpenApi();                              // OpenAPI doc for the Scalar UI
builder.Services.AddHttpContextAccessor();                  // lets the handler below read the request
builder.Services.AddTransient<UserIdForwardingHandler>();   // forwards the session's user id to the mock

// Identity: the browser carries an opaque, server-issued bearer session (minted at /api/session).
// SessionStore holds them; CurrentSession resolves "who is this request" from the bearer — the
// same per-request-header trick ChatClientFactory uses for the LLM. Replaces the old spoofable
// X-User-Id header, and is where a user's Jira OAuth tokens get attached once they log in.
builder.Services.AddSingleton<SessionStore>();
builder.Services.AddSingleton<CurrentSession>();

// Tickets:Backends lists the backends the assistant uses *at the same time* — any of "Http" (the
// TicketingMock service in this repo), "Jira" (real Jira Cloud), "InMemory" (in-process stub).
// When more than one is listed, a CompositeTicketProvider fans reads across all of them and routes
// each write to the backend that owns the target. (Legacy Tickets:Backend still works as a single.)
var backends = (builder.Configuration["Tickets:Backends"] ?? builder.Configuration["Tickets:Backend"] ?? "Http")
    .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();
bool UsesBackend(string name) => backends.Any(b => b.Equals(name, StringComparison.OrdinalIgnoreCase));
var jiraEnabled = UsesBackend("Jira");

// Register each requested backend as its concrete type; the composite (below) picks them up.
if (UsesBackend("Http"))
{
    var ticketsBaseUrl = builder.Configuration["Tickets:Http:BaseUrl"] ?? "http://localhost:5090";
    builder.Services.AddHttpClient<HttpTicketProvider>(c => c.BaseAddress = new Uri(ticketsBaseUrl))
        .AddHttpMessageHandler<UserIdForwardingHandler>();
}
if (UsesBackend("InMemory"))
{
    builder.Services.AddSingleton<InMemoryTicketProvider>();
}
if (jiraEnabled)
{
    builder.Services.AddSingleton(new JiraOptions
    {
        ProjectKey = builder.Configuration["Tickets:Jira:ProjectKey"] ?? "",
        IssueType = builder.Configuration["Tickets:Jira:IssueType"] ?? "Task",
        // ScopeToReporter is the old name for this, still honoured so an existing .env keeps working
        // — it now means "mine" as reporter *or* assignee, which is what people meant by it anyway.
        ScopeToCurrentUser = !bool.TryParse(
            builder.Configuration["Tickets:Jira:ScopeToCurrentUser"]
            ?? builder.Configuration["Tickets:Jira:ScopeToReporter"], out var s) || s
    });
    var atlassian = new AtlassianOAuthOptions
    {
        ClientId = builder.Configuration["Atlassian:ClientId"] ?? "",
        ClientSecret = builder.Configuration["Atlassian:ClientSecret"] ?? "",
        RedirectUri = builder.Configuration["Atlassian:RedirectUri"] ?? "http://localhost:5080/api/auth/jira/callback",
        Scopes = builder.Configuration["Atlassian:Scopes"] ?? "read:jira-work write:jira-work read:jira-user offline_access",
        FrontendOrigin = builder.Configuration["Atlassian:FrontendOrigin"] ?? "http://localhost:4200"
    };
    if (string.IsNullOrWhiteSpace(atlassian.ClientId) || string.IsNullOrWhiteSpace(atlassian.ClientSecret))
    {
        throw new InvalidOperationException(
            "The Jira backend needs Atlassian:ClientId and Atlassian:ClientSecret " +
            "(env: ATLASSIAN_CLIENT_ID / ATLASSIAN_CLIENT_SECRET).");
    }

    builder.Services.AddSingleton(atlassian);
    builder.Services.AddHttpClient<JiraOAuthClient>();          // OAuth token dance + accessible-resources
    builder.Services.AddSingleton<JiraAccessTokenResolver>();   // per-request token, refreshed as needed
    // No default auth header — the provider adds each user's bearer token per request and targets
    // /ex/jira/{cloudId} under this base.
    builder.Services.AddHttpClient<JiraTicketProvider>(c => c.BaseAddress = new Uri("https://api.atlassian.com"));
}

// The single ITicketProvider the loop/tools depend on: one child directly, or a composite of all.
builder.Services.AddSingleton<ITicketProvider>(sp =>
{
    var children = new List<ITicketProvider>();
    if (UsesBackend("Http")) children.Add(sp.GetRequiredService<HttpTicketProvider>());
    if (UsesBackend("InMemory")) children.Add(sp.GetRequiredService<InMemoryTicketProvider>());
    if (jiraEnabled) children.Add(sp.GetRequiredService<JiraTicketProvider>());
    if (children.Count == 0)
        throw new InvalidOperationException("No ticket backends configured — set Tickets:Backends (e.g. \"Http Jira\").");
    return children.Count == 1
        ? children[0]
        : new CompositeTicketProvider(children, sp.GetRequiredService<ILogger<CompositeTicketProvider>>());
});

// The chat client is created per request by ChatClientFactory, so the provider/model can be
// switched with the X-Llm-Provider / X-Llm-Model headers instead of only via configuration.
// Deliberately no .UseFunctionInvocation() — OrchestrationLoop drives the tool-call loop by
// hand so it can intercept writes before they run.
builder.Services.AddSingleton<ChatClientFactory>();

// Build the tool menu from whichever provider was registered above, then the loop (which
// takes the chat client + tools + provider) and the conversation memory. All singletons:
// one shared instance for the app's lifetime.
builder.Services.AddSingleton<UndoStore>();
// What this request may list, from the console's two filters: which kinds of item
// (X-Item-Types) and which backends (X-Sources).
builder.Services.AddSingleton<ItemTypeScope>();
builder.Services.AddSingleton<SourceScope>();
builder.Services.AddSingleton(sp =>
    TicketTools.Build(
        sp.GetRequiredService<ITicketProvider>(),
        sp.GetRequiredService<UndoStore>(),
        sp.GetRequiredService<ItemTypeScope>(),
        sp.GetRequiredService<SourceScope>()));
builder.Services.AddSingleton<OrchestrationLoop>();
builder.Services.AddSingleton<ConversationStore>();

// Reads the X-Debug header, so the loop knows whether to also stream its inner workings to the
// console's debug console. Off unless a request asks for it.
builder.Services.AddSingleton<DebugTrace>();

var app = builder.Build();

// ----- HTTP pipeline -----
app.UseCors(AngularDevCorsPolicy);

// No static files here: this service is the API. The front-end is the Angular console in
// src/web (http://localhost:4200), which calls it cross-origin under the policy above.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference(); // http://localhost:<port>/scalar/v1

    // Nothing lives at the root anymore, so send anyone poking at this port to the API
    // reference — the useful thing here. The app itself is the console on :4200.
    app.MapGet("/", () => Results.Redirect("/scalar/v1"));
}

// The Jira OAuth popup endpoints only exist when the Jira backend is in the mix.
if (jiraEnabled)
{
    app.MapJiraAuth();
}

// Mint a bearer session for the browser to carry (Authorization: Bearer). The optional name
// becomes the mock's per-user scope key; for Jira, identity is really the account logged in
// later via OAuth, so the name is just a label. Unauthenticated by design — a PoC (see caveats).
app.MapPost("/api/session", (CreateSessionRequest? body, SessionStore sessions) =>
{
    var userKey = string.IsNullOrWhiteSpace(body?.Name) ? "guest" : body.Name.Trim();
    return Results.Ok(new { sessionId = sessions.Create(userKey), userKey });
});

// Every project across every active backend — the create card's "which project?" picker and the
// assistant's list_projects tool both read this. Resilient: a not-yet-connected Jira just
// contributes nothing rather than erroring the whole list.
app.MapGet("/api/projects", async (ITicketProvider provider, CancellationToken ct) =>
{
    try { return Results.Ok(await provider.ListProjectsAsync(ct)); }
    catch (JiraNotConnectedException) { return Results.Ok(Array.Empty<TicketProject>()); }
});

// How the SPA turns a mock ticket id into a link (…/#PROJ-1001). Present whenever a mock-style
// backend is active; Jira ids link via each project's site URL instead (from /api/projects).
var boardUrl = builder.Configuration["Tickets:BoardUrl"] ?? "http://localhost:5090";
var ticketUrlTemplate = UsesBackend("Http") || UsesBackend("InMemory")
    ? builder.Configuration["Tickets:TicketUrlTemplate"] ?? $"{boardUrl}/#{{id}}"
    : null;

// Start a new chat. Returns its id, the greeting it opened with (one of several, rotated per
// chat), the mock link template, and whether Jira is enabled (so the SPA shows the connect UI
// and gates chat behind it).
app.MapPost("/api/conversations", (ConversationStore store, CurrentSession current) =>
{
    // Identity is the point of a session here, so an unknown or expired one is a 401 rather than an
    // anonymous chat: without a user the mock's reads used to fall back to its admin view, and the
    // assistant would cheerfully list every user's tickets as if they were yours.
    if (current.Get() is null) return Results.Unauthorized();

    var (conversationId, greeting) = store.Create();
    return Results.Ok(new { conversationId, greeting, boardUrl, ticketUrlTemplate, jiraEnabled });
});

// The assistant's standing instructions, verbatim. Only answered for a caller that asked for the
// debug trace (X-Debug): it's what the console's debug console shows the moment it's opened,
// instead of waiting for the next turn to carry the system prompt along with everything else.
app.MapGet("/api/system-prompt", (DebugTrace debug) => debug.Enabled
    ? Results.Ok(new { systemPrompt = ConversationStore.SystemPrompt })
    : Results.NotFound());

// Which LLM providers exist, which models each is configured with, which one this request would
// use, and which providers are actually usable (Ollama always; hosted providers only when their API
// key is configured) — the console uses `configured` to blank out the model dropdown for keyless
// providers, and `models` to fill it. Callers override per request with the X-Llm-Provider /
// X-Llm-Model headers.
app.MapGet("/api/llm", (ChatClientFactory chatClients, IConfiguration config) =>
{
    var (provider, model) = chatClients.Current();
    return Results.Ok(new
    {
        providers = ChatClientFactory.KnownProviders,
        models = ChatClientFactory.KnownProviders.ToDictionary(p => p, chatClients.ModelsFor),
        defaultModels = ChatClientFactory.KnownProviders.ToDictionary(p => p, chatClients.DefaultModelFor),
        configured = ChatClientFactory.KnownProviders.ToDictionary(
            p => p,
            p => p == "Ollama" || !string.IsNullOrEmpty(config[$"{p}:ApiKey"])),
        provider,
        model
    });
});

// The models installed in the local Ollama instance, for the console's model dropdown.
// Empty list when Ollama isn't reachable — the console falls back to a free-text field.
app.MapGet("/api/llm/ollama/models", async (IConfiguration config, CancellationToken ct) =>
{
    try
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri(config["Ollama:BaseUrl"] ?? "http://localhost:11434"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        var tags = await http.GetFromJsonAsync<JsonElement>("/api/tags", ct);
        var names = tags.GetProperty("models").EnumerateArray()
            .Select(m => m.GetProperty("name").GetString())
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(n => n)
            .ToArray();
        return Results.Ok(names);
    }
    catch
    {
        return Results.Ok(Array.Empty<string>());
    }
});

// What the local Ollama is actually running on right now (reads its /api/ps): GPU, CPU, or
// a split when the model doesn't fully fit in VRAM. Powers the status badge next to the
// console's GPU/CPU selector — the selector is the request, this is the reality. Also
// reports whether the machine even has an NVIDIA GPU and whether one was attached to the
// Ollama container, so the badge can say *why* it's on CPU ("GPU not attached").
app.MapGet("/api/llm/ollama/status", async (IConfiguration config, CancellationToken ct) =>
{
    // Attached = the compose deploy handed a real device to the ollama service (the up
    // scripts set OLLAMA_GPU_DEVICE, mirrored into this service's config; the CPU default
    // maps the harmless /dev/null instead).
    var gpuDevice = config["Ollama:GpuDevice"];
    var gpuAttached = !string.IsNullOrWhiteSpace(gpuDevice) && gpuDevice != "/dev/null";
    var hostHasGpu = HostHasNvidiaGpu();

    try
    {
        using var http = new HttpClient
        {
            BaseAddress = new Uri(config["Ollama:BaseUrl"] ?? "http://localhost:11434"),
            Timeout = TimeSpan.FromSeconds(5)
        };
        var ps = await http.GetFromJsonAsync<JsonElement>("/api/ps", ct);
        var loaded = ps.GetProperty("models").EnumerateArray().FirstOrDefault();
        if (loaded.ValueKind != JsonValueKind.Object)
        {
            // Nothing in memory — the model hasn't been loaded yet (compose sets
            // OLLAMA_KEEP_ALIVE=-1, so once loaded it stays until the container restarts).
            return Results.Ok(new { loaded = false, model = (string?)null, processor = (string?)null, gpuAttached, hostHasGpu });
        }

        var name = loaded.GetProperty("name").GetString();
        var size = loaded.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
        var sizeVram = loaded.TryGetProperty("size_vram", out var v) ? v.GetInt64() : 0;
        var processor = sizeVram <= 0 ? "CPU"
            : sizeVram >= size ? "GPU"
            : $"{(int)(sizeVram * 100.0 / size)}% GPU / CPU";
        return Results.Ok(new { loaded = true, model = name, processor, gpuAttached, hostHasGpu });
    }
    catch
    {
        return Results.Ok(new { loaded = false, model = (string?)null, processor = (string?)null, gpuAttached, hostHasGpu });
    }
});

// Send a user message. Appends it to the conversation, runs the loop, and streams the
// resulting events (assistant text / tools ran / a confirmation request) back as SSE.
app.MapPost("/api/conversations/{id:guid}/messages", async (
    Guid id,
    ChatRequest request,
    ConversationStore store,
    OrchestrationLoop loop,
    CurrentSession current,
    HttpContext http,
    CancellationToken ct) =>
{
    if (current.Get() is null) return Results.Unauthorized();
    // A conversation this process has never heard of (it restarted, most likely) is a 404, not an
    // exception — the console's answer to both is to start a fresh one and resend.
    if (!store.TryGet(id, out var messages)) return Results.NotFound();

    messages.Add(new ChatMessage(ChatRole.User, request.Text));

    await WriteSseAsync(http.Response, loop.RunAsync(messages, ct), ct);
    return Results.Empty;
});

// Answer to a confirmation card: approve (optionally with edits) or decline the paused
// write, then resume the loop and stream what happens next.
app.MapPost("/api/conversations/{id:guid}/confirm", async (
    Guid id,
    ConfirmRequest request,
    ConversationStore store,
    OrchestrationLoop loop,
    CurrentSession current,
    HttpContext http,
    CancellationToken ct) =>
{
    if (current.Get() is null) return Results.Unauthorized();
    if (!store.TryGet(id, out var messages)) return Results.NotFound();

    await WriteSseAsync(
        http.Response,
        loop.ResumeAfterConfirmationAsync(
            messages, request.CallId, request.Approved, request.Approved ? request.Edits : null, ct),
        ct);
    return Results.Empty;
});

app.Run();

// Streams the loop's events to the browser as Server-Sent Events: each event is turned into
// a small JSON object and written as a "data: {...}" frame, flushed immediately so the UI
// updates live instead of waiting for the whole turn to finish.
// Whether this machine has an NVIDIA GPU at all, checked via the PCI vendor id (0x10de).
// /sys/bus/pci shows the host's devices even from inside a container, so this works both
// containerized and in a local run (and just returns false on non-Linux).
static bool HostHasNvidiaGpu()
{
    try
    {
        const string root = "/sys/bus/pci/devices";
        if (!Directory.Exists(root))
        {
            return false;
        }

        foreach (var device in Directory.EnumerateDirectories(root))
        {
            var vendorFile = Path.Combine(device, "vendor");
            if (File.Exists(vendorFile)
                && File.ReadAllText(vendorFile).Trim().Equals("0x10de", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
    catch
    {
        return false;
    }
}

static async Task WriteSseAsync(
    HttpResponse response,
    IAsyncEnumerable<OrchestrationEvent> events,
    CancellationToken ct)
{
    response.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";

    await foreach (var evt in events.WithCancellation(ct))
    {
        // Translate the internal event record into the JSON shape the browser expects.
        object payload = evt switch
        {
            OrchestrationEvent.AssistantText e => new { type = "assistant_text", text = e.Text },
            OrchestrationEvent.AssistantTextDelta e => new { type = "assistant_delta", text = e.Text },
            OrchestrationEvent.AssistantReplace e => new { type = "assistant_replace", text = e.Text },
            OrchestrationEvent.ToolExecuted e => new { type = "tool_executed", toolName = e.ToolName, succeeded = e.Succeeded },
            OrchestrationEvent.ConfirmationRequired e => new
            {
                type = "confirmation_required",
                callId = e.CallId,
                toolName = e.ToolName,
                arguments = e.Arguments
            },
            // Only present when the caller set X-Debug; the console routes these to its debug
            // panel instead of the transcript.
            OrchestrationEvent.Debug e => new
            {
                type = "debug",
                stage = e.Stage,
                label = e.Label,
                detail = e.Detail,
                elapsedMs = e.ElapsedMs,
                at = DateTimeOffset.UtcNow
            },
            _ => throw new InvalidOperationException($"Unhandled event type '{evt.GetType().Name}'.")
        };

        await response.WriteAsync($"data: {JsonSerializer.Serialize(payload)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}

// Request body for POST /api/session: an optional display name to scope the session by.
internal sealed record CreateSessionRequest(string? Name);

// Request body for POST .../messages: the user's chat text.
internal sealed record ChatRequest(string Text);

// Edits holds the (optionally user-modified) tool arguments keyed by parameter name —
// e.g. { title, description, priority } for create_ticket, { status } for a status change.
internal sealed record ConfirmRequest(
    string CallId, bool Approved, Dictionary<string, object?>? Edits);
