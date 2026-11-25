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

/// <summary>Member accessibility context and testability guidance (#92).</summary>
public sealed class MemberAccessTests : FixtureValidation
{
    private const string BackgroundTask = """
        namespace Shop;

        public class BackgroundTask
        {
            public DateTime StartTime { get; private set; }
            protected TimeSpan Intervall { get; set; } = TimeSpan.FromMinutes(1);
            private int _runs;
            internal int Runs => _runs;
            public bool IsRunning { get; private set; }

            public void Start(DateTime now)
            {
                StartTime = now;
                IsRunning = true;
                _runs++;
            }
        }
        """;

    private const string Header = """
        namespace Shop.Tests;

        public class BackgroundTaskStartTests
        {
            [Fact]
            [Trait("Scenario", "S1")]
            public void Start_SetsStartTime()
            {
                var task = new BackgroundTask();
        """;

    private const string Footer = """

                task.Start(new DateTime(2026, 1, 1));
                Assert.True(task.IsRunning);
            }
        }
        """;

    public MemberAccessTests() => File.WriteAllText(Path.Combine(Fixture, "src", "Shop", "BackgroundTask.cs"), BackgroundTask);

    private MemberAccess.Report Report()
    {
        var provider = RoslynContextProvider.Load(Fixture, TestContext.Current.CancellationToken);
        var context = provider.GetContext("BackgroundTask.Start", "test");
        return MemberAccess.Describe(provider, context, XUnitConventions.Detect(Fixture, context))!;
    }

