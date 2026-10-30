using System.Diagnostics;
using System.Text.Json.Nodes;
using TestWizz.Cli;
using TestWizz.Core.Generation;
using TestWizz.Core.Planning;
using TestWizz.Core.Proposals;
using TestWizz.DotNet;

namespace TestWizz.UnitTests;

public sealed class PathGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("testwizz-guard-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("testwizz-outside-").FullName;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        Directory.Delete(_outside, recursive: true);
    }

    [Theory]
    [InlineData("tests/App.Tests/FooTests.cs", true)]
    [InlineData("FooTests.cs", true)]
    [InlineData("../FooTests.cs", false)]
    [InlineData("tests/../../FooTests.cs", false)]
    [InlineData("tests/./FooTests.cs", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:/Windows/x.cs", false)]
    [InlineData("tests\\FooTests.cs", false)]
    [InlineData("tests//FooTests.cs", false)]
    [InlineData("tests/ FooTests.cs", false)]
    [InlineData("", false)]
    public void IsSafeRelative(string path, bool expected) => Assert.Equal(expected, PathGuard.IsSafeRelative(path));

    private static GeneratedFile File(string path) => new(path, "// test\n");

    private string[] Codes(params GeneratedFile[] files) => [.. ProposalChecks.Paths(_root, files).Select(f => f.Code)];

    [Fact]
    public void PlainNewFile_Passes() => Assert.Empty(Codes(File("tests/FooTests.cs")));

    [Fact]
    public void TraversalAndAbsolutePaths_AreRejected()
    {
        Assert.Equal(["unsafe-path", "unsafe-path"], Codes(File("../escape.cs"), File("/tmp/escape.cs")));
    }

    [Fact]
    public void SymlinkedFolderPointingOutside_IsAnEscape()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "tests"), _outside);

        Assert.Equal(["path-escape"], Codes(File("tests/FooTests.cs")));
    }

    [Fact]
    public void SymlinkedFolderInsideRepository_IsAllowed()
    {
        Directory.CreateDirectory(Path.Combine(_root, "real"));
        Directory.CreateSymbolicLink(Path.Combine(_root, "tests"), Path.Combine(_root, "real"));

        Assert.Empty(Codes(File("tests/FooTests.cs")));
    }

    [Fact]
    public void ExistingFilesAndDanglingLinks_AreNeverOverwritten()
    {
        System.IO.File.WriteAllText(Path.Combine(_root, "Existing.cs"), "");
        System.IO.File.CreateSymbolicLink(Path.Combine(_root, "Dangling.cs"), Path.Combine(_root, "missing.cs"));

        Assert.Equal(["file-exists", "file-exists"], Codes(File("Existing.cs"), File("Dangling.cs")));
    }

    [Fact]
    public void SameFileTwice_IsRejected() => Assert.Contains("duplicate-path", Codes(File("A.cs"), File("a.cs")));

    [Fact]
    public void OutputDirectoryThroughSymlinkIntoRepository_IsInside()
    {
        var link = Path.Combine(_outside, "proposals");
        Directory.CreateSymbolicLink(link, _root);

        Assert.True(PathGuard.IsWithin(_root, Path.Combine(link, "x")));
        Assert.False(PathGuard.IsWithin(_root, Path.Combine(_outside, "x")));
        Assert.False(PathGuard.IsWithin(_root, _root + "-sibling"));
    }
}

public sealed class TestQualityTests : IDisposable
{
    private readonly string _fixture = RepoPaths.CopyToTemp(RepoPaths.DotNetFixture);

    public void Dispose() => Directory.Delete(_fixture, recursive: true);

    private GenerationResult Assemble(string body, string target = "Calculator.Divide")
    {
        var code = $$"""
            namespace Shop.Tests;

            public class CalculatorDivideTests
            {
                private readonly Calculator _calculator = new();

            {{body}}
            }
            """;
        var provider = RoslynContextProvider.Load(_fixture, TestContext.Current.CancellationToken);
        var context = provider.GetContext(target, "test");
        var extractor = new CSharpScenarioExtractor();
        var plan = ScenarioPlanner.Create(context, extractor.Version, extractor.Extract(context));
        var conventions = XUnitConventions.Detect(_fixture, context);
        return XUnitGenerator.Assemble(provider, context, plan, conventions, code, [], new GenerationProvenance("ollama", "m", null, "xunit/v1", "x"));
    }

    private static string Test(string name, string statements, string attribute = "[Fact]") => $$"""
            {{attribute}}
            [Trait("Scenario", "S1")]
            public void {{name}}()
            {
                {{statements}}
            }
        """;

    private static string[] Codes(GenerationResult result, string severity = "error") =>
        [.. result.Findings.Where(f => f.Severity == severity).Select(f => f.Code).Where(c => c != "scenario-not-covered")];

