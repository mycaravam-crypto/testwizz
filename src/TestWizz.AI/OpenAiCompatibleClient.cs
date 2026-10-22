using System.Text.Json.Nodes;
using TestWizz.Core.Configuration;

namespace TestWizz.AI;

/// <summary>
/// OpenAI-compatible API (LM Studio, llama.cpp server, vLLM, ...). The configured endpoint is the base URL including
/// the version segment, e.g. <c>http://127.0.0.1:1234/v1</c>.
/// </summary>
internal sealed class OpenAiCompatibleClient(AiSettings settings, string? apiKey, HttpMessageHandler handler)
    : ModelClient(settings, apiKey, handler)
{
    public override async Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
    {
        JsonNode json;
        try
        {
            json = await SendAsync(HttpMethod.Get, "models", null, cancellationToken).ConfigureAwait(false);
        }
        catch (ModelHttpException ex) when (ex.StatusCode is 404 or 405 or 501)
        {
            return null;
        }

        if (json["data"] is not JsonArray models)
        {
            throw new ModelClientException(ModelFailure.InvalidResponse, "Unexpected /models response: missing 'data'.");
        }

        return [.. models.OfType<JsonObject>()
            .Where(m => m["id"] is JsonValue)
            .Select(m => new ModelInfo(m["id"]!.GetValue<string>(), Digest: null))];
    }

    public override async Task<JsonChatResponse> CompleteJsonAsync(JsonChatRequest request, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = Settings.Model,
            ["temperature"] = 0,
            ["stream"] = false,
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = request.SchemaName,
                    ["strict"] = true,
                    ["schema"] = request.Schema.DeepClone(),
                },
            },
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = request.System },
                new JsonObject { ["role"] = "user", ["content"] = request.User },
            },
        };

        JsonNode json;
        try
        {
            json = await SendAsync(HttpMethod.Post, "chat/completions", body, cancellationToken).ConfigureAwait(false);
        }
        catch (ModelHttpException ex) when (ex.StatusCode == 404)
        {
            throw new ModelClientException(ModelFailure.ModelNotFound, $"Model '{Settings.Model}' is not available on the server.", ex);
        }

        var content = (json["choices"]?[0]?["message"]?["content"] as JsonValue)?.GetValue<string>();
        return new JsonChatResponse(
            ParseContent(content, request.Schema),
            IntOrNull(json["usage"]?["prompt_tokens"]),
            IntOrNull(json["usage"]?["completion_tokens"]));
    }
}
