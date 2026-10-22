using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TestWizz.Core.Configuration;

namespace TestWizz.AI;

public sealed record ModelInfo(string Name, string? Digest);

/// <summary>A chat request whose answer must be a JSON object matching <see cref="Schema"/>.</summary>
public sealed record JsonChatRequest(string System, string User, string SchemaName, JsonObject Schema);

public sealed record JsonChatResponse(JsonObject Content, int? PromptTokens, int? CompletionTokens);

/// <summary>
/// HTTP client for a local model server. All calls go through <see cref="EndpointGuard"/>, are bounded by the configured
/// timeout, and report failures as <see cref="ModelClientException"/> without exposing the API key or response bodies.
/// </summary>
public abstract class ModelClient : IDisposable
{
    public const int MaxResponseBytes = 8 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly TimeSpan _timeout;

    private protected ModelClient(AiSettings settings, string? apiKey, HttpMessageHandler handler)
    {
        Settings = settings;
        _apiKey = apiKey;
        _timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
    }

    public AiSettings Settings { get; }

    /// <summary>Creates the client for <paramref name="settings"/>. Fails before any network call when the API key variable is unset.</summary>
    public static ModelClient Create(AiSettings settings, Func<string, string?> getEnvironmentVariable, EndpointGuard? guard = null)
    {
        string? apiKey = null;
        if (settings.ApiKeyEnv is { } variable)
        {
            apiKey = getEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ModelClientException(ModelFailure.MissingApiKey, $"Environment variable {variable} (ai.apiKeyEnv) is not set.");
            }
        }

        var handler = (guard ?? new EndpointGuard()).CreateHandler();
        return settings.Provider switch
        {
            AiProvider.Ollama => new OllamaClient(settings, apiKey, handler),
            AiProvider.OpenAiCompatible => new OpenAiCompatibleClient(settings, apiKey, handler),
            _ => throw new ArgumentOutOfRangeException(nameof(settings)),
        };
    }

    /// <summary>Lists installed models, or returns null when the server does not support listing.</summary>
    public abstract Task<IReadOnlyList<ModelInfo>?> ListModelsAsync(CancellationToken cancellationToken);

    public abstract Task<JsonChatResponse> CompleteJsonAsync(JsonChatRequest request, CancellationToken cancellationToken);

    /// <summary>Digest of the configured model for provenance, or null when the server does not report one.</summary>
    public async Task<string?> ModelDigestAsync(CancellationToken cancellationToken)
    {
        var models = await ListModelsAsync(cancellationToken).ConfigureAwait(false);
        return models?.FirstOrDefault(m => m.Name == Settings.Model || m.Name == Settings.Model + ":latest")?.Digest;
    }

    public void Dispose()
    {
        _http.Dispose();
        GC.SuppressFinalize(this);
    }

    private protected Uri BaseUri
    {
        get
        {
            var text = Settings.Endpoint.OriginalString;
            return new Uri(text.EndsWith('/') ? text : text + "/");
        }
    }

    private protected async Task<JsonNode> SendAsync(HttpMethod method, string relativePath, JsonObject? body, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(method, new Uri(BaseUri, relativePath));
        if (_apiKey is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
            {
                throw new ModelClientException(ModelFailure.RedirectRejected, $"Server answered with redirect {status}; redirects are not followed.");
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ModelClientException(ModelFailure.AuthFailed, $"Server rejected the credentials (HTTP {status}).");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new ModelHttpException(status);
            }

            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ModelClientException(ModelFailure.EmptyResponse, "Server returned an empty response.");
            }

            try
            {
                return JsonNode.Parse(text) ?? throw new JsonException();
            }
            catch (JsonException ex)
            {
                throw new ModelClientException(ModelFailure.InvalidResponse, "Server response is not valid JSON.", ex);
            }
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            throw new ModelClientException(ModelFailure.Cancelled, "Cancelled.", ex);
        }
        catch (OperationCanceledException ex)
        {
            throw new ModelClientException(ModelFailure.Timeout, $"No response within {_timeout.TotalSeconds:0} s (ai.timeoutSeconds).", ex);
        }
        catch (HttpRequestException ex)
        {
            throw Map(ex);
        }
    }

    /// <summary>Parses the model's message text as a JSON object, tolerating a surrounding Markdown code fence.</summary>
    private protected static JsonObject ParseContent(string? content, JsonObject schema)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new ModelClientException(ModelFailure.EmptyResponse, "Model returned no content.");
        }

        var text = content.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal) && text.EndsWith("```", StringComparison.Ordinal) && text.Length >= 6)
        {
            var firstNewline = text.IndexOf('\n', StringComparison.Ordinal);
            text = firstNewline < 0 ? text[3..^3] : text[(firstNewline + 1)..^3];
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new ModelClientException(ModelFailure.InvalidResponse, "Model output is not valid JSON.", ex);
        }

        var errors = JsonSchemaLite.Validate(node, schema);
        if (errors.Count > 0)
        {
            throw new ModelClientException(ModelFailure.InvalidResponse, $"Model output does not match the schema: {string.Join(" ", errors.Take(5))}");
        }

        return node!.AsObject();
    }

    private protected static int? IntOrNull(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var result) ? result : null;

    private static ModelClientException Map(HttpRequestException ex)
    {
        for (Exception? inner = ex; inner is not null; inner = inner.InnerException)
        {
            switch (inner)
            {
                case ModelClientException guardFailure:
                    return guardFailure;
                case AuthenticationException:
                    return new ModelClientException(ModelFailure.TlsFailed, "TLS handshake failed; the server certificate is not trusted.", ex);
            }
        }

        return ex.HttpRequestError switch
        {
            HttpRequestError.NameResolutionError => new ModelClientException(ModelFailure.DnsFailed, "Could not resolve the model server host.", ex),
            HttpRequestError.ConfigurationLimitExceeded => new ModelClientException(ModelFailure.InvalidResponse, $"Response exceeds {MaxResponseBytes} bytes.", ex),
            HttpRequestError.SecureConnectionError => new ModelClientException(ModelFailure.TlsFailed, "TLS connection failed.", ex),
            _ => new ModelClientException(ModelFailure.ConnectionFailed, "Could not connect to the model server.", ex),
        };
    }

    /// <summary>Non-success status other than auth or redirect; providers translate 404 into a more specific failure.</summary>
    private protected sealed class ModelHttpException(int statusCode)
        : ModelClientException(ModelFailure.HttpError, $"Server returned HTTP {statusCode}.")
    {
        public int StatusCode { get; } = statusCode;
    }
}
