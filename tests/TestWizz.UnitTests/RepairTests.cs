using System.Text.Json.Nodes;
using TestWizz.Cli;
using TestWizz.Core.Proposals;
using TestWizz.Core.Running;
using TestWizz.DotNet;

namespace TestWizz.UnitTests;

public sealed class RepairCheckTests
{
    private static readonly string Original = CannedTests.Divide;

    private static RepairCheck Check(string from, string to) => XUnitRepairer.Check(Original, Original.Replace(from, to, StringComparison.Ordinal));

    [Fact]
    public void FixingACall_IsAccepted()
    {
        var broken = Original.Replace("Divide(8, 2)", "Divide(8, \"2\")", StringComparison.Ordinal);

        var check = XUnitRepairer.Check(broken, Original);

        Assert.True(check.Accepted);
        Assert.Equal(["Divide_ReturnsQuotient"], check.ChangedExpectations);
    }

    [Fact]
    public void ChangedExpectedValue_IsAcceptedButFlagged()
    {
        var check = Check("Assert.Equal(4, _calculator.Divide(8, 2));", "Assert.Equal(5, _calculator.Divide(10, 2));");

        Assert.True(check.Accepted);
        Assert.Equal(["Divide_ReturnsQuotient"], check.ChangedExpectations);
    }

    [Theory]
    [InlineData("Assert.Equal(4, _calculator.Divide(8, 2));", "_calculator.Divide(8, 2);", "fewer assertions")]
    [InlineData("Assert.Equal(4, _calculator.Divide(8, 2));", "Assert.True(_calculator.Divide(8, 2) > 0);", "looser one")]
    [InlineData("Assert.Throws<DivideByZeroException>(", "Assert.ThrowsAny<Exception>(", "looser one")]
    [InlineData("Assert.Equal(4, _calculator.Divide(8, 2));", "Assert.NotNull(_calculator);", "fewer assertions")]
    [InlineData("[Fact]\n    [Trait(\"Scenario\", \"S1\")]", "[Fact(Skip = \"flaky\")]\n    [Trait(\"Scenario\", \"S1\")]", "now skipped")]
    [InlineData("Assert.Equal(4, _calculator.Divide(8, 2));", "Assert.Skip(\"later\"); Assert.Equal(4, _calculator.Divide(8, 2));", "now skipped")]
    [InlineData("Assert.Equal(4, _calculator.Divide(8, 2));", "Assert.Equal(4, _calculator.Divide(8, 2)); Assert.True(true);", "cannot fail")]
    [InlineData("[Trait(\"Scenario\", \"S1\")]", "[Trait(\"Scenario\", \"S2\")]", "scenario links")]
    [InlineData("public void Divide_ByZero_Throws()", "public void Divide_ByZero_Fails()", "removed or renamed")]
    public void WeakeningRepairs_AreRejected(string from, string to, string reason)
    {
        Assert.Contains(from, Original, StringComparison.Ordinal);

        var check = Check(from, to);

        Assert.False(check.Accepted);
        Assert.Contains(check.Reasons, r => r.Contains(reason, StringComparison.Ordinal));
    }
}

public sealed class RepairLoopTests : IAsyncLifetime
{
    private readonly string _fixture = RepoPaths.CopyToTemp(RepoPaths.DotNetFixture);
    private readonly string _proposals = Directory.CreateTempSubdirectory("testwizz-proposals-").FullName;
    private readonly FakeRuntime _runtime = new();
    private FakeModelServer _server = null!;
    private int _builds;

    private static readonly string Broken = CannedTests.Divide.Replace("Divide(8, 2)", "Divide(8, \"2\")", StringComparison.Ordinal);

