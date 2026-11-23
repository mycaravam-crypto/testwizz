using TestWizz.Cli;
using TestWizz.Cli.Commands;

namespace TestWizz.UnitTests;

public sealed class JourneyDraftBatchTests
{
    private const string Plan = """
        {"format":"ado-testplan/1","project":"Shop","plan":{"id":12},
         "suites":[{"testCaseIds":[512,513]},{"testCaseIds":[512,514]}],
         "testCases":[{"id":512},{"id":513},{"id":514}]}
        """;

    private const string Trace = """
        {"format":"ado.test-traceability/v1","project":"Shop","area":"Shop",
         "planId":12,"requirements":[
           {"id":4711,"testCaseIds":[512,513,999]},
           {"id":4712,"testCaseIds":[512]},
           {"id":4711,"testCaseIds":[512]}
         ]}
        """;

    [Fact]
    public void Select_UsesOnlyExplicitLinks_AndDeduplicatesSuitesAndPairs()
    {
        var selected = JourneyDraftBatch.Select(Plan, Trace, 12);
        Assert.Equal([512, 513, 514], selected.CaseIds);
        Assert.Equal(
            [(512, 4711), (512, 4712), (513, 4711)],
            selected.Links.Select(link => (link.TestCaseId, link.WorkItemId)).ToArray());
    }

    [Fact]
    public void Select_RejectsMismatchedPlanAndTrace()
    {
        var wrongPlan = Trace.Replace("\"planId\":12", "\"planId\":13", StringComparison.Ordinal);
        Assert.Equal("journey_batch.trace",
            Assert.Throws<CliException>(() => JourneyDraftBatch.Select(Plan, wrongPlan, 12)).Code);
        Assert.Equal("journey_batch.plan",
            Assert.Throws<CliException>(() => JourneyDraftBatch.Select(Plan, Trace, 13)).Code);
    }

    [Fact]
    public void Batch_RequiresExplicitLocalRouteBeforeFetchingADO()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var cli = new CliApp(stdout, stderr, Path.GetTempPath(), _ => null);
        var code = cli.Run(["journey-draft", "--plan-id", "12", "--format", "json"],
            TestContext.Current.CancellationToken);
        Assert.Equal(2, code);
        Assert.Contains("journey_batch.url", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Batch_RejectsIncompatibleSingleCaseOptions()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var cli = new CliApp(stdout, stderr, Path.GetTempPath(), _ => null);
        var code = cli.Run(["journey-draft", "--plan-id", "12", "--case", "512", "--format", "json"],
            TestContext.Current.CancellationToken);
        Assert.Equal(2, code);
        Assert.Contains("\"code\": \"usage\"", stdout.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Batch_RejectsRemoteBrowserURL()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var cli = new CliApp(stdout, stderr, Path.GetTempPath(), _ => null);
        var code = cli.Run(
            ["journey-draft", "--plan-id", "12", "--url", "https://public.example.test", "--format", "json"],
            TestContext.Current.CancellationToken);
        Assert.Equal(2, code);
        Assert.Contains("journey_draft.invalid_url", stdout.ToString(), StringComparison.Ordinal);
    }
}
