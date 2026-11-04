using System.Text.Json.Nodes;
using TestWizz.Cli.Commands;

namespace TestWizz.UnitTests;

public class UiCommandTests
{
    [Fact]
    public void Criteria_ParsesOnlyMarkdownBullets()
    {
        var items = UiCommand.ParseCriteria("# Checkout\n- Invalid address shows an error\n* Successful submit creates an order\n");
        Assert.Equal(["AC1", "AC2"], items.Select(x => x.Id));
        Assert.Equal("Invalid address shows an error", items[0].Text);
    }

    [Fact]
    public void Validation_RejectsMissingAndWeakScenarios()
    {
        var criteria = UiCommand.ParseCriteria("- Error appears\n- Save succeeds\n");
        var response = JsonNode.Parse("""
            {"tests":[{"id":"AC1","title":"Error","body":"await page.getByRole('button').click();"}],"assumptions":[]}
            """)!.AsObject();
        var result = UiCommand.Validate(response, criteria);
        Assert.Contains(result.Findings, x => x.Code == "ui.unsafe_or_weak");
        Assert.Contains(result.Findings, x => x.Code == "ui.uncovered");
    }

    [Fact]
    public void Validation_RejectsUnknownDuplicateAndExecutableApis()
    {
        var criteria = UiCommand.ParseCriteria("- Error appears\n");
        var response = JsonNode.Parse("""
            {"tests":[
                {"id":"AC1","title":"Error","body":"await page.evaluate(() => 1); expect(true).toBe(true);"},
                {"id":"AC1","title":"Duplicate","body":"expect(true).toBe(true);"},
                {"id":"AC99","title":"Invented","body":"expect(true).toBe(true);"}
            ],"assumptions":[]}
            """)!.AsObject();
        var result = UiCommand.Validate(response, criteria);
        Assert.Contains(result.Findings, x => x.Code == "ui.unsafe_or_weak");
        Assert.Equal(2, result.Findings.Count(x => x.Code == "ui.invalid_test"));
    }

    private static string RequirementJson(string items = "[{\"index\":1,\"text\":\"Submit creates an order\"}]", int revision = 4) =>
        $$"""
        {
          "schema": "ado.requirement-context/v1",
          "server": "https://tfs.example.test/tfs/DefaultCollection",
          "id": 4711,
          "revision": {{revision}},
          "currentRevision": 4,
          "isCurrent": {{(revision == 4 ? "true" : "false")}},
          "webUrl": "https://tfs.example.test/Platform/_workitems/edit/4711",
          "project": "Platform",
          "acceptanceCriteria": {
            "sourceField": "Microsoft.VSTS.Common.AcceptanceCriteria",
            "items": {{items}}
          }
        }
        """;

    [Fact]
    public void AdoCriteria_ParsesStructuredAcceptanceCriteriaWithProvenance()
    {
        var result = AdoUiCriteria.Parse(RequirementJson(
            "[{\"index\":1,\"text\":\"First criterion\"},{\"index\":2,\"text\":\"Second criterion\"}]"), 4711);
        Assert.Equal(4711, result.Id);
        Assert.Equal(4, result.Revision);
        Assert.Equal("AC1", result.Criteria[0].Id);
        Assert.Equal("Second criterion", result.Criteria[1].Text);
        Assert.Contains("- First criterion", result.CriteriaMarkdown, StringComparison.Ordinal);
        Assert.Equal(64, result.Sha256.Length);
    }

    [Fact]
    public void AdoCriteria_RejectsMissingCriteria()
    {
        var ex = Assert.Throws<CliException>(() => AdoUiCriteria.Parse(RequirementJson("[]"), 4711));
        Assert.Equal("ui.criteria_count", ex.Code);
    }

    [Fact]
    public void AdoCriteria_RejectsWrongWorkItemAndHistoricalRevision()
    {
        Assert.Equal("ui.ado_contract",
            Assert.Throws<CliException>(() => AdoUiCriteria.Parse(RequirementJson(), 4712)).Code);
        Assert.Equal("ui.ado_revision",
            Assert.Throws<CliException>(() => AdoUiCriteria.Parse(RequirementJson(revision: 3), 4711)).Code);
    }

    [Fact]
    public void AdoCriteria_RejectsMalformedJsonAndInvalidItems()
    {
        Assert.Equal("ui.ado_contract",
            Assert.Throws<CliException>(() => AdoUiCriteria.Parse("{", 4711)).Code);
        Assert.Equal("ui.ado_contract",
            Assert.Throws<CliException>(() => AdoUiCriteria.Parse(
                RequirementJson("[{\"index\":2,\"text\":\"Invalid order\"}]"), 4711)).Code);
    }

    [Fact]
    public void Ui_CriteriaSourcesAreMutuallyExclusive()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var cli = new TestWizz.Cli.CliApp(stdout, stderr, Path.GetTempPath());
        var exit = cli.Run([
            "ui", "--url", "http://127.0.0.1:5173/",
            "--criteria", "criteria.md", "--ado-workitem", "4711", "--format", "json"]);
        Assert.Equal(2, exit);
        Assert.Contains("usage", stdout.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://localhost:5173/new")]
    [InlineData("http://example.com:5173/new")]
    [InlineData("http://user:pass@localhost:5173/new")]
    public void Url_RejectsNonLocalOrCredentialedOrigins(string url)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = new TestWizz.Cli.CliApp(stdout, stderr, Path.GetTempPath()).Run(
            ["ui", "--url", url, "--criteria", "example.md", "--format", "json"], TestContext.Current.CancellationToken);
        Assert.Equal(2, exit);
        Assert.Contains("ui.invalid_url", stdout.ToString(), StringComparison.Ordinal);
    }
}