    public async ValueTask InitializeAsync()
    {
        _server = await FakeModelServer.StartAsync();
        File.WriteAllText(
            Path.Combine(_fixture, "testwizz.json"),
            new JsonObject { ["version"] = 1, ["ai"] = new JsonObject { ["endpoint"] = _server.BaseUri.ToString() }, ["output"] = _proposals }.ToJsonString());
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_fixture, recursive: true);
        Directory.Delete(_proposals, recursive: true);
    }

    private static string Reply(string code, params string[] changes) => new JsonObject
    {
        ["code"] = code,
        ["changes"] = new JsonArray([.. changes.Select(c => (JsonNode)c)]),
        ["suspectedBugs"] = new JsonArray(),
    }.ToJsonString();

    /// <summary>The build fails while the proposed file still has the broken call; tests pass otherwise.</summary>
    private void CompilesOnlyWhenFixed() => _runtime.Respond = spec =>
    {
        var file = Path.Combine(spec.Workspace, "tests", "Shop.Tests", "CalculatorDivideTests.cs");
        switch (FakeRuntime.Phase(spec))
        {
            case "build":
                _builds++;
                return File.ReadAllText(file).Contains("\"2\"", StringComparison.Ordinal)
                    ? new ContainerResult(1, "/work/tests/Shop.Tests/CalculatorDivideTests.cs(11,46): error CS1503: Argument 2: cannot convert from 'string' to 'int' [/work/tests/Shop.Tests/Shop.Tests.csproj]", TimeSpan.Zero, false)
                    : new ContainerResult(0, "ok", TimeSpan.Zero, false);
            case "test":
                FakeRuntime.WriteTrx(spec, Bundles.Trx(("Divide_ReturnsQuotient", "Passed", null), ("Divide_ByZero_Throws", "Passed", null), ("Divide_ByOne_ReturnsDividend", "Passed", null)));
                return new ContainerResult(0, "ok", TimeSpan.Zero, false);
            default:
                return new ContainerResult(0, "ok", TimeSpan.Zero, false);
        }
    };

    private (int ExitCode, JsonNode Json) Run(string bundle, params string[] extra)
    {
        using var stdout = new StringWriter();
        var exit = new CliApp(stdout, new StringWriter(), _fixture, _ => null, _ => _runtime)
            .Run(["run", "--proposal", Path.GetFileName(bundle), "--format", "json", .. extra], TestContext.Current.CancellationToken);
        return (exit, JsonNode.Parse(stdout.ToString())!);
    }

    private static JsonArray History(string bundle) => ProposalBundle.ReadManifest(bundle)["history"]!.AsArray();

    private string[] ChatPrompts() =>
        [.. _server.Requests.Where(r => r.Path == "/api/chat").Select(r => (string)JsonNode.Parse(r.Body)!["messages"]![1]!["content"]!)];

    [Fact]
    public void CompileError_IsRepairedAndRerun()
    {
        var bundle = Bundles.Create(_fixture, _proposals, Broken);
        CompilesOnlyWhenFixed();
        _server.AsOllamaSequence("qwen2.5-coder:14b", Reply(CannedTests.Divide, "Passed the divisor as an int."));

        var (exit, json) = Run(bundle);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal("passed", (string)json["result"]!["status"]!);
        Assert.Equal(1, (int)json["result"]!["repair"]!["accepted"]!);
        Assert.Equal(2, _builds);
        Assert.Equal(CannedTests.Divide + "\n", File.ReadAllText(Path.Combine(bundle, "files", "tests", "Shop.Tests", "CalculatorDivideTests.cs")));
        Assert.Contains("\"2\"", File.ReadAllText(Path.Combine(bundle, "attempts", "1", "tests", "Shop.Tests", "CalculatorDivideTests.cs")), StringComparison.Ordinal);
        Assert.Empty(ProposalReader.Load(bundle).Edited);
        Assert.Contains("+        Assert.Equal(4, _calculator.Divide(8, 2));", File.ReadAllText(Path.Combine(bundle, "changes.patch")), StringComparison.Ordinal);
        Assert.Equal(["run", "repair", "run"], History(bundle).Select(h => (string)h!["kind"]!));
        Assert.Equal("passed", (string)History(bundle)[^1]!["status"]!);

        var prompt = Assert.Single(ChatPrompts());
        Assert.Contains("line 11, column 46: CS1503 Argument 2: cannot convert from 'string' to 'int'", prompt, StringComparison.Ordinal);
        Assert.Contains("Divide(8, \"2\")", prompt, StringComparison.Ordinal);
        Assert.Contains("## History", File.ReadAllText(Path.Combine(bundle, "findings.md")), StringComparison.Ordinal);
    }

    [Fact]
    public void RepairThatDropsAnAssertion_IsRejectedAndRetriedWithTheReason()
    {
        var bundle = Bundles.Create(_fixture, _proposals, Broken);
        CompilesOnlyWhenFixed();
        var dropped = CannedTests.Divide.Replace("Assert.Equal(4, _calculator.Divide(8, 2));", "_calculator.Divide(8, 2);", StringComparison.Ordinal);
        _server.AsOllamaSequence("qwen2.5-coder:14b", Reply(dropped), Reply(CannedTests.Divide));

        var (exit, _) = Run(bundle);

        Assert.Equal(ExitCodes.Success, exit);
        var repairs = History(bundle).Where(h => (string)h!["kind"]! == "repair").ToList();
        Assert.False((bool)repairs[0]!["accepted"]!);
        Assert.Contains("fewer assertions", (string)repairs[0]!["reasons"]![0]!, StringComparison.Ordinal);
        Assert.True((bool)repairs[1]!["accepted"]!);
        Assert.Equal(1, _builds - 1);
        Assert.Contains("Your previous repair was rejected because", ChatPrompts()[1], StringComparison.Ordinal);
    }

    [Fact]
    public void RepairsThatSkip_AreRejectedAndFailuresAreLabelled()
    {
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        _runtime.Respond = spec =>
        {
            if (FakeRuntime.Phase(spec) == "test")
            {
                FakeRuntime.WriteTrx(spec, Bundles.Trx(("Divide_ReturnsQuotient", "Failed", "Assert.Equal() Failure"), ("Divide_ByZero_Throws", "Passed", null), ("Divide_ByOne_ReturnsDividend", "Passed", null)));
                return new ContainerResult(1, "failed", TimeSpan.Zero, false);
            }

            return new ContainerResult(0, "ok", TimeSpan.Zero, false);
        };
        var skipped = CannedTests.Divide.Replace("public void Divide_ReturnsQuotient()", "[Fact(Skip = \"broken\")] public void Divide_ReturnsQuotient()", StringComparison.Ordinal)
            .Replace("[Fact]\n    [Trait(\"Scenario\", \"S1\")]", "[Trait(\"Scenario\", \"S1\")]", StringComparison.Ordinal);
        _server.AsOllamaSequence("qwen2.5-coder:14b", Reply(skipped));

        var (exit, json) = Run(bundle);

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Equal(2, (int)json["result"]!["repair"]!["attempts"]!);
        Assert.Equal(0, (int)json["result"]!["repair"]!["accepted"]!);
        Assert.Equal(["Shop.Tests.CalculatorDivideTests.Divide_ReturnsQuotient"], json["result"]!["stillFailing"]!.AsArray().Select(t => (string)t!));
        var last = History(bundle)[^1]!;
        Assert.Equal("result", (string)last["kind"]!);
        Assert.Equal(RepairLoop.StillFailingLabel, (string)last["label"]!);
        Assert.Equal(CannedTests.Divide + "\n", File.ReadAllText(Path.Combine(bundle, "files", "tests", "Shop.Tests", "CalculatorDivideTests.cs")));
    }

    [Fact]
    public void CompileErrorsInRepositoryCode_AreNotSentToTheModel()
    {
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        _runtime.Respond = spec => FakeRuntime.Phase(spec) == "build"
            ? new ContainerResult(1, "/work/src/Shop/Calculator.cs(3,1): error CS0246: The type 'Missing' could not be found [/work/src/Shop/Shop.csproj]", TimeSpan.Zero, false)
            : new ContainerResult(0, "ok", TimeSpan.Zero, false);
        _server.AsOllamaSequence("qwen2.5-coder:14b", Reply(CannedTests.Divide));

        var (exit, _) = Run(bundle);

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Empty(ChatPrompts());
        Assert.Equal("repair-skipped", (string)History(bundle)[^1]!["kind"]!);
    }

    [Fact]
    public void NoRepair_RunsOnce()
    {
        var bundle = Bundles.Create(_fixture, _proposals, Broken);
        CompilesOnlyWhenFixed();
        _server.AsOllamaSequence("qwen2.5-coder:14b", Reply(CannedTests.Divide));

        var (exit, json) = Run(bundle, "--no-repair");

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Equal("compile-error", (string)json["result"]!["status"]!);
        Assert.Empty(ChatPrompts());
    }

    [Fact]
    public void ModelFailure_EndsRepairsButKeepsTheRun()
    {
        var bundle = Bundles.Create(_fixture, _proposals, Broken);
        CompilesOnlyWhenFixed();
        _server.AsOllamaSequence("qwen2.5-coder:14b", """{"code": 42}""");

        var (exit, json) = Run(bundle);

        Assert.Equal(ExitCodes.ValidationFailed, exit);
        Assert.Equal("compile-error", (string)json["result"]!["status"]!);
        Assert.Equal("repair-failed", (string)History(bundle)[^1]!["kind"]!);
    }
}

