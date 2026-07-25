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
public sealed class ChatClientFactory(IConfiguration configuration, IHttpContextAccessor accessor)
{
    public const string ProviderHeader = "X-Llm-Provider";
    public const string ModelHeader = "X-Llm-Model";
    public const string ComputeHeader = "X-Ollama-Compute";

    /// <summary>The providers the console offers; each needs its own credentials except Ollama.</summary>
    public static readonly string[] KnownProviders = ["Ollama", "Anthropic", "OpenAI", "Google"];

    private readonly ConcurrentDictionary<string, IChatClient> _clients = new();

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
    /// Ollama uses one shared, space-separated model list (Ollama:Models) for both "what to
    /// download on startup" and "which is the default" — the first entry is the default, the
    /// rest exist for the console's dropdown. Falls back to the older single-model key.
    /// </summary>
    private string DefaultOllamaModel() =>
        configuration["Ollama:Models"]?.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
        ?? configuration["Ollama:Model"]
        ?? "qwen2.5:3b";

    /// <summary>The chat client for this request, created once per provider+model combination.</summary>
    public IChatClient Resolve()
    {
        var (provider, model) = Current();
        return _clients.GetOrAdd($"{provider}|{model}", _ => Build(provider, model));
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
