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

    [Theory]
    [InlineData("https://localhost:5173/new")]
    [InlineData("http://example.com:5173/new")]
    [InlineData("http://user:pass@localhost:5173/new")]
    public void Url_RejectsNonLocalOrCredentialedOrigins(string url)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = new TestWizz.Cli.CliApp(stdout, stderr, Path.GetTempPath()).Run(
            ["ui", "--url", url, "--criteria", "example.md", "--format", "json"]);
        Assert.Equal(2, exit);
        Assert.Contains("ui.invalid_url", stdout.ToString(), StringComparison.Ordinal);
    }
}