public sealed class ApplyCommandTests : IDisposable
{
    private readonly string _fixture = RepoPaths.CopyToTemp(RepoPaths.DotNetFixture);
    private readonly string _proposals = Directory.CreateTempSubdirectory("testwizz-proposals-").FullName;
    private const string Target = "tests/Shop.Tests/CalculatorDivideTests.cs";

    public ApplyCommandTests() =>
        File.WriteAllText(Path.Combine(_fixture, "testwizz.json"), new JsonObject { ["version"] = 1, ["output"] = _proposals }.ToJsonString());

    public void Dispose()
    {
        Directory.Delete(_fixture, recursive: true);
        Directory.Delete(_proposals, recursive: true);
    }

    private (int ExitCode, JsonNode Json) Apply(string bundle, params string[] extra)
    {
        using var stdout = new StringWriter();
        var exit = new CliApp(stdout, new StringWriter(), _fixture, _ => null)
            .Run(["apply", "--proposal", Path.GetFileName(bundle), "--format", "json", .. extra], TestContext.Current.CancellationToken);
        return (exit, JsonNode.Parse(stdout.ToString())!);
    }

    private static void RecordRun(string bundle, string status) =>
        ProposalBundle.AppendHistory(bundle, new JsonObject { ["at"] = "2026-01-01T00:00:00Z", ["kind"] = "run", ["status"] = status, ["summary"] = status });

