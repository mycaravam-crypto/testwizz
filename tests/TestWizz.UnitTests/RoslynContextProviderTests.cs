using System.Diagnostics;
using System.Text.Json.Nodes;
using TestWizz.Cli;
using TestWizz.Core.Context;
using TestWizz.DotNet;

namespace TestWizz.UnitTests;

internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string DotNetFixture => Path.Combine(Root, "fixtures", "dotnet");

    public static string Golden => Path.Combine(Root, "tests", "TestWizz.UnitTests", "Golden");

    /// <summary>Copies a directory, skipping build output, into a fresh temp directory outside any git repository.</summary>
    public static string CopyToTemp(string source)
    {
        var target = Directory.CreateTempSubdirectory("testwizz-fixture-").FullName;
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(p => p is "bin" or "obj"))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, relative))!);
            File.Copy(file, Path.Combine(target, relative));
        }

        return target;
    }

    private static string FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TestWizz.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (TestWizz.slnx) not found.");
    }
}

public sealed class RoslynContextProviderTests : IDisposable
{
    private readonly string _fixture = RepoPaths.CopyToTemp(RepoPaths.DotNetFixture);
    private readonly List<string> _cleanup = [];

    public RoslynContextProviderTests() => _cleanup.Add(_fixture);

    public void Dispose()
    {
        foreach (var directory in _cleanup)
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private ContextDocument Context(string target, int maxChars = RoslynContextProvider.DefaultMaxExcerptChars) =>
        RoslynContextProvider.Load(_fixture).GetContext(target, "test", maxChars);

    /// <summary>Set TESTWIZZ_UPDATE_GOLDEN=1 to rewrite the golden files after an intentional change.</summary>
    [Theory]
    [InlineData("Calculator.Divide", "calculator-divide.json")]
    [InlineData("Calculator.Percentage(decimal, int)", "calculator-percentage-int.json")]
    [InlineData("OrderService.PlaceOrderAsync", "orderservice-placeorderasync.json")]
    [InlineData("OrderService", "orderservice.json")]
    public void Context_MatchesGolden(string target, string golden)
    {
        var actual = ContextJson.ToJson(Context(target)).ToJsonString(CliOutput.JsonOptions) + "\n";
        var path = Path.Combine(RepoPaths.Golden, golden);

        if (Environment.GetEnvironmentVariable("TESTWIZZ_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(RepoPaths.Golden);
            File.WriteAllText(path, actual);
        }

        Assert.True(File.Exists(path), $"Golden file {golden} is missing. Run the tests with TESTWIZZ_UPDATE_GOLDEN=1.");
        var expected = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var observed = JsonNode.Parse(actual)!.AsObject();
        Assert.Equal(typeof(Microsoft.CodeAnalysis.CSharp.CSharpCompilation).Assembly.GetName().Version!.ToString(),
            (string?)observed["provenance"]!["providerVersion"]);
        expected["provenance"]!.AsObject().Remove("providerVersion");
        observed["provenance"]!.AsObject().Remove("providerVersion");
        Assert.True(JsonNode.DeepEquals(expected, observed));
    }

    [Fact]
    public void Discovery_ReturnsDistinctOverloadSafeProductionMethods()
    {
        var methods = RoslynContextProvider.Load(_fixture).DiscoverMethods();
        Assert.Contains("M:Shop.Calculator.Divide(System.Int32,System.Int32)", methods);
        Assert.Contains("M:Shop.Calculator.Percentage(System.Decimal,System.Int32)", methods);
        Assert.DoesNotContain(methods, id => id.Contains("CalculatorTests", StringComparison.Ordinal));
        Assert.Equal(methods.Count, methods.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(methods.OrderBy(x => x, StringComparer.Ordinal), methods);
    }

    [Fact]
    public void Context_IsDeterministic()
    {
        var first = ContextJson.ToJson(Context("OrderService")).ToJsonString();
        var second = ContextJson.ToJson(RoslynContextProvider.Load(_fixture).GetContext("OrderService", "test")).ToJsonString();

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("Shop.Calculator.Add")]
    [InlineData("Calculator.Add")]
    [InlineData("Calculator.Add(int,int)")]
    [InlineData("M:Shop.Calculator.Add(System.Int32,System.Int32)")]
    public void Target_AcceptsSeveralSpellings(string target)
    {
        Assert.Equal("M:Shop.Calculator.Add(System.Int32,System.Int32)", Context(target).Target.Id);
    }

    [Fact]
    public void Overloads_AreAmbiguousWithCandidates()
    {
        var ex = Assert.Throws<ContextException>(() => Context("Calculator.Percentage"));

        Assert.Equal("target.ambiguous", ex.Code);
        Assert.Equal(["Calculator.Percentage(decimal, decimal)", "Calculator.Percentage(decimal, int)"], ex.Candidates);
    }

    [Fact]
    public void UnknownTarget_SuggestsSimilarNames()
    {
        var ex = Assert.Throws<ContextException>(() => Context("Calculator.Multiply"));

        Assert.Equal("target.not_found", ex.Code);
        Assert.Empty(ex.Candidates);

        var similar = Assert.Throws<ContextException>(() => Context("orderservice.place"));
        Assert.Contains("OrderService.PlaceOrderAsync(Guid, IReadOnlyList<OrderLine>, CancellationToken)", similar.Candidates);
    }

    [Fact]
    public void TestMethods_AreNotTargets()
    {
        Assert.Equal("target.not_found", Assert.Throws<ContextException>(() => Context("CalculatorTests.Add_ReturnsSum")).Code);
    }

    [Fact]
    public void ConstructorDependencies_AreMockCandidatesWithSignatures()
    {
        var context = Context("OrderService.CancelAsync");

        var repository = context.Dependencies.Single(d => d.Role == "constructor" && d.ParameterName == "repository");
        Assert.Equal("Shop.IOrderRepository", repository.Type);
        Assert.Equal("interface", repository.Kind);
        Assert.Equal(new SourceLocation("src/Shop/Orders.cs", 7), repository.DeclaredIn);
        Assert.Equal(
            ["Task<Order?> FindAsync(Guid id, CancellationToken cancellationToken)", "Task SaveAsync(Order order, CancellationToken cancellationToken)"],
            repository.Members);
    }

    [Fact]
    public void RecordDependencies_IncludePrimaryConstructor()
    {
        var line = Context("OrderService.PlaceOrderAsync").Dependencies.Single(d => d.Role == "parameter");

        Assert.Equal("Shop.OrderLine", line.Type);
        Assert.Equal("record OrderLine(string Sku, int Quantity, decimal UnitPrice)", line.Members[0]);
    }

    [Fact]
    public void Throws_IncludeGuardHelpersAndThrowExpressions()
    {
        Assert.Equal(
            ["System.ArgumentNullException", "System.ArgumentException", "System.ArgumentException", "Shop.OrderLimitExceededException"],
            Context("OrderService.PlaceOrderAsync").Throws.Select(t => t.Type));
        Assert.Equal(
            ["System.ArgumentNullException", "System.ArgumentNullException"],
            Context("OrderService").Throws.Take(2).Select(t => t.Type));
    }

    [Fact]
    public void RelatedTests_AreNameReferences()
    {
        Assert.Equal(["CalculatorTests.Divide_ReturnsQuotient"], Context("Calculator.Divide").RelatedTests.Select(t => t.Name));
        Assert.Empty(Context("Calculator.Percentage(decimal, int)").RelatedTests);
        Assert.Contains(Context("Calculator").Unavailable, u => u.Field == "coverage");
    }

    [Fact]
    public void LongSource_IsTruncatedAtLineBoundary()
    {
        var source = Context("OrderService.PlaceOrderAsync", maxChars: 150).Source;

        Assert.True(source.Truncated);
        Assert.True(source.Excerpt.Length <= 150);
        Assert.EndsWith("\n", source.Excerpt, StringComparison.Ordinal);
    }

    [Fact]
    public void ExternalTypes_AreReportedNotGuessed()
    {
        var repo = Directory.CreateTempSubdirectory("testwizz-ext-").FullName;
        _cleanup.Add(repo);
        File.WriteAllText(Path.Combine(repo, "Mapper.cs"), """
            using Newtonsoft.Json.Linq;
            namespace App;
            public class Mapper(ILoggerFactory loggers)
            {
                public string Map(JObject input) => input.ToString();
            }
            """);

        var context = RoslynContextProvider.Load(repo).GetContext("Mapper.Map", "test");

        Assert.Contains(context.Diagnostics, d => d.Code == "unresolved-type" && d.Message.Contains("JObject", StringComparison.Ordinal));
        var logger = context.Dependencies.Single(d => d.Role == "constructor");
        Assert.Null(logger.DeclaredIn);
        Assert.Null(logger.Kind);
        Assert.Null(context.Project);
    }

    [Fact]
    public void GitRepository_UsesRevisionAndRespectsGitignore()
    {
        File.WriteAllText(Path.Combine(_fixture, ".gitignore"), "generated/\n");
        Directory.CreateDirectory(Path.Combine(_fixture, "generated"));
        File.WriteAllText(Path.Combine(_fixture, "generated", "Ignored.cs"), "namespace Shop; public class IgnoredThing { public void Run() { } }");
        Git("init", "-q");
        Git("add", ".");
        Git("-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "fixture");
        var head = Git("rev-parse", "HEAD").Trim();

        var provider = RoslynContextProvider.Load(_fixture);

        Assert.Equal(head, provider.GetContext("Calculator.Add", "test").Revision);
        Assert.Equal("target.not_found", Assert.Throws<ContextException>(() => provider.GetContext("IgnoredThing", "test")).Code);
    }

    [Fact]
    public void Context_DoesNotModifyTheRepository()
    {
        var before = Snapshot();

        Context("OrderService");

        Assert.Equal(before, Snapshot());
    }

    private string[] Snapshot() =>
        [.. Directory.EnumerateFileSystemEntries(_fixture, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(p => $"{p}:{(File.Exists(p) ? new FileInfo(p).Length + ":" + File.GetLastWriteTimeUtc(p).Ticks : "<dir>")}")];

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = _fixture, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {error}");
        return output;
    }
}

public sealed class ContextCommandTests : IDisposable
{
    private readonly string _fixture = RepoPaths.CopyToTemp(RepoPaths.DotNetFixture);

    public void Dispose() => Directory.Delete(_fixture, recursive: true);

    private (int ExitCode, string Stdout, string Stderr) Run(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = new CliApp(stdout, stderr, _fixture).Run(args);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Json_WrapsContextDocument()
    {
        var (exit, stdout, stderr) = Run("context", "--for", "Calculator.Divide", "--format", "json");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Empty(stderr);
        var result = JsonNode.Parse(stdout)!["result"]!;
        Assert.Equal("testwizz.context/v1", (string)result["schema"]!);
        Assert.Equal("src/Shop/Calculator.cs", (string)result["source"]!["file"]!);
    }

    [Fact]
    public void Text_SummarizesContext()
    {
        var (exit, stdout, _) = Run("context", ".", "--for", "OrderService.PlaceOrderAsync");

        Assert.Equal(ExitCodes.Success, exit);
        Assert.Contains("[constructor] Shop.IClock clock", stdout, StringComparison.Ordinal);
        Assert.Contains("Shop.OrderLimitExceededException", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void Ambiguous_ListsCandidatesInJsonError()
    {
        var (exit, stdout, stderr) = Run("context", "--for", "Calculator.Percentage", "--format", "json");

        Assert.Equal(ExitCodes.Error, exit);
        var error = JsonNode.Parse(stdout)!["error"]!;
        Assert.Equal("target.ambiguous", (string)error["code"]!);
        Assert.Equal(2, error["details"]!.AsArray().Count);
        Assert.Contains("candidate: Calculator.Percentage(decimal, int)", stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("context")]
    [InlineData("context", "--for", "")]
    [InlineData("context", "--for", "Calculator.Add", "--max-chars", "ten")]
    [InlineData("context", "--for", "Calculator.Add", "--max-chars", "5")]
    [InlineData("context", "missing-dir", "--for", "Calculator.Add")]
    public void InvalidUsage_ExitsTwo(params string[] args)
    {
        Assert.Equal(ExitCodes.Error, Run(args).ExitCode);
    }
}