    [Theory]
    [InlineData("Assert.Equal(4, _calculator.Divide(8, 2));")]
    [InlineData("Assert.Throws<DivideByZeroException>(() => _calculator.Divide(1, 0));")]
    [InlineData("_calculator.Divide(8, 2).Should().Be(4);")]
    [InlineData("_calculator.Divide(8, 2).ShouldBe(4);")]
    [InlineData("AssertQuotient(4, 8, 2);")]
    public void MeaningfulAssertions_Pass(string statement)
    {
        var result = Assemble(Test("Divide_Works", statement) + "\n    private void AssertQuotient(int e, int a, int b) => Assert.Equal(e, _calculator.Divide(a, b));");

        Assert.Empty(Codes(result));
        Assert.Empty(Codes(result, "warning"));
        Assert.Equal(1, result.Tests.Single().Assertions.Meaningful);
    }

    [Theory]
    [InlineData("Assert.True(true);")]
    [InlineData("Assert.False(false);")]
    [InlineData("Assert.Equal(4, 4);")]
    [InlineData("Assert.Equal(-1, (-1));")]
    [InlineData("var x = _calculator.Divide(8, 2); Assert.Equal(x, x);")]
    [InlineData("Assert.NotNull(new object());")]
    [InlineData("true.Should().BeTrue();")]
    [InlineData("throw new NotImplementedException();")]
    public void Placeholders_AreErrors(string statement)
    {
        var result = Assemble(Test("Divide_Works", statement));

        Assert.Equal(["placeholder-assertion"], Codes(result));
        Assert.Equal(11, result.Findings.Single(f => f.Code == "placeholder-assertion").Line);
    }

    [Theory]
    [InlineData("[Fact(Skip = \"later\")]", "Assert.Equal(4, _calculator.Divide(8, 2));")]
    [InlineData("[Fact]", "Assert.Skip(\"later\"); Assert.Equal(4, _calculator.Divide(8, 2));")]
    [InlineData("[Fact]", "Assert.SkipWhen(true, \"later\"); Assert.Equal(4, _calculator.Divide(8, 2));")]
    public void SkippedTests_AreErrors(string attribute, string statements)
    {
        var result = Assemble(Test("Divide_Works", statements, attribute));

        Assert.Equal(["skipped-test"], Codes(result));
        Assert.True(result.Tests.Single().Skipped);
    }

    [Fact]
    public void MissingAssertions_AreErrors()
    {
        Assert.Equal(["no-assertions"], Codes(Assemble(Test("Divide_Works", "_calculator.Divide(8, 2);"))));
    }

    [Theory]
    [InlineData("Assert.NotNull(_calculator);")]
    [InlineData("Assert.True(_calculator != null);")]
    [InlineData("((object)_calculator).Should().NotBeNull();")]
    public void NullChecksOnly_AreWeakWarnings(string statement)
    {
        var result = Assemble(Test("Divide_Works", statement));

        Assert.True(result.IsValid);
        Assert.Equal(["weak-assertions"], Codes(result, "warning"));
    }

    [Fact]
    public void DuplicateTestNames_AreErrors()
    {
        var statement = "Assert.Equal(4, _calculator.Divide(8, 2));";

        Assert.Contains("duplicate-test-name", Codes(Assemble(Test("Divide_Works", statement) + "\n" + Test("Divide_Works", statement))));
    }

    [Fact]
    public void ExistingTargetFile_IsAnError()
    {
        File.WriteAllText(Path.Combine(_fixture, "tests", "Shop.Tests", "CalculatorDivideTests.cs"), "");

        // Conventions pick a fresh name, so force the clash through the shared check directly.
        var findings = ProposalChecks.Paths(_fixture, [new GeneratedFile("tests/Shop.Tests/CalculatorDivideTests.cs", "x")]);

        Assert.Equal("file-exists", Assert.Single(findings).Code);
    }

    [Fact]
    public void CannedOutputs_HaveNoQualityFindings()
    {
        var provider = RoslynContextProvider.Load(_fixture, TestContext.Current.CancellationToken);
        foreach (var (target, code) in new[] { ("Calculator.Divide", CannedTests.Divide), ("OrderService.PlaceOrderAsync", CannedTests.PlaceOrder) })
        {
            var context = provider.GetContext(target, "test");
            var extractor = new CSharpScenarioExtractor();
            var plan = ScenarioPlanner.Create(context, extractor.Version, extractor.Extract(context));
            var result = XUnitGenerator.Assemble(provider, context, plan, XUnitConventions.Detect(_fixture, context), code, [], new GenerationProvenance("ollama", "m", null, "xunit/v1", "x"));

            Assert.All(result.Tests, t => Assert.True(t.Assertions.Meaningful > 0, t.Name));
            Assert.True(result.IsValid);
        }
    }
}

