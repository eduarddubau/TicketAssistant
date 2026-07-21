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

    /// <summary>The configured (or built-in) default model for a provider.</summary>
    public string DefaultModelFor(string provider) => provider.ToLowerInvariant() switch
    {
        "anthropic" => configuration["Anthropic:Model"] ?? "claude-sonnet-5",
        "openai" => configuration["OpenAI:Model"] ?? "gpt-4o-mini",
        "google" => configuration["Google:Model"] ?? "gemini-2.0-flash",
        _ => configuration["Ollama:Model"] ?? "llama3.2:3b"
    };

    /// <summary>The chat client for this request, created once per provider+model combination.</summary>
    public IChatClient Resolve()
    {
        var (provider, model) = Current();
        return _clients.GetOrAdd($"{provider}|{model}", _ => Build(provider, model));
    }

    private string RequireKey(string key) =>
        configuration[key] ?? throw new InvalidOperationException($"{key} is required for the selected LLM provider.");

    // Google is reached through its OpenAI-compatible endpoint, so it reuses the OpenAI client.
    private IChatClient Build(string provider, string model) => provider.ToLowerInvariant() switch
    {
        "anthropic" => (configuration["Anthropic:ApiKey"] is { Length: > 0 } key
            ? new AnthropicClient { ApiKey = key }
            : new AnthropicClient()).AsIChatClient(model),

        "openai" => new OpenAIClient(new ApiKeyCredential(RequireKey("OpenAI:ApiKey")))
            .GetChatClient(model)
            .AsIChatClient(),

        "google" => new OpenAIClient(
                new ApiKeyCredential(RequireKey("Google:ApiKey")),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(configuration["Google:BaseUrl"]
                                       ?? "https://generativelanguage.googleapis.com/v1beta/openai/")
                })
            .GetChatClient(model)
            .AsIChatClient(),

        _ => new OllamaApiClient(new Uri(configuration["Ollama:BaseUrl"] ?? "http://localhost:11434"), model)
    };
}
