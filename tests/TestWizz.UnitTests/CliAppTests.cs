using System.Text.Json.Nodes;
using TestWizz.Cli;
using TestWizz.Core.Configuration;

namespace TestWizz.UnitTests;

public sealed class CliAppTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("testwizz-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string ConfigPath => Path.Combine(_directory, TestWizzConfig.FileName);

    private (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = new CliApp(stdout, stderr, _directory).Run(args);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private static JsonObject ParseJson(string stdout) => JsonNode.Parse(stdout)!.AsObject();

    [Fact]
    public void NoArguments_ShowsHelp()
    {
        var (exit, stdout, stderr) = Run();

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Usage: testwizz", stdout, StringComparison.Ordinal);
        Assert.Empty(stderr);
    }

    [Fact]
    public void Help_ListsExactlyTheImplementedCommands()
    {
        var (exit, stdout, _) = Run("help", "--format", "json");

        Assert.Equal(ExitCodes.Success, exit);
        var names = ParseJson(stdout)["result"]!["commands"]!.AsArray().Select(c => (string)c!["name"]!);
        Assert.Equal(["help", "init", "config", "ai", "context", "plan"], names);
    }

    [Theory]
    [InlineData("help", "init")]
    [InlineData("init", "--help")]
    [InlineData("init", "-h")]
    public void CommandHelp_ShowsUsage(params string[] args)
    {
        var (exit, stdout, _) = Run(args);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("Usage: testwizz init [path] [--force]", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpForUnknownCommand_IsUsageError()
    {
        Assert.Equal(ExitCodes.Error, Run("help", "frob").ExitCode);
    }

    [Fact]
    public void Version_PrintsVersion()
    {
        var (exit, stdout, _) = Run("--version", "--format", "json");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(CliApp.Version, (string)ParseJson(stdout)["result"]!["version"]!);
    }

    [Theory]
    [InlineData("init", "--version")]
    [InlineData("--version", "--help")]
    public void VersionWithOtherCommand_IsUsageError(params string[] args)
    {
        Assert.Equal(ExitCodes.Error, Run(args).ExitCode);
    }

    [Fact]
    public void Init_WritesValidDefaultConfig()
    {
        var (exit, stdout, stderr) = Run("init", "--format", "json");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Empty(stderr);
        var result = ParseJson(stdout)["result"]!;
        Assert.Equal(ConfigPath, (string)result["path"]!);
        Assert.False((bool)result["overwritten"]!);
        Assert.Equal(TestWizzConfig.Default, ConfigJson.Parse(File.ReadAllText(ConfigPath)).Config);
    }

    [Fact]
    public void Init_RefusesToOverwriteWithoutForce()
    {
        File.WriteAllText(ConfigPath, "keep me");

        var (exit, stdout, stderr) = Run("init", "--format", "json");

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("config.exists", (string)ParseJson(stdout)["error"]!["code"]!);
        Assert.Contains("--force", stderr, StringComparison.Ordinal);
        Assert.Equal("keep me", File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void Init_OverwritesWithForce()
    {
        File.WriteAllText(ConfigPath, "old");

        var (exit, _, _) = Run("init", "--force");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.True(ConfigJson.Parse(File.ReadAllText(ConfigPath)).IsValid);
    }

    [Fact]
    public void Init_InMissingDirectory_Fails()
    {
        var (exit, _, stderr) = Run("init", "does-not-exist");

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Contains("Directory not found", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_ShowsEffectiveConfig()
    {
        File.WriteAllText(ConfigPath, """{ "version": 1, "ai": { "model": "codestral" } }""");

        var (exit, stdout, stderr) = Run("config", "--format", "json");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Empty(stderr);
        var json = ParseJson(stdout);
        Assert.Equal("testwizz.output/v1", (string)json["schema"]!);
        Assert.Equal("ok", (string)json["status"]!);
        Assert.Equal("codestral", (string)json["result"]!["config"]!["ai"]!["model"]!);
        Assert.Equal("ollama", (string)json["result"]!["config"]!["ai"]!["provider"]!);
    }

    [Fact]
    public void Config_Missing_PointsToInit()
    {
        var (exit, stdout, stderr) = Run("config", "--format", "json");

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("config.not_found", (string)ParseJson(stdout)["error"]!["code"]!);
        Assert.Contains("testwizz init", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Config_Invalid_ReportsEveryProblem()
    {
        File.WriteAllText(ConfigPath, """{ "version": 1, "ai": { "provider": "x" }, "nope": 1 }""");

        var (exit, stdout, stderr) = Run("config", "--format", "json");

        Assert.Equal(ExitCodes.Error, exit);
        var error = ParseJson(stdout)["error"]!;
        Assert.Equal("config.invalid", (string)error["code"]!);
        Assert.Equal(["$.ai.provider", "$.nope"], error["details"]!.AsArray().Select(d => (string)d!["path"]!));
        Assert.Contains("$.nope: Unknown key.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonOutput_IsDeterministic()
    {
        File.WriteAllText(ConfigPath, """{ "version": 1 }""");

        var first = Run("config", "--format", "json").Stdout;
        var second = Run("config", "--format", "json").Stdout;

        Assert.Equal(first, second);
        Assert.DoesNotContain("\r", first, StringComparison.Ordinal);
        Assert.Equal(["schema", "command", "status", "result"], ParseJson(first).Select(p => p.Key));
    }

    [Fact]
    public void UsageError_InJsonMode_WritesJsonToStdoutAndMessageToStderr()
    {
        var (exit, stdout, stderr) = Run("frob", "--format", "json");

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("usage", (string)ParseJson(stdout)["error"]!["code"]!);
        Assert.StartsWith("error: Unknown command 'frob'.", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyCommands_DoNotModifyTheDirectory()
    {
        File.WriteAllText(ConfigPath, """{ "version": 1 }""");
        var before = Snapshot();

        Run("help");
        Run("config");
        Run("config", "--format", "json");
        Run("--version");

        Assert.Equal(before, Snapshot());
    }

    private string[] Snapshot() =>
        [.. Directory.EnumerateFileSystemEntries(_directory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => $"{p}:{(File.Exists(p) ? File.ReadAllText(p) : "<dir>")}")];
}
