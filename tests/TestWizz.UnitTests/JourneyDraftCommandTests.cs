using System.Text.Json.Nodes;
using TestWizz.Cli;
using TestWizz.Cli.Commands;

namespace TestWizz.UnitTests;

public sealed class JourneyDraftCommandTests
{
    private static readonly JourneyCommand.TestCase Case = new(
        512, 3, "Create order", "Shop", 12,
        [new JourneyCommand.CaseStep(2, "Enter address", "Address entered"),
         new JourneyCommand.CaseStep(3, "Save order", "Order created")]);

    private const string Proposal = """
        {
          "steps": [
            { "stepId": 2, "action": "fill", "label": "Delivery address",
              "value": "Fixture", "expectRole": "heading", "expectName": "Create order" },
            { "stepId": 3, "action": "click", "role": "button",
              "name": "Place order", "expectText": "Order created" }
          ],
          "assumptions": ["Confirm the example value is synthetic."]
        }
        """;

    [Fact]
    public void Valid_draft_preserves_ADO_step_order_and_requires_review()
    {
        var result = JourneyDraftCommand.ValidateDraft(JsonNode.Parse(Proposal)!.AsObject(), Case);
        Assert.Equal("testwizz.journey/v1", result.Schema);
        Assert.Equal([2, 3], result.Steps.Select(x => x.StepId));
    }

    [Fact]
    public void Rejects_missing_or_reordered_ADO_steps()
    {
        var changed = Proposal.Replace("\"stepId\": 3", "\"stepId\": 4", StringComparison.Ordinal);
        Assert.Equal("journey_draft.step_mismatch", Assert.Throws<CliException>(() =>
            JourneyDraftCommand.ValidateDraft(JsonNode.Parse(changed)!.AsObject(), Case)).Code);
    }

    [Fact]
    public void Rejects_malformed_reviewed_selectors()
    {
        var changed = Proposal.Replace("\"label\": \"Delivery address\",",
            "\"role\": \"textbox\", \"name\": \"Delivery address\", \"label\": \"Delivery address\",",
            StringComparison.Ordinal);
        Assert.Equal("journey_draft.invalid", Assert.Throws<CliException>(() =>
            JourneyDraftCommand.ValidateDraft(JsonNode.Parse(changed)!.AsObject(), Case)).Code);
    }

    [Fact]
    public void Rejects_missing_assumptions_array()
    {
        var changed = Proposal.Replace("\"assumptions\": [\"Confirm the example value is synthetic.\"]",
            "\"assumptions\": null", StringComparison.Ordinal);
        Assert.Equal("journey_draft.invalid", Assert.Throws<CliException>(() =>
            JourneyDraftCommand.ValidateDraft(JsonNode.Parse(changed)!.AsObject(), Case)).Code);
    }
}
