using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestWizz.Core.Generation;

namespace TestWizz.DotNet;

/// <param name="Code">The source to validate: unchanged, or decoded once when that provably repairs it.</param>
/// <param name="Repair">A traceable finding when the source was decoded; null when it was left as returned.</param>
public sealed record NormalizedSource(string Code, GenerationFinding? Repair);

/// <summary>
/// Handles C# that a model returned with one extra layer of string escaping inside the (already JSON-decoded)
/// <c>code</c> field, e.g. <c>using Xunit;\npublic class …</c> on one line. Legitimate escapes inside C# string and
/// character literals are never touched on their own: the whole document is decoded once, and the result is only
/// used when the original has stray backslashes outside literals (CS1056) or is a single escaped line, and the decoded
/// form has strictly fewer syntax errors and none of those stray backslashes. Decoding is tolerant: a backslash that is
/// not a JSON escape (for example a regex <c>\d</c> the model escaped only once) is kept as is instead of making the
/// whole decode fail and leaving every line escaped. Anything else is returned unchanged for validation to report.
/// </summary>
public static class ModelSource
{
    private static readonly CSharpParseOptions ParseOptions = new(LanguageVersion.Preview);

    public static NormalizedSource Normalize(string raw)
    {
        var code = raw.ReplaceLineEndings("\n").Trim();
        var original = Analyze(code);
        var singleEscapedLine = !code.Contains('\n') && code.Contains(@"\n", StringComparison.Ordinal);
        if (!singleEscapedLine && original.StrayBackslashLines.Count == 0)
        {
            return new(code + "\n", null);
        }

        // With real line breaks present, decoding could also change verbatim or raw strings that are valid as they are.
        if (!singleEscapedLine && original.HasBackslashInVerbatimOrRawString)
        {
            return new(code + "\n", null);
        }

        var decoded = DecodeOnce(code).ReplaceLineEndings("\n").Trim();
        if (decoded == code || !decoded.Contains('\n'))
        {
            return new(code + "\n", null);
        }

        var candidate = Analyze(decoded);
        if (candidate.StrayBackslashLines.Count > 0 || candidate.Errors >= original.Errors
            || (!singleEscapedLine && candidate.Errors > 0))
        {
            return new(code + "\n", null);
        }

        return new(decoded + "\n", GenerationFinding.Warning("escaped-source-repaired", string.Create(CultureInfo.InvariantCulture,
            $"The model returned C# with an extra layer of string escaping; it was decoded once ({original.Errors} syntax error(s) before, {candidate.Errors} after). Review string literals in the test file.")));
    }

    /// <summary>Lines with a backslash outside string, character and comment tokens, where C# never allows one.</summary>
    public static IReadOnlyList<int> StrayBackslashLines(string code) => Analyze(code).StrayBackslashLines;

    private sealed record Analysis(int Errors, IReadOnlyList<int> StrayBackslashLines, bool HasBackslashInVerbatimOrRawString);

    private static Analysis Analyze(string code)
    {
        var tree = CSharpSyntaxTree.ParseText(code, ParseOptions);
        var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var stray = errors
            // Roslyn reports CS1056 with an empty span that starts at the unexpected character.
            .Where(d => d.Id == "CS1056" && d.Location.SourceSpan.Start < code.Length && code[d.Location.SourceSpan.Start] == '\\')
            .Select(d => d.Location.GetLineSpan().StartLinePosition.Line + 1)
            .Distinct()
            .Order()
            .ToList();
        var verbatim = tree.GetRoot().DescendantNodes().Any(n => n switch
        {
            LiteralExpressionSyntax literal => literal.Token.Text.Contains('\\')
                && (literal.Token.Text.StartsWith('@')
                    || literal.Token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
                    || literal.Token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)),
            InterpolatedStringExpressionSyntax interpolated => interpolated.ToString().Contains('\\')
                && (interpolated.StringStartToken.Text.Contains('@')
                    || interpolated.StringStartToken.Text.Contains("\"\"\"", StringComparison.Ordinal)),
            _ => false,
        });
        return new(errors.Count, stray, verbatim);
    }

    /// <summary>One JSON-style unescape pass; unknown escapes and incomplete <c>\u</c> sequences are kept verbatim.</summary>
    internal static string DecodeOnce(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\' || i + 1 >= text.Length)
            {
                result.Append(text[i]);
                continue;
            }

            var next = text[i + 1];
            switch (next)
            {
                case 'n': result.Append('\n'); i++; break;
                case 'r': result.Append('\r'); i++; break;
                case 't': result.Append('\t'); i++; break;
                case '"': result.Append('"'); i++; break;
                case '\\': result.Append('\\'); i++; break;
                case '/': result.Append('/'); i++; break;
                case 'u' when i + 5 < text.Length
                    && int.TryParse(text.AsSpan(i + 2, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var unit):
                    result.Append((char)unit);
                    i += 5;
                    break;
                default:
                    result.Append('\\');
                    break;
            }
        }

        return result.ToString();
    }
}
