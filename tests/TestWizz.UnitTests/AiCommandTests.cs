using System.Text.Json.Nodes;
using TestWizz.Cli;
using TestWizz.Core.Configuration;

namespace TestWizz.UnitTests;

public sealed class AiCommandTests : IAsyncLifetime
{
    private const string Secret = "sk-cli-secret-9876543210";

    private readonly string _directory = Directory.CreateTempSubdirectory("testwizz-ai-tests-").FullName;
    private FakeModelServer _server = null!;

    public async ValueTask InitializeAsync() => _server = await FakeModelServer.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    private void WriteConfig(string endpoint, string provider = "ollama", string? apiKeyEnv = null) =>
        File.WriteAllText(Path.Combine(_directory, TestWizzConfig.FileName), new JsonObject
        {
            ["version"] = 1,
            ["ai"] = new JsonObject
            {
                ["provider"] = provider,
                ["endpoint"] = endpoint,
                ["model"] = "qwen2.5-coder:14b",
                ["apiKeyEnv"] = apiKeyEnv,
                ["timeoutSeconds"] = 10,
            },
        }.ToJsonString());

    private (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = new CliApp(stdout, stderr, _directory, name => name == "TESTWIZZ_KEY" ? Secret : null).Run(args);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Check_Success_ExitsZeroWithReport()
    {
        _server.AsOllama("qwen2.5-coder:14b", """{"answer":5}""");
        WriteConfig(_server.BaseUri.ToString());

        var (exit, stdout, stderr) = Run("ai", "check", "--format", "json");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Empty(stderr);
        var json = JsonNode.Parse(stdout)!;
        Assert.Equal("ok", (string)json["status"]!);
        Assert.True((bool)json["result"]!["ok"]!);
        Assert.Equal("sha256:abc123", (string)json["result"]!["modelDigest"]!);
        Assert.Equal(
            ["apiKey", "endpoint", "model", "structuredOutput"],
            json["result"]!["steps"]!.AsArray().Select(s => (string)s!["name"]!));
    }

    [Fact]
    public void Check_TextOutput_ListsSteps()
    {
        _server.AsOllama("qwen2.5-coder:14b", """{"answer":5}""");
        WriteConfig(_server.BaseUri.ToString());

        var (exit, stdout, _) = Run("ai", "check");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("structuredOutput", stdout, StringComparison.Ordinal);
        Assert.Contains("Result: OK", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_ServerFailure_ExitsOne()
    {
        _server.AsOllama("qwen2.5-coder:14b", "not json");
        WriteConfig(_server.BaseUri.ToString());

        var (exit, stdout, _) = Run("ai", "check", "--format", "json");

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        var json = JsonNode.Parse(stdout)!;
        Assert.Equal("failed", (string)json["status"]!);
        var failed = json["result"]!["steps"]!.AsArray().Single(s => (string)s!["status"]! == "failed")!;
        Assert.Equal("invalid_response", (string)failed["code"]!);
    }

    [Fact]
    public void Check_PublicEndpoint_IsConfigurationError()
    {
        WriteConfig("http://8.8.8.8:11434");

        var (exit, stdout, stderr) = Run("ai", "check", "--format", "json");

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("ai.endpoint_not_private", (string)JsonNode.Parse(stdout)!["error"]!["code"]!);
        Assert.Contains("non-private", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Check_MissingKey_IsConfigurationError()
    {
        WriteConfig(_server.BaseUri.ToString(), apiKeyEnv: "NOT_SET");

        var (exit, stdout, _) = Run("ai", "check", "--format", "json");

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("ai.missing_api_key", (string)JsonNode.Parse(stdout)!["error"]!["code"]!);
        Assert.Empty(_server.Requests);
    }

    [Fact]
    public void Check_NeverPrintsTheApiKey()
    {
        _server.OnJson("GET /v1/models", "{}", status: 401);
        WriteConfig(new Uri(_server.BaseUri, "v1").ToString(), "openai-compatible", "TESTWIZZ_KEY");

        var json = Run("ai", "check", "--format", "json");
        var text = Run("ai", "check");

        Assert.Equal(ExitCodes.ValidationFailed, json.ExitCode);
        Assert.Contains(_server.Requests, r => r.Authorization == $"Bearer {Secret}");
        foreach (var output in new[] { json.Stdout, json.Stderr, text.Stdout, text.Stderr })
        {
            Assert.DoesNotContain(Secret, output, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("ai")]
    [InlineData("ai", "status")]
    [InlineData("ai", "check", "a", "b")]
    public void InvalidSubcommand_IsUsageError(params string[] args)
    {
        Assert.Equal(ExitCodes.Error, Run(args).ExitCode);
    }

    [Fact]
    public void Check_WithoutConfig_PointsToInit()
    {
        var (exit, _, stderr) = Run("ai", "check");

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Contains("testwizz init", stderr, StringComparison.Ordinal);
    }
}
