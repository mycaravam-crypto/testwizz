using System.Text.Json.Nodes;
using TestWizz.Cli;
using TestWizz.Cli.Commands;
using TestWizz.Core.Context;
using TestWizz.Core.Planning;

namespace TestWizz.UnitTests;

public sealed class VueScenarioExtractorTests
{
    [Fact]
    public void EmitCallSitesProduceDeterministicEvidenceLinkedReviewScenarios()
    {
        string root = FindVueFixtureRoot();
        ContextDocument context = VueContext.Load(root, "src/components/CartItem.vue", 20_000);
        var extractor = new VueScenarioExtractor();

        var extraction = extractor.Extract(context);

        var scenario = Assert.Single(extraction.Scenarios);
        var evidence = Assert.Single(extraction.Evidence);
        Assert.Equal("vue-extractor/1", extractor.Version);
        Assert.Equal(ScenarioCategory.StateTransition, scenario.Category);
        Assert.Equal("Emits 'select' at line 16", scenario.Title);
        Assert.Equal("Calls emit('select', ...) with 1 payload argument(s).", scenario.ExpectedBehavior);
        Assert.Equal(["E1"], scenario.Evidence);
        Assert.Equal(16, evidence.Line);
        Assert.Equal("call", evidence.Kind);
        Assert.True(scenario.NeedsReview);
        Assert.Equal(VueScenarioExtractor.ReviewReason, scenario.ReviewReason);
        Assert.Empty(extraction.Findings);

        var plan = ScenarioPlanner.Create(context, extractor.Version, extraction);
        Assert.Equal(PlanJson.ContextHash(context), plan.ContextSha256);
        Assert.Equal("S1", Assert.Single(plan.Scenarios).Id);
    }

    [Fact]
    public void NoEmitCallSitesProduceFindingInsteadOfInventedScenarios()
    {
        string root = FindVueFixtureRoot();
        ContextDocument context = VueContext.Load(root, "src/composables/useCart.ts", 20_000);

        var extraction = new VueScenarioExtractor().Extract(context);

        Assert.Empty(extraction.Scenarios);
        Assert.Empty(extraction.Evidence);
        Assert.Contains(extraction.Findings, finding => finding.Code == "no-static-emit-calls");
    }

    [Fact]
    public void PlanCommandSupportsVueContextWithoutAi()
    {
        string root = FindVueFixtureRoot();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var app = new CliApp(stdout, stderr, root, _ => null);

        var exitCode = app.Run(["plan", "--for", "src/components/CartItem.vue", "--format", "json"], TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.Success, exitCode);
        Assert.Empty(stderr.ToString());
        var output = JsonNode.Parse(stdout.ToString())!;
        var plan = output["result"]!;
        Assert.Equal("testwizz.plan/v1", (string?)plan["schema"]);
        Assert.Equal("vue", (string?)plan["stack"]);
        Assert.Equal("vue-extractor/1", (string?)plan["generator"]?["extractorVersion"]);
        Assert.Equal("select", (string?)plan["scenarios"]?[0]?["title"]?.ToString()?.Split('\'')[1]);
        Assert.Equal("E1", (string?)plan["scenarios"]?[0]?["evidence"]?[0]);
    }

    private static string FindVueFixtureRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string fixtureRoot = Path.Combine(directory.FullName, "fixtures", "vue");
            if (File.Exists(Path.Combine(fixtureRoot, "package.json")))
                return fixtureRoot;
        }

        throw new DirectoryNotFoundException("Could not locate the Vue fixture project root.");
    }
}
