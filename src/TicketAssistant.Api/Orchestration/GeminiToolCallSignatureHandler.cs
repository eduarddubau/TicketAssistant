using System.Text;
using System.Text.Json.Nodes;

namespace TicketAssistant.Api.Orchestration;

/// <summary>
/// Gemini (3.x models) requires every assistant tool call sent back in history to carry a
/// "thought signature" — an opaque blob attached to the model's original call. The OpenAI
/// SDK we use for Google's OpenAI-compatible endpoint knows nothing about that proprietary
/// field, so it gets lost on the round trip and Gemini rejects the request with a 400.
///
/// Google documents a placeholder value for externally-reconstructed calls; it passes
/// validation, at the cost of the model not recovering its earlier private reasoning (fine
/// for this PoC). This handler injects the placeholder into any outgoing tool call missing a
/// signature, rewriting the JSON at the HTTP layer so the standard client stays unchanged.
/// Attached only to the Google chat client.
/// </summary>
public sealed class GeminiToolCallSignatureHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private const string PlaceholderSignature = "skip_thought_signature_validator";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Content is not null
            && request.Content.Headers.ContentType?.MediaType == "application/json")
        {
            var body = await request.Content.ReadAsStringAsync(cancellationToken);
            if (body.Contains("\"tool_calls\"", StringComparison.Ordinal)
                && TryInjectSignatures(body) is { } rewritten)
            {
                request.Content = new StringContent(rewritten, Encoding.UTF8, "application/json");
            }
        }

        return await base.SendAsync(request, cancellationToken);
    }

    /// <summary>Adds the placeholder signature to each unsigned tool call; null = nothing changed.</summary>
    private static string? TryInjectSignatures(string body)
    {
        try
        {
            var root = JsonNode.Parse(body);
            if (root?["messages"] is not JsonArray messages)
            {
                return null;
            }

            var changed = false;
            foreach (var message in messages)
            {
                if (message?["tool_calls"] is not JsonArray toolCalls)
                {
                    continue;
                }

                foreach (var call in toolCalls)
                {
                    if (call is null || call["extra_content"] is not null)
                    {
                        continue;
                    }

                    call["extra_content"] = new JsonObject
                    {
                        ["google"] = new JsonObject { ["thought_signature"] = PlaceholderSignature }
                    };
                    changed = true;
                }
            }

            return changed ? root.ToJsonString() : null;
        }
        catch
        {
            return null; // never break a request over the rewrite — worst case Google rejects it as before
        }
    }
}
