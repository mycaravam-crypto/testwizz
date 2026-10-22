using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TestWizz.Core.Configuration;

public sealed record ConfigError(string Path, string Message);

public sealed record ConfigParseResult(TestWizzConfig? Config, IReadOnlyList<ConfigError> Errors)
{
    public bool IsValid => Config is not null;
}

/// <summary>
/// Reads and writes <c>testwizz.json</c>. Parsing is strict: unknown or duplicate keys and out-of-range values are
/// reported with their JSON path instead of being ignored or silently corrected. Omitted keys take default values.
/// </summary>
public static partial class ConfigJson
{
    public const int MinTimeoutSeconds = 1;
    public const int MaxTimeoutSeconds = 3600;
    public const int MinContextTokens = 1024;
    public const int MaxContextTokens = 1_000_000;
    public const int MaxRepairAttemptsLimit = 5;

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        AllowDuplicateProperties = false,
    };

    private static readonly Dictionary<string, AiProvider> Providers = new(StringComparer.Ordinal)
    {
        ["ollama"] = AiProvider.Ollama,
        ["openai-compatible"] = AiProvider.OpenAiCompatible,
    };

    private static readonly Dictionary<string, ContainerRuntime> Runtimes = new(StringComparer.Ordinal)
    {
        ["docker"] = ContainerRuntime.Docker,
        ["podman"] = ContainerRuntime.Podman,
    };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvVarName();

    public static string ProviderName(AiProvider provider) => Providers.Single(p => p.Value == provider).Key;

    public static string RuntimeName(ContainerRuntime runtime) => Runtimes.Single(r => r.Value == runtime).Key;

    public static ConfigParseResult Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException ex)
        {
            return Failed(new ConfigError("$", $"Invalid JSON: {ex.Message}"));
        }

        using (document)
        {
            var errors = new List<ConfigError>();
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Failed(new ConfigError("$", "Expected a JSON object."));
            }

            var top = new ObjectReader(root, "$", errors);
            var version = top.Int("version");
            if (version is null && !top.Has("version"))
            {
                errors.Add(new ConfigError("$.version", $"Required. Use {TestWizzConfig.CurrentVersion}."));
            }
            else if (version is not null && version != TestWizzConfig.CurrentVersion)
            {
                errors.Add(new ConfigError("$.version", $"Unsupported version {version}. This TestWizz supports {TestWizzConfig.CurrentVersion}."));
            }

            var defaults = TestWizzConfig.Default;
            var ai = ReadAi(top.Object("ai"), defaults.Ai, errors);
            var run = ReadRun(top.Object("run"), defaults.Run, errors);
            var output = top.String("output") ?? defaults.Output;
            if (string.IsNullOrWhiteSpace(output))
            {
                errors.Add(new ConfigError("$.output", "Must not be empty."));
            }

            top.RejectUnknown();
            return errors.Count == 0
                ? new ConfigParseResult(new TestWizzConfig(ai, run, output), [])
                : Failed([.. errors]);
        }
    }

    public static JsonObject ToJson(TestWizzConfig config) => new()
    {
        ["version"] = TestWizzConfig.CurrentVersion,
        ["ai"] = new JsonObject
        {
            ["provider"] = ProviderName(config.Ai.Provider),
            ["endpoint"] = config.Ai.Endpoint.OriginalString,
            ["model"] = config.Ai.Model,
            ["apiKeyEnv"] = config.Ai.ApiKeyEnv,
            ["timeoutSeconds"] = config.Ai.TimeoutSeconds,
            ["maxContextTokens"] = config.Ai.MaxContextTokens,
        },
        ["run"] = new JsonObject
        {
            ["runtime"] = RuntimeName(config.Run.Runtime),
            ["maxRepairAttempts"] = config.Run.MaxRepairAttempts,
        },
        ["output"] = config.Output,
    };

    private static AiSettings ReadAi(ObjectReader? reader, AiSettings defaults, List<ConfigError> errors)
    {
        if (reader is null)
        {
            return defaults;
        }

        var provider = defaults.Provider;
        if (reader.String("provider") is { } providerName && !Providers.TryGetValue(providerName, out provider))
        {
            errors.Add(new ConfigError(reader.PathOf("provider"), $"Unknown provider '{providerName}'. Use one of: {string.Join(", ", Providers.Keys)}."));
        }

        var endpoint = defaults.Endpoint;
        if (reader.String("endpoint") is { } endpointText)
        {
            endpoint = ParseEndpoint(endpointText, reader.PathOf("endpoint"), errors) ?? defaults.Endpoint;
        }

        var model = reader.String("model") ?? defaults.Model;
        if (string.IsNullOrWhiteSpace(model))
        {
            errors.Add(new ConfigError(reader.PathOf("model"), "Must not be empty."));
        }

        var apiKeyEnv = reader.Has("apiKeyEnv") ? reader.NullableString("apiKeyEnv") : defaults.ApiKeyEnv;
        if (apiKeyEnv is not null && !EnvVarName().IsMatch(apiKeyEnv))
        {
            errors.Add(new ConfigError(reader.PathOf("apiKeyEnv"), "Must be an environment variable name (letters, digits, underscore), not a key value."));
        }

        var timeout = reader.IntInRange("timeoutSeconds", MinTimeoutSeconds, MaxTimeoutSeconds) ?? defaults.TimeoutSeconds;
        var maxTokens = reader.IntInRange("maxContextTokens", MinContextTokens, MaxContextTokens) ?? defaults.MaxContextTokens;

        reader.RejectUnknown();
        return new AiSettings(provider, endpoint, model, apiKeyEnv, timeout, maxTokens);
    }

    private static RunSettings ReadRun(ObjectReader? reader, RunSettings defaults, List<ConfigError> errors)
    {
        if (reader is null)
        {
            return defaults;
        }

        var runtime = defaults.Runtime;
        if (reader.String("runtime") is { } runtimeName && !Runtimes.TryGetValue(runtimeName, out runtime))
        {
            errors.Add(new ConfigError(reader.PathOf("runtime"), $"Unknown runtime '{runtimeName}'. Use one of: {string.Join(", ", Runtimes.Keys)}."));
        }

        var repairs = reader.IntInRange("maxRepairAttempts", 0, MaxRepairAttemptsLimit) ?? defaults.MaxRepairAttempts;

        reader.RejectUnknown();
        return new RunSettings(runtime, repairs);
    }

    private static Uri? ParseEndpoint(string text, string path, List<ConfigError> errors)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add(new ConfigError(path, "Must be an absolute http:// or https:// URL."));
            return null;
        }

        if (uri.UserInfo.Length > 0)
        {
            errors.Add(new ConfigError(path, "Must not contain credentials. Use apiKeyEnv instead."));
            return null;
        }

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            errors.Add(new ConfigError(path, "Must not contain a query string or fragment."));
            return null;
        }

        return uri;
    }

    private static ConfigParseResult Failed(params ConfigError[] errors) => new(null, errors);

    /// <summary>Tracks which keys of one JSON object were consumed so the rest can be reported as unknown.</summary>
    private sealed class ObjectReader(JsonElement element, string path, List<ConfigError> errors)
    {
        private readonly HashSet<string> _known = new(StringComparer.Ordinal);

        public string PathOf(string name) => $"{path}.{name}";

        public bool Has(string name)
        {
            _known.Add(name);
            return element.TryGetProperty(name, out _);
        }

        public string? String(string name) => Read(name, JsonValueKind.String, "a string", e => e.GetString());

        public string? NullableString(string name)
        {
            _known.Add(name);
            return element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Null
                ? null
                : String(name);
        }

        public int? Int(string name) => Read(name, JsonValueKind.Number, "an integer", e => e.TryGetInt32(out var v) ? v : (int?)null);

        public int? IntInRange(string name, int min, int max)
        {
            var value = Int(name);
            if (value is not null && (value < min || value > max))
            {
                errors.Add(new ConfigError(PathOf(name), $"Must be between {min} and {max}."));
                return null;
            }

            return value;
        }

        public ObjectReader? Object(string name) =>
            Read(name, JsonValueKind.Object, "an object", e => new ObjectReader(e, PathOf(name), errors));

        public void RejectUnknown()
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!_known.Contains(property.Name))
                {
                    errors.Add(new ConfigError(PathOf(property.Name), "Unknown key."));
                }
            }
        }

        private T? Read<T>(string name, JsonValueKind kind, string expected, Func<JsonElement, T?> convert)
        {
            _known.Add(name);
            if (!element.TryGetProperty(name, out var value))
            {
                return default;
            }

            var result = value.ValueKind == kind ? convert(value) : default;
            if (result is null)
            {
                errors.Add(new ConfigError(PathOf(name), $"Must be {expected}."));
            }

            return result;
        }
    }
}
