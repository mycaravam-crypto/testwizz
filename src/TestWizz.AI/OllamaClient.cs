using System.Text.Json.Nodes;
using TestWizz.Core.Configuration;

namespace TestWizz.AI;

/// <summary>Ollama native API: <c>GET /api/tags</c> and <c>POST /api/chat</c> with a JSON schema in <c>format</c>.</summary>
internal sealed class OllamaClient(AiSettings settings, string? apiKey, HttpMessageHandler handler)
    : ModelClient(settings, apiKey, handler)
{
    public override async Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken)
    {
        var json = await SendAsync(HttpMethod.Get, "api/tags", null, cancellationToken).ConfigureAwait(false);
        if (json["models"] is not JsonArray models)
        {
            throw new ModelClientException(ModelFailure.InvalidResponse, "Unexpected /api/tags response: missing 'models'.");
        }

        return [.. models.OfType<JsonObject>()
            .Where(m => m["name"] is JsonValue)
            .Select(m => new ModelInfo(m["name"]!.GetValue<string>(), (m["digest"] as JsonValue)?.GetValue<string>()))];
    }

    public override async Task<JsonChatResponse> CompleteJsonAsync(JsonChatRequest request, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["model"] = Settings.Model,
            ["stream"] = false,
            ["format"] = request.Schema.DeepClone(),
            ["options"] = new JsonObject
            {
                ["temperature"] = 0,
                ["num_ctx"] = Settings.MaxContextTokens,
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
            json = await SendAsync(HttpMethod.Post, "api/chat", body, cancellationToken).ConfigureAwait(false);
        }
        catch (ModelHttpException ex) when (ex.StatusCode == 404)
        {
            throw new ModelClientException(ModelFailure.ModelNotFound, $"Model '{Settings.Model}' is not available. Run 'ollama pull {Settings.Model}'.", ex);
        }

        var content = (json["message"]?["content"] as JsonValue)?.GetValue<string>();
        return new JsonChatResponse(
            ParseContent(content, request.Schema),
            IntOrNull(json["prompt_eval_count"]),
            IntOrNull(json["eval_count"]));
    }
}
