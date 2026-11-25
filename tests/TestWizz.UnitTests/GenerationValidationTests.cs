using TestWizz.Core.Context;
using TestWizz.DotNet;

namespace TestWizz.UnitTests;

/// <summary>Ambiguous target resolution (#95): one symbol is one target; distinct symbols are listed distinctly.</summary>
public sealed class TargetResolutionTests : IDisposable
{
    private readonly string _repo = Directory.CreateTempSubdirectory("testwizz-resolve-").FullName;

    public void Dispose() => TestTempDirectories.Delete(_repo);

    private void Write(string path, string code)
    {
        var full = Path.Combine(_repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, code);
    }

    private ContextDocument Context(string target) =>
        RoslynContextProvider.Load(_repo, TestContext.Current.CancellationToken).GetContext(target, "test");

    [Fact]
    public void SameTypeNameInTwoNamespaces_ListsBothFullyQualifiedWithLocations()
    {
        Write("src/Jobs/BackgroundTask.cs", "namespace App.Jobs; public class BackgroundTask { public void StartAsync() { } }");
        Write("src/Legacy/BackgroundTask.cs", "namespace App.Legacy; public class BackgroundTask { public void StartAsync() { } }");

        var ex = Assert.Throws<ContextException>(() => Context("BackgroundTask.StartAsync"));

        Assert.Equal("target.ambiguous", ex.Code);
        Assert.Equal(2, ex.Candidates.Count);
        Assert.Contains(ex.Candidates, c => c.StartsWith("App.Jobs.BackgroundTask.StartAsync() (src/Jobs/BackgroundTask.cs:1; --for \"M:App.Jobs.BackgroundTask.StartAsync\")", StringComparison.Ordinal));
        Assert.Contains(ex.Candidates, c => c.StartsWith("App.Legacy.BackgroundTask.StartAsync() (src/Legacy/BackgroundTask.cs:1", StringComparison.Ordinal));
    }

    [Fact]
    public void QualifiedNameAndDocumentationId_SelectOneOfThem()
    {
        Write("src/Jobs/BackgroundTask.cs", "namespace App.Jobs; public class BackgroundTask { public void StartAsync() { } }");
        Write("src/Legacy/BackgroundTask.cs", "namespace App.Legacy; public class BackgroundTask { public void StartAsync() { } }");

        Assert.Equal("M:App.Jobs.BackgroundTask.StartAsync", Context("App.Jobs.BackgroundTask.StartAsync").Target.Id);
        Assert.Equal("M:App.Legacy.BackgroundTask.StartAsync", Context("M:App.Legacy.BackgroundTask.StartAsync").Target.Id);
    }