    private string VerifiedBundle()
    {
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        RecordRun(bundle, "passed");
        return bundle;
    }

    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(_fixture, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(_fixture, p).Replace('\\', '/'), File.ReadAllText);

    [Fact]
    public void VerifiedProposal_WritesOnlyTheNewTestFile()
    {
        var bundle = VerifiedBundle();
        var before = Snapshot();

        var (exit, json) = Apply(bundle);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal([Target], json["result"]!["files"]!.AsArray().Select(f => (string)f!));
        var after = Snapshot();
        Assert.Equal(CannedTests.Divide + "\n", after[Target]);
        after.Remove(Target);
        Assert.Equal(before, after);
        var applied = ProposalBundle.ReadManifest(bundle)["history"]!.AsArray()[^1]!;
        Assert.Equal("apply", (string)applied["kind"]!);
        Assert.True((bool)applied["verified"]!);

        var (again, error) = Apply(bundle);
        Assert.Equal(ExitCodes.Error, again);
        Assert.Equal("apply.file_exists", (string)error["error"]!["code"]!);
    }

    [Theory]
    [InlineData(null, "apply.unverified")]
    [InlineData("failed", "apply.unverified")]
    [InlineData("compile-error", "apply.unverified")]
    public void UnverifiedProposal_NeedsExplicitConsent(string? lastRun, string code)
    {
        var bundle = Bundles.Create(_fixture, _proposals, CannedTests.Divide);
        if (lastRun is not null)
        {
            RecordRun(bundle, lastRun);
        }

        var (exit, json) = Apply(bundle);
        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal(code, (string)json["error"]!["code"]!);
        Assert.False(File.Exists(Path.Combine(_fixture, Target)));

        Assert.Equal(ExitCodes.Success, Apply(bundle, "--allow-unverified").ExitCode);
        Assert.False((bool)ProposalBundle.ReadManifest(bundle)["history"]!.AsArray()[^1]!["verified"]!);
    }

