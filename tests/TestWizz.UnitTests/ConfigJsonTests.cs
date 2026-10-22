using TestWizz.Core.Configuration;

namespace TestWizz.UnitTests;

public class ConfigJsonTests
{
    [Fact]
    public void DefaultConfig_RoundTrips()
    {
        var json = ConfigJson.ToJson(TestWizzConfig.Default).ToJsonString();

        var result = ConfigJson.Parse(json);

        Assert.Empty(result.Errors);
        Assert.Equal(TestWizzConfig.Default, result.Config);
    }

    [Fact]
    public void MinimalConfig_UsesDefaults()
    {
        var result = ConfigJson.Parse("""{ "version": 1 }""");

        Assert.Equal(TestWizzConfig.Default, result.Config);
    }

    [Fact]
    public void FullConfig_IsParsed()
    {
        var result = ConfigJson.Parse("""
            {
              // comments and trailing commas are allowed
              "version": 1,
              "ai": {
                "provider": "openai-compatible",
                "endpoint": "https://10.0.0.5:8443/v1",
                "model": "codestral",
                "apiKeyEnv": "TESTWIZZ_API_KEY",
                "timeoutSeconds": 30,
                "maxContextTokens": 32000,
              },
              "run": { "runtime": "podman", "maxRepairAttempts": 0 },
              "output": "/tmp/proposals"
            }
            """);

        Assert.Empty(result.Errors);
        var config = result.Config!;
        Assert.Equal(AiProvider.OpenAiCompatible, config.Ai.Provider);
        Assert.Equal(new Uri("https://10.0.0.5:8443/v1"), config.Ai.Endpoint);
        Assert.Equal("codestral", config.Ai.Model);
        Assert.Equal("TESTWIZZ_API_KEY", config.Ai.ApiKeyEnv);
        Assert.Equal(30, config.Ai.TimeoutSeconds);
        Assert.Equal(32000, config.Ai.MaxContextTokens);
        Assert.Equal(ContainerRuntime.Podman, config.Run.Runtime);
        Assert.Equal(0, config.Run.MaxRepairAttempts);
        Assert.Equal("/tmp/proposals", config.Output);
    }

    [Theory]
    [InlineData("""{ }""", "$.version")]
    [InlineData("""{ "version": 2 }""", "$.version")]
    [InlineData("""{ "version": "1" }""", "$.version")]
    [InlineData("""{ "version": 1, "extra": true }""", "$.extra")]
    [InlineData("""{ "version": 1, "ai": [] }""", "$.ai")]
    [InlineData("""{ "version": 1, "ai": { "provider": "openai" } }""", "$.ai.provider")]
    [InlineData("""{ "version": 1, "ai": { "endpoint": "localhost:11434" } }""", "$.ai.endpoint")]
    [InlineData("""{ "version": 1, "ai": { "endpoint": "ftp://127.0.0.1" } }""", "$.ai.endpoint")]
    [InlineData("""{ "version": 1, "ai": { "endpoint": "http://user:pw@127.0.0.1" } }""", "$.ai.endpoint")]
    [InlineData("""{ "version": 1, "ai": { "endpoint": "http://127.0.0.1/?k=v" } }""", "$.ai.endpoint")]
    [InlineData("""{ "version": 1, "ai": { "model": " " } }""", "$.ai.model")]
    [InlineData("""{ "version": 1, "ai": { "apiKeyEnv": "sk-secret-value" } }""", "$.ai.apiKeyEnv")]
    [InlineData("""{ "version": 1, "ai": { "timeoutSeconds": 0 } }""", "$.ai.timeoutSeconds")]
    [InlineData("""{ "version": 1, "ai": { "timeoutSeconds": 1.5 } }""", "$.ai.timeoutSeconds")]
    [InlineData("""{ "version": 1, "ai": { "maxContextTokens": 100 } }""", "$.ai.maxContextTokens")]
    [InlineData("""{ "version": 1, "ai": { "unknown": 1 } }""", "$.ai.unknown")]
    [InlineData("""{ "version": 1, "run": { "runtime": "lxc" } }""", "$.run.runtime")]
    [InlineData("""{ "version": 1, "run": { "maxRepairAttempts": 6 } }""", "$.run.maxRepairAttempts")]
    [InlineData("""{ "version": 1, "output": "" }""", "$.output")]
    [InlineData("""{ "version": 1, "output": null }""", "$.output")]
    public void InvalidValue_IsReportedWithPath(string json, string expectedPath)
    {
        var result = ConfigJson.Parse(json);

        Assert.Null(result.Config);
        Assert.Contains(result.Errors, e => e.Path == expectedPath);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "version": 1, "version": 1 }""")]
    public void MalformedDocument_IsRejected(string json)
    {
        var result = ConfigJson.Parse(json);

        Assert.Null(result.Config);
        Assert.Equal("$", Assert.Single(result.Errors).Path);
    }

    [Fact]
    public void AllErrors_AreReportedAtOnce()
    {
        var result = ConfigJson.Parse("""{ "version": 1, "ai": { "provider": "x", "model": "" }, "run": { "runtime": "y" } }""");

        Assert.Equal(["$.ai.provider", "$.ai.model", "$.run.runtime"], result.Errors.Select(e => e.Path));
    }

    [Fact]
    public void InvalidApiKeyEnv_DoesNotEchoTheValue()
    {
        var result = ConfigJson.Parse("""{ "version": 1, "ai": { "apiKeyEnv": "sk-secret-value" } }""");

        Assert.DoesNotContain(result.Errors, e => e.Message.Contains("sk-secret-value", StringComparison.Ordinal));
    }

    [Fact]
    public void NullApiKeyEnv_IsAllowed()
    {
        var result = ConfigJson.Parse("""{ "version": 1, "ai": { "apiKeyEnv": null } }""");

        Assert.Null(result.Config!.Ai.ApiKeyEnv);
    }
}
