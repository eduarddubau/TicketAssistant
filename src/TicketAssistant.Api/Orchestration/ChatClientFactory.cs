using System.ClientModel;
using System.Collections.Concurrent;
using Anthropic;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;

namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// Builds the IChatClient for a request. Configuration (Llm:Provider and the per-provider
/// Model settings) supplies the default, but a caller may override it per request with the
/// X-Llm-Provider / X-Llm-Model headers — which is what lets the console switch models on the
/// fly and compare how they behave.
///
/// Clients are cached per provider+model since they're just thin wrappers over an HTTP client;
/// rebuilding one per request would be wasteful.
/// </summary>
public sealed class ChatClientFactory(
    IConfiguration configuration, IHttpContextAccessor accessor, ILogger<ChatClientFactory> logger)
{
    public const string ProviderHeader = "X-Llm-Provider";
    public const string ModelHeader = "X-Llm-Model";
    public const string ComputeHeader = "X-Ollama-Compute";

    /// <summary>The providers the console offers; each needs its own credentials except Ollama.</summary>
    public static readonly string[] KnownProviders = ["Ollama", "Anthropic", "OpenAI", "Google"];

    private readonly ConcurrentDictionary<string, IChatClient> _clients = new();

    /// <summary>
    /// The Ollama model currently held in VRAM on our account, so a switch can hand it back.
    /// Guarded because turns can overlap; the write is cheap and the unload happens outside the lock.
    /// </summary>
    private string? _resident;
    private readonly Lock _residency = new();

    private string? Header(string name) =>
        accessor.HttpContext?.Request.Headers[name].ToString() is { Length: > 0 } value ? value : null;

    /// <summary>The provider/model this request will use, after applying any header override.</summary>
    public (string Provider, string Model) Current()
    {
        var provider = Header(ProviderHeader) ?? configuration["Llm:Provider"] ?? "Ollama";
        var model = Header(ModelHeader) ?? DefaultModelFor(provider);
        return (provider, model);
    }

    /// <summary>
    /// True when this request targets Ollama and the caller asked for CPU-only inference
    /// (X-Ollama-Compute: cpu). Hosted providers always ignore this — where their models run
    /// isn't ours to choose. The loop translates it into Ollama's per-request num_gpu option,
    /// so the choice takes effect immediately, without restarting the container.
    /// </summary>
    public bool CpuOnlyRequested()
    {
        var (provider, _) = Current();
        return provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Header(ComputeHeader), "cpu", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The configured (or built-in) default model for a provider.</summary>
    public string DefaultModelFor(string provider) => provider.ToLowerInvariant() switch
    {
        "anthropic" => configuration["Anthropic:Model"] ?? "claude-sonnet-5",
        "openai" => configuration["OpenAI:Model"] ?? "gpt-4o-mini",
        "google" => configuration["Google:Model"] ?? "gemini-flash-latest",
        _ => DefaultOllamaModel()
    };

    /// <summary>
    /// Every model configured for a provider, in configuration order — the console's model picker.
    /// Deliberately what the config says rather than what a provider reports it can serve: for
    /// Ollama those are the models the stack actually downloaded on startup, and for a hosted
    /// provider its catalogue is long, changes without notice, and mostly isn't wired up here.
    /// </summary>
    public IReadOnlyList<string> ModelsFor(string provider) => provider.ToLowerInvariant() switch
    {
        "ollama" => OllamaModels(),
        _ => [DefaultModelFor(provider)]
    };

    /// <summary>
    /// Ollama uses one shared, space-separated model list (Ollama:Models) for both "what to
    /// download on startup" and "which is the default" — the first entry is the default, the
    /// rest exist for the console's dropdown. Falls back to the older single-model key.
    /// </summary>
    private string DefaultOllamaModel() => OllamaModels().FirstOrDefault() ?? "qwen2.5:3b";

    private IReadOnlyList<string> OllamaModels() =>
        (configuration["Ollama:Models"] ?? configuration["Ollama:Model"] ?? "qwen2.5:3b")
        .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The chat client for this request, created once per provider+model combination.</summary>
    public IChatClient Resolve()
    {
        var (provider, model) = Current();
        ReleaseResidentUnless(provider.Equals("Ollama", StringComparison.OrdinalIgnoreCase) ? model : null);
        return _clients.GetOrAdd($"{provider}|{model}", _ => Build(provider, model));
    }

    /// <summary>
    /// Hands VRAM back for the model being left behind. Ollama's keep-alive (OLLAMA_KEEP_ALIVE=-1
    /// here) pins a model in memory so replies stay fast — but it pins <i>every</i> model that has
    /// answered, and a 6GB card holds one of these at a time. The second one doesn't fail; it
    /// quietly loads part of itself into system RAM and answers several times slower, which reads
    /// as "the bigger model is bad" rather than "the bigger model didn't fit".
    ///
    /// So keep-alive is left to do its job for as long as the choice stands, and a change of model
    /// — or a move to a hosted provider, which needs no local VRAM at all — evicts the previous one
    /// first. Fire-and-forget: the user is waiting on the new model, not on the old one leaving,
    /// and a failed eviction costs speed rather than correctness.
    /// </summary>
    private void ReleaseResidentUnless(string? wanted)
    {
        string? leaving;
        lock (_residency)
        {
            leaving = _resident is { } current
                      && !string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase)
                ? current
                : null;
            _resident = wanted;
        }

        if (leaving is null)
        {
            return;
        }

        logger.LogInformation("Unloading {Model} from Ollama — the request wants {Wanted}",
            leaving, wanted ?? "a hosted provider");

        _ = UnloadAsync(leaving);
    }

    // keep_alive: 0 with no prompt is Ollama's "drop this model now". A short timeout because
    // nothing waits on the answer, and any failure just leaves the model loaded as before.
    private async Task UnloadAsync(string model)
    {
        try
        {
            using var http = new HttpClient
            {
                BaseAddress = new Uri(configuration["Ollama:BaseUrl"] ?? "http://localhost:11434"),
                Timeout = TimeSpan.FromSeconds(10)
            };
            using var response = await http.PostAsJsonAsync("/api/generate", new { model, keep_alive = 0 });
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not unload {Model} from Ollama", model);
        }
    }

    private string RequireKey(string key) =>
        configuration[key] ?? throw new InvalidOperationException($"{key} is required for the selected LLM provider.");

    private IChatClient Build(string provider, string model) => provider.ToLowerInvariant() switch
    {
        "anthropic" => (configuration["Anthropic:ApiKey"] is { Length: > 0 } key
            ? new AnthropicClient { ApiKey = key }
            : new AnthropicClient()).AsIChatClient(model),

        "openai" => new OpenAIClient(new ApiKeyCredential(RequireKey("OpenAI:ApiKey")))
            .GetChatClient(model)
            .AsIChatClient(),

        // Google's own SDK speaks the native Gemini protocol, so provider-specific details
        // like tool-call thought signatures are its problem rather than ours.
        "google" => new Google.GenAI.Client(apiKey: RequireKey("Google:ApiKey")).AsIChatClient(model),

        // Local models answer at their own pace: a long reply, or a turn that loads the model
        // first, easily runs past HttpClient's 100-second default, which would abort the stream
        // mid-answer. Nothing here should impose a deadline — the request's own cancellation
        // token (the browser hanging up) is what ends a call.
        _ => new OllamaApiClient(
            new HttpClient
            {
                BaseAddress = new Uri(configuration["Ollama:BaseUrl"] ?? "http://localhost:11434"),
                Timeout = Timeout.InfiniteTimeSpan
            },
            model)
    };
}
