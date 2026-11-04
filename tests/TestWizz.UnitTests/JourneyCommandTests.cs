using TestWizz.Cli;
using TestWizz.Cli.Commands;

namespace TestWizz.UnitTests;

public sealed class JourneyCommandTests
{
    private const string Reviewed = """
        {
          "schema": "testwizz.journey/v1",
          "steps": [
            {"stepId": 2, "action": "click", "role": "button", "name": "New",
             "expectRole": "heading", "expectName": "New order"},
            {"stepId": 3, "action": "fill", "label": "Customer", "value": "Fixture",
             "expectText": "Fixture"}
          ]
        }
        """;

    [Fact]
    public void ReviewedJourney_RequiresAssertionsAndUniqueStepIds()
    {
        var parsed = JourneyCommand.ParseSpec(Reviewed);
        Assert.Equal(2, parsed.Steps.Count);
        Assert.Equal("journey.invalid_steps", Assert.Throws<CliException>(() =>
            JourneyCommand.ParseSpec(Reviewed.Replace("\"expectText\": \"Fixture\"", "\"unexpected\": \"value\""))).Code);
        Assert.Equal("journey.invalid_steps", Assert.Throws<CliException>(() =>
            JourneyCommand.ParseSpec(Reviewed.Replace("\"stepId\": 3", "\"stepId\": 2"))).Code);
    }

    [Fact]
    public void AdoPlan_PreservesTestCaseRevisionAndStepSequence()
    {
        var plan = """
            {"format":"ado-testplan/1","project":"Shop","plan":{"id":12},
             "suites":[{"testCaseIds":[512]}],
             "testCases":[{"id":512,"rev":3,"title":"Create order","steps":[
               {"id":2,"action":"<b>Click New</b>","expectedResult":"Form shown"},
               {"id":3,"action":"Fill customer","expectedResult":"Customer entered"}
             ]}]}
            """;
        var parsed = JourneyCommand.ParseCase(plan, 512);
        Assert.Equal(3, parsed.Revision);
        Assert.Equal([2, 3], parsed.Steps.Select(x => x.Id));
        Assert.Equal("Click New", parsed.Steps[0].Action);
        Assert.Equal("journey.case_missing", Assert.Throws<CliException>(() =>
            JourneyCommand.ParseCase(plan, 999)).Code);
    }

    [Fact]
    public void Capture_RejectsNonLoopbackBeforeOpeningEvidence()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = new CliApp(stdout, stderr, Path.GetTempPath()).Run(
            ["journey", "--url", "https://public.example.test/orders", "--format", "json"],
            TestContext.Current.CancellationToken);
        Assert.Equal(2, code);
        Assert.Contains("journey.invalid_url", stdout.ToString(), StringComparison.Ordinal);
    }
}
