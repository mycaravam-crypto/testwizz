using System.Text.Json.Nodes;
using TestWizz.Cli.Commands;

namespace TestWizz.UnitTests;

public sealed class BatchFailureTests
{
    [Theory]
    [InlineData("ai.timeout", "No response within 120 s.", true)]
    [InlineData("ai.connection_failed", "Could not connect.", true)]
    [InlineData("ai.empty_response", "Model returned no content.", true)]
    [InlineData("ai.invalid_response", "Model output is not valid JSON.", true)]
    [InlineData("ai.http_error", "Server returned HTTP 503. Run 'testwizz ai check' for details.", true)]
    [InlineData("ai.http_error", "Server returned HTTP 429.", true)]
    [InlineData("ai.http_error", "Server returned HTTP 400.", false)]
    [InlineData("ai.auth_failed", "Server rejected the credentials (HTTP 401).", false)]
    [InlineData("ai.endpoint_not_private", "Endpoint is public.", false)]
    [InlineData("ai.missing_api_key", "Variable not set.", false)]
    [InlineData("ai.cancelled", "Cancelled.", false)]
    [InlineData("validation.unknown-member", "No member.", false)]
    [InlineData("output.inside_repository", "Inside.", false)]
    public void OnlyTransientFailuresAreRecoverable(string code, string message, bool recoverable) =>
        Assert.Equal(recoverable, BatchFailure.IsRecoverable(code, message));

    [Fact]
    public void Messages_AreRedactedBoundedAndTerminalSafe()
    {
        var message = BatchFailure.SafeMessage(
            "Bearer abc.def-123 api_key=sk-secret token: t0k3n https://user:pw@host/x eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl \u001b[31mred" + new string('x', 1000));

        Assert.DoesNotContain("abc.def-123", message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret", message, StringComparison.Ordinal);
        Assert.DoesNotContain("t0k3n", message, StringComparison.Ordinal);
        Assert.DoesNotContain("user:pw", message, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJzdWIiOiIxIn0", message, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', message);
        Assert.True(message.Length <= 401, message.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl")]
    [InlineData("token expired: eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl.")]
    [InlineData("(eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2lnbmF0dXJl)")]
    public void Jwts_AreRedactedByBatchFailureItself(string message)
    {
        var safe = BatchFailure.SafeMessage(message);

        Assert.Contains("[JWT redacted]", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJzdWIiOiIxIn0", safe, StringComparison.Ordinal);
        Assert.DoesNotContain("c2lnbmF0dXJl", safe, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerationFailure_UsesFirstValidationErrorWithLineAndCount()
    {
        var manifest = JsonNode.Parse("""
            { "findings": [
              { "severity": "warning", "code": "scenario-not-covered", "message": "S2" },
              { "severity": "error", "code": "signature-mismatch", "message": "No overload for method 'GetName' takes 0 arguments", "line": 11 },
              { "severity": "error", "code": "unknown-member", "message": "Other" } ] }
            """);

        var failure = BatchFailure.FromGeneration(manifest);

        Assert.Equal("validation.signature-mismatch", failure.Code);
        Assert.Equal("No overload for method 'GetName' takes 0 arguments (line 11); 1 more error(s)", failure.Message);
        Assert.Equal("generate.failed", BatchFailure.FromGeneration(null).Code);
    }

    [Fact]
    public void RunFailure_SummarizesOutcome()
    {
        var failure = BatchFailure.FromRun(JsonNode.Parse("""
            { "status": "failed", "result": { "status": "failed", "summary": { "passed": 2, "failed": 1, "compileErrors": 0 } } }
            """));

        Assert.Equal("run.failed", failure.Code);
        Assert.Equal("2 passed, 1 failed, 0 compile error(s).", failure.Message);
        Assert.Equal("container.unavailable", BatchFailure.FromRun(JsonNode.Parse("""{ "status": "error", "error": { "code": "container.unavailable", "message": "Docker is not running." } }""")).Code);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "findings": "not-an-array" }""")]
    [InlineData("""{ "findings": [ 1, null, "x", { "severity": "warning", "code": "w" } ] }""")]
    public void GenerationFailure_ToleratesMalformedManifests(string json)
    {
        var failure = BatchFailure.FromGeneration(JsonNode.Parse(json));

        Assert.Equal("generate.failed", failure.Code);
    }

    [Fact]
    public void GenerationFailure_ToleratesMissingFieldsAndNonNumericLines()
    {
        var failure = BatchFailure.FromGeneration(JsonNode.Parse("""{ "findings": [ { "severity": "error", "line": "eleven" } ] }"""));

        Assert.Equal("validation.failed", failure.Code);
        Assert.Equal("Validation failed.", failure.Message);
    }

    [Theory]
    [InlineData("{}", "run.failed", "0 passed, 0 failed, 0 compile error(s).")]
    [InlineData("""{ "status": "error" }""", "run.error", "Run failed.")]
    [InlineData("""{ "status": "failed", "result": { "status": "compile-error", "summary": { "compileErrors": 3 } } }""", "run.compile-error", "0 passed, 0 failed, 3 compile error(s).")]
    public void RunFailure_ToleratesPartialResults(string json, string code, string message)
    {
        var failure = BatchFailure.FromRun(JsonNode.Parse(json));

        Assert.Equal(code, failure.Code);
        Assert.Equal(message, failure.Message);
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("42")]
    [InlineData("""{ "result": [ "x" ] }""")]
    [InlineData("""{ "result": { "summary": "n/a", "status": 7 } }""")]
    [InlineData("""{ "result": { "summary": { "passed": "two", "failed": 1.5 } } }""")]
    [InlineData("""{ "status": "error", "error": "boom" }""")]
    [InlineData("""{ "status": [ "error" ] }""")]
    public void RunFailure_NeverThrowsOnWrongShapes(string json)
    {
        var failure = BatchFailure.FromRun(JsonNode.Parse(json));

        Assert.StartsWith("run.", failure.Code, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("\"text\"")]
    [InlineData("""{ "findings": [ { "severity": "error", "code": 5, "message": [ "x" ], "line": 2.5 } ] }""")]
    [InlineData("""{ "findings": [ { "severity": [ "error" ] } ] }""")]
    public void GenerationFailure_NeverThrowsOnWrongShapes(string json)
    {
        var failure = BatchFailure.FromGeneration(JsonNode.Parse(json));

        Assert.False(string.IsNullOrEmpty(failure.Code));
    }

    [Theory]
    [InlineData("", "Unknown failure.")]
    [InlineData("   ", "Unknown failure.")]
    [InlineData("plain message", "plain message")]
    [InlineData("password=hunter2, next", "password=[redacted], next")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz", "Authorization: [redacted] [redacted]")]
    public void SafeMessage_HandlesEmptyPlainAndSecretInputs(string input, string expected) =>
        Assert.Equal(expected, BatchFailure.SafeMessage(input));

    [Theory]
    [InlineData("ai.timeout", "ai.timeout")]
    [InlineData("bad code\u001b[31m;rm -rf", "badcode31mrm-rf")]
    [InlineData("\u0001\u0002", "error")]
    public void Codes_AreRestrictedToSafeCharacters(string code, string expected) =>
        Assert.Equal(expected, BatchFailure.Create(code, "m").Code);

    [Fact]
    public void LegacyErrorString_IsCodeColonMessage() =>
        Assert.Equal("ai.timeout: No response.", BatchFailure.Create("ai.timeout", "No response.").ToString());
}
