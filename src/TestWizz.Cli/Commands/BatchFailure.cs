using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace TestWizz.Cli.Commands;

/// <summary>A machine-readable reason why one batch target failed, safe to print and to persist in a checkpoint.</summary>
internal sealed partial record BatchFailure(string Code, string Message)
{
    private const int MaxMessageChars = 400;

    /// <summary><c>code: message</c>, the format of the checkpoint's legacy <c>error</c> field.</summary>
    public override string ToString() => Code + ": " + Message;

    public JsonObject ToJson() => new() { ["code"] = Code, ["message"] = Message };

    public static BatchFailure Create(string code, string message) => new(SafeCode(code), SafeMessage(message));

    public static BatchFailure From(CliException ex) => Create(ex.Code, ex.Message);

    /// <summary>
    /// The first validation error of a generated proposal (<c>generate</c>'s JSON result is its manifest), e.g.
    /// <c>validation.signature-mismatch: No overload ... (line 11); 2 more error(s)</c>.
    /// </summary>
    public static BatchFailure FromGeneration(JsonNode? result)
    {
        var errors = (Member(result, "findings") as JsonArray)?.OfType<JsonObject>()
            .Where(f => Text(f, "severity") == "error").ToList() ?? [];
        if (errors.Count == 0)
            return Create("generate.failed", "Generation did not produce a valid proposal.");
        var first = errors[0];
        var message = new StringBuilder(Text(first, "message") ?? "Validation failed.");
        if (Number(first, "line") is { } number)
            message.Append(CultureInfo.InvariantCulture, $" (line {number})");
        if (errors.Count > 1)
            message.Append(CultureInfo.InvariantCulture, $"; {errors.Count - 1} more error(s)");
        return Create("validation." + (Text(first, "code") ?? "failed"), message.ToString());
    }

    /// <summary>Why <c>run</c> did not pass, from its JSON result.</summary>
    public static BatchFailure FromRun(JsonNode? response)
    {
        if (Text(response, "status") == "error")
        {
            var error = Member(response, "error");
            return Create(Text(error, "code") ?? "run.error", Text(error, "message") ?? "Run failed.");
        }

        var result = Member(response, "result");
        var summary = Member(result, "summary");
        var status = Text(result, "status") ?? "failed";
        return Create("run." + status, string.Create(CultureInfo.InvariantCulture,
            $"{Number(summary, "passed") ?? 0} passed, {Number(summary, "failed") ?? 0} failed, {Number(summary, "compileErrors") ?? 0} compile error(s)."));
    }

    // External JSON can have any shape: indexing a non-object or casting a value of another kind would throw.
    private static JsonNode? Member(JsonNode? node, string name) => node is JsonObject obj ? obj[name] : null;

    private static string? Text(JsonNode? node, string name) =>
        Member(node, name) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Number(JsonNode? node, string name) =>
        Member(node, name) is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    /// <summary>
    /// Transient model or transport problems that another attempt may fix. Configuration, authorization, policy,
    /// validation and cancellation failures are never retried.
    /// </summary>
    public static bool IsRecoverable(string code, string message) => code switch
    {
        "ai.timeout" or "ai.connection_failed" or "ai.dns_failed" or "ai.empty_response" or "ai.invalid_response" => true,
        "ai.http_error" => HttpStatus().Match(message) is { Success: true } match
            && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) is 408 or 425 or 429 or >= 500,
        _ => false,
    };

    /// <summary>Terminal-safe, bounded text without credentials (tokens, keys, passwords, JWTs).</summary>
    public static string SafeMessage(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "Unknown failure.";
        var text = Jwt().Replace(message, "[JWT redacted]");
        text = Bearer().Replace(text, "$1[redacted]");
        text = Secret().Replace(text, "$1$2[redacted]");
        text = UserInfo().Replace(text, "$1[redacted]@");
        var safe = new StringBuilder(Math.Min(text.Length, MaxMessageChars + 1));
        foreach (var c in text)
        {
            if (safe.Length >= MaxMessageChars)
            {
                safe.Append('…');
                break;
            }

            safe.Append(char.IsControl(c) ? ' ' : c);
        }

        return safe.ToString().Trim();
    }

    private static string SafeCode(string code)
    {
        var safe = new string([.. code.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').Take(80)]);
        return safe.Length == 0 ? "error" : safe;
    }

    // Self-contained on purpose: the redaction guarantee is owned and tested here, not by another feature's helper.
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex Jwt();

    [GeneratedRegex(@"HTTP (\d{3})", RegexOptions.CultureInvariant)]
    private static partial Regex HttpStatus();

    [GeneratedRegex(@"(?i)\b((?:bearer|basic|digest|token)\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant)]
    private static partial Regex Bearer();

    [GeneratedRegex(@"(?i)\b(api[_-]?key|access[_-]?token|token|password|passwd|secret|authorization)(\s*[:=]\s*)[^\s,;]+", RegexOptions.CultureInvariant)]
    private static partial Regex Secret();

    [GeneratedRegex(@"(?i)(\b[a-z][a-z0-9+.-]*://)[^/\s@]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UserInfo();
}