public sealed class ProposalBundleTests : IAsyncLifetime
{
    private readonly string _fixture = RepoPaths.CopyToTemp(RepoPaths.DotNetFixture);
    private readonly string _proposals = Directory.CreateTempSubdirectory("testwizz-proposals-").FullName;
    private FakeModelServer _server = null!;

    public async ValueTask InitializeAsync()
    {
        _server = await FakeModelServer.StartAsync();
        _server.AsOllama("qwen2.5-coder:14b", new JsonObject { ["code"] = CannedTests.Divide, ["assumptions"] = new JsonArray("Used 8 / 2 as a representative division.") }.ToJsonString());
        File.WriteAllText(
            Path.Combine(_fixture, "testwizz.json"),
            new JsonObject { ["version"] = 1, ["ai"] = new JsonObject { ["endpoint"] = _server.BaseUri.ToString() } }.ToJsonString());
    }

    public async ValueTask DisposeAsync()
    {
        await _server.DisposeAsync();
        Directory.Delete(_fixture, recursive: true);
        Directory.Delete(_proposals, recursive: true);
    }

    private (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = new CliApp(stdout, stderr, _fixture, _ => null).Run(args, TestContext.Current.CancellationToken);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private string Generate()
    {
        var (exit, stdout, stderr) = Run("generate", "--for", "Calculator.Divide", "--out", _proposals, "--format", "json");
        Assert.True(exit == ExitCodes.Success, stderr + stdout);
        return (string)JsonNode.Parse(stdout)!["result"]!["path"]!;
    }

    private static string[] BundleFiles(string bundle) =>
        [.. Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(bundle, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    /// <summary>Set TESTWIZZ_UPDATE_GOLDEN=1 to rewrite the golden bundle after an intentional change.</summary>
    [Fact]
    public void Bundle_MatchesGolden()
    {
        var bundle = Generate();
        var golden = Path.Combine(RepoPaths.Golden, "proposal-calculator-divide");

        if (Environment.GetEnvironmentVariable("TESTWIZZ_UPDATE_GOLDEN") == "1")
        {
            if (Directory.Exists(golden))
            {
                Directory.Delete(golden, recursive: true);
            }

            foreach (var file in BundleFiles(bundle))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(golden, file))!);
                File.Copy(Path.Combine(bundle, file), Path.Combine(golden, file));
            }
        }

        Assert.True(Directory.Exists(golden), "Golden bundle is missing. Run the tests with TESTWIZZ_UPDATE_GOLDEN=1.");
        Assert.Equal(BundleFiles(golden), BundleFiles(bundle));
        // Package versions are Git-derived, so IDs and provenance hashes can vary by commit.
        // Compare the stable payload while validating those dynamic fields separately.
        var expectedManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(golden, "manifest.json")))!.AsObject();
        var actualManifest = JsonNode.Parse(File.ReadAllText(Path.Combine(bundle, "manifest.json")))!.AsObject();
        Assert.False(string.IsNullOrWhiteSpace((string?)actualManifest["id"]));
        Assert.False(string.IsNullOrWhiteSpace((string?)actualManifest["toolVersion"]));
        foreach (var field in new[] { "id", "toolVersion", "contextSha256", "planSha256" })
        {
            expectedManifest.Remove(field);
            actualManifest.Remove(field);
        }
        Assert.True(JsonNode.DeepEquals(expectedManifest, actualManifest));
        foreach (var file in BundleFiles(bundle).Where(file => file != "manifest.json"))
        {
            var expected = File.ReadAllText(Path.Combine(golden, file)).ReplaceLineEndings("\n");
            var actual = File.ReadAllText(Path.Combine(bundle, file)).ReplaceLineEndings("\n");
            if (file == "findings.md")
            {
                expected = System.Text.RegularExpressions.Regex.Replace(expected, @"(?<=# Proposal `)[^`]+", "<dynamic>");
                actual = System.Text.RegularExpressions.Regex.Replace(actual, @"(?<=# Proposal `)[^`]+", "<dynamic>");
            }
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void SameInput_GivesSameBundleUnderANewId()
    {
        var first = Generate();
        var second = Generate();

        Assert.Equal(Path.GetFileName(first) + "-2", Path.GetFileName(second));
        Assert.Equal(BundleFiles(first), BundleFiles(second));
        var a = JsonNode.Parse(File.ReadAllText(Path.Combine(first, "manifest.json")))!.AsObject();
        var b = JsonNode.Parse(File.ReadAllText(Path.Combine(second, "manifest.json")))!.AsObject();
        a.Remove("id");
        b.Remove("id");
        Assert.True(JsonNode.DeepEquals(a, b));
        Assert.Equal(2, Directory.GetDirectories(_proposals).Length);
        Assert.Empty(Directory.GetDirectories(_proposals, ".*"));
    }

    [Fact]
    public void Manifest_RecordsProvenanceAndSources()
    {
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(Generate(), "manifest.json")))!;

        Assert.Equal(ProposalBundle.Schema, (string)manifest["schema"]!);
        Assert.Equal("sha256:abc123", (string)manifest["generator"]!["modelDigest"]!);
        Assert.Equal(XUnitGenerator.PromptTemplate, (string)manifest["generator"]!["promptTemplate"]!);
        Assert.Equal("src/Shop/Calculator.cs", (string)manifest["sources"]![0]!["path"]!);
        Assert.Equal(["Used 8 / 2 as a representative division."], manifest["assumptions"]!.AsArray().Select(a => (string)a!));
        Assert.Equal("Divide_ReturnsQuotient", (string)manifest["scenarios"]![0]!["coveredBy"]![0]!);
    }

    [Fact]
    public void Patch_AppliesCleanlyWithGit()
    {
        var bundle = Generate();
        var before = File.ReadAllText(Path.Combine(bundle, "files", "tests", "Shop.Tests", "CalculatorDivideTests.cs"));

        var git = Process.Start(new ProcessStartInfo("git", ["apply", Path.Combine(bundle, "changes.patch")])
        {
            WorkingDirectory = _fixture,
            RedirectStandardError = true,
        })!;
        var error = git.StandardError.ReadToEnd();
        git.WaitForExit();

        Assert.True(git.ExitCode == 0, error);
        Assert.Equal(before, File.ReadAllText(Path.Combine(_fixture, "tests", "Shop.Tests", "CalculatorDivideTests.cs")));
    }

    [Fact]
    public void Patch_MarksMissingFinalNewline()
    {
        var patch = ProposalBundle.Patch([new GeneratedFile("a/B.cs", "one\ntwo")]);

        Assert.Equal("diff --git a/a/B.cs b/a/B.cs\nnew file mode 100644\n--- /dev/null\n+++ b/a/B.cs\n@@ -0,0 +1,2 @@\n+one\n+two\n\\ No newline at end of file\n", patch);
    }

    [Fact]
    public void OutputInsideRepository_IsRefused()
    {
        var (exit, stdout, _) = Run("generate", "--for", "Calculator.Divide", "--out", Path.Combine(_fixture, "proposals"), "--format", "json");

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("proposal.output_inside_repository", (string)JsonNode.Parse(stdout)!["error"]!["code"]!);
        Assert.False(Directory.Exists(Path.Combine(_fixture, "proposals")));
        Assert.DoesNotContain(_server.Requests, r => r.Path == "/api/chat");
    }

    [Fact]
    public void OutputLinkedIntoRepository_IsRefused()
    {
        var link = Path.Combine(_proposals, "link");
        Directory.CreateSymbolicLink(link, _fixture);

        var (exit, stdout, _) = Run("generate", "--for", "Calculator.Divide", "--out", link, "--format", "json");

        Assert.Equal(ExitCodes.Error, exit);
        Assert.Equal("proposal.output_inside_repository", (string)JsonNode.Parse(stdout)!["error"]!["code"]!);
    }

    [Fact]
    public void DefaultOutput_IsConfiguredDirectoryNextToRepository()
    {
        File.WriteAllText(
            Path.Combine(_fixture, "testwizz.json"),
            new JsonObject { ["version"] = 1, ["ai"] = new JsonObject { ["endpoint"] = _server.BaseUri.ToString() }, ["output"] = Path.GetRelativePath(_fixture, _proposals) }.ToJsonString());

        var (exit, _, _) = Run("generate", "--for", "Calculator.Divide");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Single(Directory.GetDirectories(_proposals));
    }
}


public sealed class EscapedModelCodeTests
{
    [Fact]
    public void DecodesEntireDoubleEscapedCSharpFile()
    {
        // Encode exactly as an extra JSON string layer, as returned by affected models.
        var source = "using Xunit;\npublic class A {\n  [Fact]\n  public void Check() { Assert.Equal(\"a\", \"a\"); }\n}";
        var extraEscaped = System.Text.Json.JsonSerializer.Serialize(source)[1..^1];
        Assert.Equal(source + "\n", XUnitGenerator.NormalizeModelCode(extraEscaped));
    }

    [Fact]
    public void PreservesValidCSharpStringEscapeSequences()
    {
        var source = "class A { string Text = \"hello\\\\nworld\"; }";
        Assert.Equal(source + "\n", XUnitGenerator.NormalizeModelCode(source));
    }
}
