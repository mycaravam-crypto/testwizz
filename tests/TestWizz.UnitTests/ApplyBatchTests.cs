using System.Text.Json.Nodes;
using TestWizz.Cli;

namespace TestWizz.UnitTests;

public sealed class ApplyBatchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("testwizz-apply-batch-").FullName;
    private string Output => Path.Combine(_root, "proposals");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private (int Exit, JsonObject Json) Run(params string[] options)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var arguments = new[] { "apply", "--format", "json" }.Concat(options).ToArray();
        var exit = new CliApp(stdout, stderr, _root).Run(arguments);
        return (exit, JsonNode.Parse(stdout.ToString())!.AsObject());
    }

    [Theory]
    [InlineData("--all")]
    [InlineData("--verified-only")]
    [InlineData("--all", "--verified-only", "--allow-unverified")]
    [InlineData("--all", "--allow-unverified", "--proposal", "abc")]
    [InlineData("--all", "--verified-only", "--proposal", "abc")]
    public void InvalidBatchOptions_AreRejected(params string[] options)
    {
        var (exit, json) = Run(options);
        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("usage", (string)json["error"]!["code"]!);
    }

    [Fact]
    public void AllowUnverifiedBatch_EmptyDirectory_ReturnsSuccessfulSummary()
    {
        Directory.CreateDirectory(Output);
        File.WriteAllText(Path.Combine(_root, "testwizz.json"), """{"version":1,"output":"proposals"}""");

        var (exit, json) = Run("--all", "--allow-unverified");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal("testwizz.apply-batch/v1", (string)json["result"]!["schema"]!);
        Assert.Equal(0, (int)json["result"]!["applied"]!);
    }

    [Fact]
    public void EmptyOutputDirectory_ProducesEmptySuccessfulSummary()
    {
        Directory.CreateDirectory(Output);
        File.WriteAllText(Path.Combine(_root, "testwizz.json"), """{"version":1,"output":"proposals"}""");
        Directory.CreateDirectory(Path.Combine(Output, "not-a-proposal"));

        var (exit, json) = Run("--all", "--verified-only");

        Assert.Equal(ExitCodes.Success, exit);
        var result = json["result"]!;
        Assert.Equal("testwizz.apply-batch/v1", (string)result["schema"]!);
        Assert.Equal(0, (int)result["applied"]!);
        Assert.Equal(0, (int)result["skipped"]!);
        Assert.Equal(0, (int)result["conflicted"]!);
        Assert.Empty(result["items"]!.AsArray());
    }

    [Fact]
    public void UnverifiedAndInvalidProposals_AreSkippedWithoutChangingRepository()
    {
        Directory.CreateDirectory(Output);
        File.WriteAllText(Path.Combine(_root, "testwizz.json"), """{"version":1,"output":"proposals"}""");
        var directory = Path.Combine(Output, "invalid");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), """{"schema":"wrong"}""");

        var (exit, json) = Run("--all", "--verified-only");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal(1, (int)json["result"]!["skipped"]!);
        Assert.Equal("skipped", (string)json["result"]!["items"]![0]!["status"]!);
        Assert.Equal(0, (int)json["result"]!["filesApplied"]!);
    }
}