    [Fact]
    public void PartialMethodDeclaredTwice_IsOneTargetAndUsesTheImplementation()
    {
        Write("src/Config.cs", "namespace App; public partial class Config { public partial void SetConfig(string value); }");
        Write("src/Config.Impl.cs", "namespace App; public partial class Config { public partial void SetConfig(string value) { System.ArgumentNullException.ThrowIfNull(value); } }");

        var context = Context("Config.SetConfig");

        Assert.Equal("M:App.Config.SetConfig(System.String)", context.Target.Id);
        Assert.Contains("ThrowIfNull", context.Source.Excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void PartialTypeDeclaredInTwoFiles_IsOneTarget()
    {
        Write("src/Config.cs", "namespace App; public partial class Config { public void A() { } }");
        Write("src/Config.More.cs", "namespace App; public partial class Config { public void B() { } }");

        Assert.Equal("T:App.Config", Context("Config").Target.Id);
    }

    [Fact]
    public void RealOverloads_StayAmbiguousWithShortCandidates()
    {
        Write("src/Config.cs", "namespace App; public class Config { public void Set(string v) { } public void Set(int v) { } }");

        var ex = Assert.Throws<ContextException>(() => Context("Config.Set"));

        Assert.Equal(["Config.Set(int)", "Config.Set(string)"], ex.Candidates);
    }
}

/// <summary>Validates generated code against the .NET fixture like <c>generate</c> does.</summary>
public abstract class FixtureValidation : IDisposable
{
    protected string Fixture { get; } = RepoPaths.CopyToTemp(RepoPaths.DotNetFixture);

    public void Dispose()
    {
        TestTempDirectories.Delete(Fixture);
        GC.SuppressFinalize(this);
    }

    protected TestWizz.Core.Generation.GenerationResult Assemble(string target, string code)
    {
        var provider = RoslynContextProvider.Load(Fixture, TestContext.Current.CancellationToken);
        var context = provider.GetContext(target, "test");
        var extractor = new CSharpScenarioExtractor();
        var plan = TestWizz.Core.Planning.ScenarioPlanner.Create(context, extractor.Version, extractor.Extract(context));
        var conventions = XUnitConventions.Detect(Fixture, context);
        return XUnitGenerator.Assemble(provider, context, plan, conventions, code, [],
            new TestWizz.Core.Generation.GenerationProvenance("ollama", "m", null, "xunit/v1", "x"));
    }

    protected static string[] Codes(TestWizz.Core.Generation.GenerationResult result, string severity = "error") =>
        [.. result.Findings.Where(f => f.Severity == severity).Select(f => f.Code)];
}

/// <summary>Malformed or double-escaped model source (#90).</summary>
public sealed class ModelSourceTests : FixtureValidation
{
    private const string Regex = """
        using System.Text.RegularExpressions;

        public class A
        {
            public bool Digits(string s) => Regex.IsMatch(s, "^\\d+$");
            public string Lines() => "first\nsecond\t\"quoted\"";
            public char Slash() => '\\';
        }
        """;

    private static string EscapeOnce(string source) => System.Text.Json.JsonSerializer.Serialize(source)[1..^1];

    [Fact]
    public void FullyEscapedSource_IsDecodedOnceWithATraceableFinding()
    {
        var normalized = ModelSource.Normalize(EscapeOnce(Regex));

        Assert.Equal(Regex.ReplaceLineEndings("\n") + "\n", normalized.Code);
        Assert.Equal("escaped-source-repaired", normalized.Repair?.Code);
    }

    [Fact]
    public void EscapedSourceWithAOnceEscapedRegex_IsStillDecoded()
    {
        // Strict JSON decoding throws on "\d" and used to leave every line escaped: "Unexpected character '\'" on line 1.
        var mixed = EscapeOnce(Regex).Replace(@"\\\\d", @"\d", StringComparison.Ordinal);
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<string>("\"" + mixed + "\""));

        var normalized = ModelSource.Normalize(mixed);

        // Every line is decoded; the regex keeps its single backslash, which validation then reports (CS1009).
        Assert.NotNull(normalized.Repair);
        Assert.Contains("public class A\n{", normalized.Code, StringComparison.Ordinal);
        Assert.Contains("\"^\\d+$\"", normalized.Code, StringComparison.Ordinal);
        Assert.Empty(ModelSource.StrayBackslashLines(normalized.Code));
    }

    [Fact]
    public void ValidSourceWithEscapeSequencesAndLiteralNewlines_IsUnchanged()
    {
        var verbatim = Regex + "\npublic class B { public string Path = @\"C:\\temp\\new\"; public string Raw = \"\"\"\n    a\\nb\n    \"\"\"; }";

        foreach (var source in new[] { Regex, verbatim, "class A { string Text = \"hello\\\\nworld\"; }" })
        {
            var normalized = ModelSource.Normalize(source);

            Assert.Equal(source.ReplaceLineEndings("\n").Trim() + "\n", normalized.Code);
            Assert.Null(normalized.Repair);
        }
    }

    [Fact]
    public void PartlyEscapedFirstLine_IsRepairedWhenTheResultIsValid()
    {
        var partly = "using System;\\nusing System.Linq;\npublic class A { public int One() => 1; }";

        var normalized = ModelSource.Normalize(partly);

        Assert.Equal("using System;\nusing System.Linq;\npublic class A { public int One() => 1; }\n", normalized.Code);
        Assert.NotNull(normalized.Repair);
    }

    [Fact]
    public void PartlyEscapedSourceWithVerbatimBackslashes_IsNotDecoded()
    {
        var partly = "using System;\\nusing System.Linq;\npublic class A { public string P = @\"C:\\new\"; }";

        var normalized = ModelSource.Normalize(partly);

        Assert.Equal(partly + "\n", normalized.Code);
        Assert.Null(normalized.Repair);
    }

    [Fact]
    public void UnrepairableStrayBackslash_IsLeftUnchangedAndRejected()
    {
        var code = CannedTests.Divide.Replace("Assert.Equal(4,", "Assert.Equal(4 \\ 2,", StringComparison.Ordinal);

        Assert.Equal(code.Trim() + "\n", ModelSource.Normalize(code).Code);
        var result = Assemble("Calculator.Divide", ModelSource.Normalize(code).Code);
        Assert.Contains("malformed-source", Codes(result));
        Assert.False(result.IsValid);
    }

    [Fact]
    public void ValidGeneratedTests_HaveNoMalformedSourceFinding() =>
        Assert.DoesNotContain("malformed-source", Codes(Assemble("Calculator.Divide", CannedTests.Divide)));

    [Fact]
    public void DecodeOnce_KeepsUnknownAndIncompleteEscapes() =>
        Assert.Equal("a\nb \\d \\u12 \u0041", ModelSource.DecodeOnce("a\\nb \\d \\u12 \\u0041"));
}

/// <summary>Scenario references are constrained to the actual plan (#91).</summary>
public sealed class ScenarioReferenceTests : FixtureValidation
{
    private const string AddTests = """
        namespace Shop.Tests;

        public class CalculatorAddTests
        {
            private readonly Calculator _calculator = new();

            [Fact]
            [Trait("Scenario", "S1")]
            public void Add_ReturnsSum() => Assert.Equal(5, _calculator.Add(2, 3));
        %EXTRA%
        }
        """;

