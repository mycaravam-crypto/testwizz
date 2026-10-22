using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TestWizz.Cli;

public enum OutputFormat
{
    Text,
    Json,
}

public static class ExitCodes
{
    public const int Success = 0;
    public const int ValidationFailed = 1;
    public const int Error = 2;
}

/// <summary>A failure reported to the user. <see cref="Code"/> is a stable, machine-readable identifier.</summary>
public sealed class CliException(string code, string message, int exitCode = ExitCodes.Error, IReadOnlyList<(string Path, string Message)>? details = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public int ExitCode { get; } = exitCode;
    public IReadOnlyList<(string Path, string Message)> Details { get; } = details ?? [];
}

/// <summary>
/// Writes command results. In JSON mode stdout receives exactly one versioned JSON object; human-readable
/// diagnostics always go to stderr.
/// </summary>
public sealed class CliOutput(TextWriter stdout, TextWriter stderr, OutputFormat format)
{
    public const string Schema = "testwizz.output/v1";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public OutputFormat Format { get; } = format;

    public TextWriter Stdout { get; } = stdout;

    public TextWriter Stderr { get; } = stderr;

    public void Result(string command, JsonObject result, string text)
    {
        if (Format == OutputFormat.Json)
        {
            WriteJson(new JsonObject
            {
                ["schema"] = Schema,
                ["command"] = command,
                ["status"] = "ok",
                ["result"] = result,
            });
        }
        else
        {
            Stdout.Write(text);
        }
    }

    public void Error(string? command, CliException error)
    {
        Stderr.WriteLine($"error: {error.Message}");
        foreach (var (path, message) in error.Details)
        {
            Stderr.WriteLine($"  {path}: {message}");
        }

        if (Format == OutputFormat.Json)
        {
            var details = new JsonArray();
            foreach (var (path, message) in error.Details)
            {
                details.Add(new JsonObject { ["path"] = path, ["message"] = message });
            }

            WriteJson(new JsonObject
            {
                ["schema"] = Schema,
                ["command"] = command,
                ["status"] = "error",
                ["error"] = new JsonObject
                {
                    ["code"] = error.Code,
                    ["message"] = error.Message,
                    ["details"] = details,
                },
            });
        }
    }

    private void WriteJson(JsonObject value)
    {
        Stdout.Write(value.ToJsonString(JsonOptions));
        Stdout.Write('\n');
    }
}