    [Fact]
    public void EditedAfterRun_IsUnverified()
    {
        var bundle = VerifiedBundle();
        File.AppendAllText(Path.Combine(bundle, "files", "tests", "Shop.Tests", "CalculatorDivideTests.cs"), "// edited\n");

        Assert.Equal("apply.unverified", (string)Apply(bundle).Json["error"]!["code"]!);
    }

    [Fact]
    public void ChangedSource_IsRefused()
    {
        var bundle = VerifiedBundle();
        File.AppendAllText(Path.Combine(_fixture, "src", "Shop", "Calculator.cs"), "\n// changed\n");

        var (exit, json) = Apply(bundle, "--allow-unverified");

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("apply.stale", (string)json["error"]!["code"]!);
        Assert.False(File.Exists(Path.Combine(_fixture, Target)));
    }

    [Fact]
    public void ExistingTargetFile_IsNeverOverwritten()
    {
        var bundle = VerifiedBundle();
        File.WriteAllText(Path.Combine(_fixture, Target), "mine");

        Assert.Equal("apply.file_exists", (string)Apply(bundle, "--allow-unverified").Json["error"]!["code"]!);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(_fixture, Target)));
    }

    [Fact]
    public void ProductionPaths_AreRefused()
    {
        var bundle = VerifiedBundle();
        var manifest = Path.Combine(bundle, "manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace($"\"path\": \"{Target}\"", "\"path\": \"src/Shop/Backdoor.cs\"", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.Combine(bundle, "files", "src", "Shop"));
        File.Copy(Path.Combine(bundle, "files", "tests", "Shop.Tests", "CalculatorDivideTests.cs"), Path.Combine(bundle, "files", "src", "Shop", "Backdoor.cs"));

        var (exit, json) = Apply(bundle, "--allow-unverified");

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("apply.outside_test_project", (string)json["error"]!["code"]!);
        Assert.False(File.Exists(Path.Combine(_fixture, "src", "Shop", "Backdoor.cs")));
    }

    [Fact]
    public void SymlinkedTestFolderOutsideRepository_IsRefused()
    {
        var bundle = VerifiedBundle();
        var outside = Directory.CreateTempSubdirectory("testwizz-outside-").FullName;
        try
        {
            var tests = Path.Combine(_fixture, "tests", "Shop.Tests");
            foreach (var file in Directory.GetFiles(tests))
            {
                File.Copy(file, Path.Combine(outside, Path.GetFileName(file)));
            }

            Directory.Delete(tests, recursive: true);
            Directory.CreateSymbolicLink(tests, outside);

            Assert.Equal("apply.path_escape", (string)Apply(bundle, "--allow-unverified").Json["error"]!["code"]!);
            Assert.False(File.Exists(Path.Combine(outside, "CalculatorDivideTests.cs")));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }
}