    private void GrantInternals() =>
        File.WriteAllText(Path.Combine(Fixture, "src", "Shop", "Shop.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><InternalsVisibleTo Include="$(AssemblyName).Tests" /></ItemGroup></Project>""");

    [Fact]
    public void Report_SeparatesAccessibleFromInaccessibleMembers()
    {
        var report = Report();

        Assert.False(report.InternalsVisible);
        Assert.Contains(report.Accessible, a => a.StartsWith("DateTime StartTime", StringComparison.Ordinal) && a.Contains("read-only from tests (private set)", StringComparison.Ordinal));
        Assert.Contains(report.Accessible, a => a.StartsWith("void Start(DateTime)", StringComparison.Ordinal));
        Assert.Contains(report.Inaccessible, a => a.StartsWith("protected TimeSpan Intervall", StringComparison.Ordinal));
        Assert.Contains(report.Inaccessible, a => a.StartsWith("private int _runs", StringComparison.Ordinal));
        Assert.Contains(report.Inaccessible, a => a.StartsWith("internal int Runs", StringComparison.Ordinal));
    }

    [Fact]
    public void InternalsVisibleToProjectItem_MakesInternalMembersAccessible()
    {
        GrantInternals();

        var report = Report();

        Assert.True(report.InternalsVisible);
        Assert.Contains(report.Accessible, a => a.StartsWith("int Runs", StringComparison.Ordinal));
    }

    [Fact]
    public void Prompt_CarriesAccessibilityAndTheNoBypassRule()
    {
        var provider = RoslynContextProvider.Load(Fixture, TestContext.Current.CancellationToken);
        var context = provider.GetContext("BackgroundTask.Start", "test");
        var extractor = new CSharpScenarioExtractor();
        var plan = TestWizz.Core.Planning.ScenarioPlanner.Create(context, extractor.Version, extractor.Extract(context));
        var conventions = XUnitConventions.Detect(Fixture, context);

        var request = XUnitGenerator.BuildRequest(context, plan, conventions, MemberAccess.PromptLines(MemberAccess.Describe(provider, context, conventions)));

        Assert.Contains("## What tests can use of BackgroundTask", request.User, StringComparison.Ordinal);
        Assert.Contains("- protected TimeSpan Intervall", request.User, StringComparison.Ordinal);
        Assert.Contains("never use reflection", request.System, StringComparison.Ordinal);
        Assert.Contains("Untestable <id>", request.System, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("task.StartTime = DateTime.MinValue;", "read-only-member")]
    [InlineData("var i = task.Intervall;", "inaccessible-member")]
    [InlineData("var r = task.Runs;", "inaccessible-member")]
    [InlineData("var f = typeof(BackgroundTask).GetField(\"_runs\", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);", "encapsulation-bypass")]
    public void BypassingEncapsulation_IsAnError(string statement, string code)
    {
        var result = Assemble("BackgroundTask.Start", Header + "\n        " + statement + Footer);

        Assert.Contains(code, Codes(result));
    }

    [Fact]
    public void InternalMember_IsFineWithInternalsVisibleTo()
    {
        GrantInternals();

        var result = Assemble("BackgroundTask.Start", Header + "\n        var r = task.Runs;" + Footer);

        Assert.DoesNotContain("inaccessible-member", Codes(result));
    }

    [Fact]
    public void PublicBehaviorTest_IsValid()
    {
        var result = Assemble("BackgroundTask.Start", Header + "\n        var before = task.StartTime;" + Footer);

        Assert.True(result.IsValid, string.Join("; ", result.Findings.Select(f => f.Message)));
    }
}

/// <summary>xUnit assertion APIs are validated against the detected xUnit version (#93).</summary>
public sealed class AssertionApiTests : FixtureValidation
{
    private static string With(string statement) => CannedTests.Divide.Replace(
        "Assert.Equal(4, _calculator.Divide(8, 2));", "Assert.Equal(4, _calculator.Divide(8, 2));\n        " + statement, StringComparison.Ordinal);

    private void UseXunit2() =>
        File.WriteAllText(Path.Combine(Fixture, "tests", "Shop.Tests", "Shop.Tests.csproj"), File.ReadAllText(Path.Combine(Fixture, "tests", "Shop.Tests", "Shop.Tests.csproj"))
            .Replace("<PackageReference Include=\"xunit.v3\" Version=\"3.2.2\" />", "<PackageReference Include=\"xunit\" Version=\"2.9.3\" />", StringComparison.Ordinal));

    [Fact]
    public void DoesNotThrow_IsRejectedWithAnAlternative()
    {
        var result = Assemble("Calculator.Divide", With("Assert.DoesNotThrow(() => _calculator.Divide(1, 1));"));

        var finding = Assert.Single(result.Findings, f => f.Code == "unsupported-assertion");
        Assert.Contains("Record.Exception", finding.Message, StringComparison.Ordinal);
        Assert.Contains("xunit.v3 3.2.2", finding.Message, StringComparison.Ordinal);
        Assert.False(result.IsValid);
    }

    [Theory]
    [InlineData("Assert.Equals(1, 1);")]
    [InlineData("Assert.ReferenceEquals(_calculator, _calculator);")]
    [InlineData("Assert.AreEqual(1, 1);")]
    public void AssertionsFromOtherFrameworksOrObject_AreRejected(string statement) =>
        Assert.Contains("unsupported-assertion", Codes(Assemble("Calculator.Divide", With(statement))));

    [Theory]
    [InlineData("Assert.InRange(_calculator.Divide(8, 2), 3, 5);")]
    [InlineData("Assert.Multiple(() => Assert.True(true), () => Assert.False(false));")]
    [InlineData("Assert.Skip(\"not on CI\");")]
    [InlineData("var ex = Record.Exception(() => _calculator.Divide(1, 1)); Assert.Null(ex);")]
    public void LegitimateXunitV3Assertions_AreAccepted(string statement) =>
        Assert.DoesNotContain("unsupported-assertion", Codes(Assemble("Calculator.Divide", With(statement))));

    [Fact]
    public void V3OnlyAssertion_IsRejectedForAnXunit2Project()
    {
        UseXunit2();

        var finding = Assert.Single(Assemble("Calculator.Divide", With("Assert.Skip(\"later\");")).Findings, f => f.Code == "unsupported-assertion");

        Assert.Contains("exists only in xUnit v3", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CustomAssertHelper_IsNotTreatedAsXunit()
    {
        var code = With("Assert.DoesNotThrow(() => _calculator.Divide(1, 1));") + """

            internal static class Assert
            {
                public static void DoesNotThrow(Action action) => action();
                public static void Equal<T>(T expected, T actual) { }
                public static void Throws<T>(Action action) { }
            }
            """;

        Assert.DoesNotContain("unsupported-assertion", Codes(Assemble("Calculator.Divide", code)));
    }

    [Fact]
    public void V3List_MatchesTheRealXunitAssertType()
    {
        var real = typeof(Xunit.Assert).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName).Select(m => m.Name).Distinct().Order(StringComparer.Ordinal).ToList();

        // ReferenceEquals/Equals exist only to throw; they are deliberately reported as unsupported.
        Assert.Equal(real.Except(["Equals", "ReferenceEquals"]), XUnitAssertApi.V3.Order(StringComparer.Ordinal));
    }
}

/// <summary>Generated helper interface implementations and dependency types (#94).</summary>
public sealed class GeneratedHelperTests : FixtureValidation
{
    private const string Worker = """
        using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Logging;

        namespace Shop.Jobs;

        public class Worker
        {
            private readonly IServiceScopeFactory _scopes;

            public Worker(IServiceScopeFactory scopes, ILogger<Worker> logger) => _scopes = scopes;

            public bool Run()
            {
                using var scope = _scopes.CreateScope();
                return scope.ServiceProvider is not null;
            }
        }
        """;

    private const string CompleteFakes = """
        private sealed class FakeLogger : ILogger<Worker>
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
        }

        private sealed class FakeScope : IServiceScope
        {
            public IServiceProvider ServiceProvider { get; } = new FakeProvider();
            public void Dispose() { }
        }

        private sealed class FakeProvider : IServiceProvider
        {
            public object? GetService(Type serviceType) => null;
        }

        private sealed class FakeScopeFactory : IServiceScopeFactory
        {
            public IServiceScope CreateScope() => new FakeScope();
        }
        """;

    public GeneratedHelperTests()
    {
        Directory.CreateDirectory(Path.Combine(Fixture, "src", "Shop", "Jobs"));
        File.WriteAllText(Path.Combine(Fixture, "src", "Shop", "Jobs", "Worker.cs"), Worker);
    }

    private static string Tests(string fakes, string factory = "new FakeScopeFactory()", string usings = "using Shop.Jobs;\n") => $$"""
        {{usings}}using Microsoft.Extensions.DependencyInjection;
        using Microsoft.Extensions.Logging;

        namespace Shop.Tests;

        public class WorkerRunTests
        {
            [Fact]
            [Trait("Scenario", "S1")]
            public void Run_CreatesAScope()
            {
                var worker = new Worker({{factory}}, new FakeLogger());

                Assert.True(worker.Run());
            }

        {{fakes}}
        }
        """;

    private TestWizz.Core.Generation.GenerationResult Check(string code) => Assemble("Worker.Run", code);

    [Fact]
    public void CompleteFakes_AreValid()
    {
        var result = Check(Tests(CompleteFakes));

        Assert.DoesNotContain(result.Findings, f => f.Code is "incomplete-fake" or "argument-type-mismatch" or "missing-using" or "unverified-fake");
    }

    [Fact]
    public void LoggerFakeWithoutIsEnabled_IsIncomplete()
    {
        var fakes = CompleteFakes.Replace("    public bool IsEnabled(LogLevel logLevel) => true;\n", "", StringComparison.Ordinal);

        var finding = Assert.Single(Check(Tests(fakes)).Findings, f => f.Code == "incomplete-fake");

        Assert.Contains("FakeLogger implements Microsoft.Extensions.Logging.ILogger but does not declare IsEnabled", finding.Message, StringComparison.Ordinal);
        Assert.Contains("known members", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScopeFakeWithoutServiceProvider_IsIncomplete()
    {
        var fakes = CompleteFakes.Replace("    public IServiceProvider ServiceProvider { get; } = new FakeProvider();\n", "", StringComparison.Ordinal);

        Assert.Contains(Check(Tests(fakes)).Findings, f => f.Code == "incomplete-fake" && f.Message.Contains("ServiceProvider", StringComparison.Ordinal));
    }

    [Fact]
    public void FactoryFakeThatIsNotAnIServiceScopeFactory_IsAnArgumentMismatch()
    {
        var fakes = CompleteFakes + "\n    private sealed class NotAFactory : IDisposable { public void Dispose() { } }";

        var finding = Assert.Single(Check(Tests(fakes, "new NotAFactory()")).Findings, f => f.Code == "argument-type-mismatch");

        Assert.Contains("expects IServiceScopeFactory", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryInterfaceFake_IsVerifiedByCompilation()
    {
        var fakes = CompleteFakes + "\n    private sealed class FakeClock : Shop.IClock { }";

        var finding = Assert.Single(Check(Tests(fakes)).Findings, f => f.Code == "incomplete-fake");

        Assert.Contains("verified by compilation", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepositoryTypeWithoutItsUsing_IsAMissingUsing()
    {
        var finding = Assert.Single(Check(Tests(CompleteFakes, usings: "")).Findings, f => f.Code == "missing-using" && f.Message.Contains("Worker", StringComparison.Ordinal));

        Assert.Contains("using Shop.Jobs;", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownUnresolvedInterface_IsOnlyAWarning()
    {
        var fakes = CompleteFakes + "\n    private sealed class FakeLegacy : ILegacyGateway { }";

        var result = Check(Tests(fakes));

        Assert.Contains("unverified-fake", Codes(result, "warning"));
        Assert.DoesNotContain("unverified-fake", Codes(result));
    }
}
