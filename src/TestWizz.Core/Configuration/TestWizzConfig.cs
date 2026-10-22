namespace TestWizz.Core.Configuration;

public enum AiProvider
{
    Ollama,
    OpenAiCompatible,
}

public enum ContainerRuntime
{
    Docker,
    Podman,
}

/// <summary>Connection settings for the local model. Reachability and private-network checks happen when the client connects.</summary>
/// <param name="ApiKeyEnv">Name of the environment variable holding the API key. The key itself is never stored in configuration.</param>
public sealed record AiSettings(
    AiProvider Provider,
    Uri Endpoint,
    string Model,
    string? ApiKeyEnv,
    int TimeoutSeconds,
    int MaxContextTokens);

public sealed record RunSettings(ContainerRuntime Runtime, int MaxRepairAttempts);

/// <param name="Output">Proposal output directory, relative to the directory containing the config file.</param>
public sealed record TestWizzConfig(AiSettings Ai, RunSettings Run, string Output)
{
    public const string FileName = "testwizz.json";
    public const int CurrentVersion = 1;

    public static TestWizzConfig Default { get; } = new(
        new AiSettings(
            AiProvider.Ollama,
            new Uri("http://127.0.0.1:11434"),
            "qwen2.5-coder:14b",
            ApiKeyEnv: null,
            TimeoutSeconds: 120,
            MaxContextTokens: 16000),
        new RunSettings(ContainerRuntime.Docker, MaxRepairAttempts: 2),
        "../.testwizz-proposals");
}