    private static string With(string extra) => AddTests.Replace("%EXTRA%", extra, StringComparison.Ordinal);

    [Fact]
    public void SingleScenarioPlan_RejectsAnInventedS2AndNamesTheValidIds()
    {
        var result = Assemble("Calculator.Add", With("""
                [Fact]
                [Trait("Scenario", "S2")]
                public void Add_Negative() => Assert.Equal(-1, _calculator.Add(1, -2));
            """));

        var finding = Assert.Single(result.Findings, f => f.Code == "unknown-scenario");
        Assert.Contains("S2", finding.Message, StringComparison.Ordinal);
        Assert.Contains("valid: S1", finding.Message, StringComparison.Ordinal);
        Assert.False(result.IsValid);
    }

    [Fact]
    public void SingleScenarioPlan_WithOnlyS1_IsValid()
    {
        var result = Assemble("Calculator.Add", With(""));

        Assert.True(result.IsValid, string.Join("; ", result.Findings.Select(f => f.Message)));
        Assert.DoesNotContain("scenario-not-covered", Codes(result, "warning"));
    }

    [Fact]
    public void NonLiteralScenarioTrait_CannotBeVerified()
    {
        var result = Assemble("Calculator.Add", With("""
                private const string Id = "S1";

                [Fact]
                [Trait("Scenario", Id)]
                public void Add_Zero() => Assert.Equal(2, _calculator.Add(2, 0));
            """));

        Assert.Contains("unverifiable-scenario-link", Codes(result));
    }

    [Fact]
    public void MissingCoverage_IsReported()
    {
        var code = CannedTests.Divide.Replace("[Trait(\"Scenario\", \"S2\")]", "[Trait(\"Scenario\", \"S1\")]", StringComparison.Ordinal);

        var result = Assemble("Calculator.Divide", code);

        Assert.Contains(result.Findings, f => f.Code == "scenario-not-covered" && f.Message.Contains("S2", StringComparison.Ordinal));
    }

    [Fact]
    public void Prompt_ListsTheValidScenarioIds()
    {
        var provider = RoslynContextProvider.Load(Fixture, TestContext.Current.CancellationToken);
        var context = provider.GetContext("Calculator.Add", "test");
        var extractor = new CSharpScenarioExtractor();
        var plan = TestWizz.Core.Planning.ScenarioPlanner.Create(context, extractor.Version, extractor.Extract(context));

        var request = XUnitGenerator.BuildRequest(context, plan, XUnitConventions.Detect(Fixture, context));

        Assert.Contains("Valid scenario ids: S1 (link tests only to these)", request.User, StringComparison.Ordinal);
        Assert.Contains("never invent new ids", request.System, StringComparison.Ordinal);
    }
}
