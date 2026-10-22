using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TestWizz.DotNet;

/// <summary>Finds C# methods intersecting added or modified new-side Git diff lines.</summary>
public static class ChangedCode
{
    private static readonly Regex Hunk = new(@"^@@ -\d+(?:,\d+)? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled);

    public sealed record RankedMethod(string Target, int Risk, IReadOnlyList<string> Reasons);

    public static IReadOnlyList<string> Methods(string root, string since) => Ranked(root, since).Select(x => x.Target).ToList();

    public static IReadOnlyList<RankedMethod> Ranked(string root, string since)
    {
        if (string.IsNullOrWhiteSpace(since) || since.StartsWith('-'))
        {
            throw new ArgumentException("A valid Git base revision is required.", nameof(since));
        }

        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in new[] { "diff", "--no-ext-diff", "--no-textconv", "--no-color", "--unified=0", $"{since}...HEAD", "--", "*.cs" })
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to launch git.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("Git diff timed out.");
        }

        var diff = outputTask.GetAwaiter().GetResult();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException("Git diff failed: " + errorTask.GetAwaiter().GetResult().Trim());
        }

        var changed = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        string? current = null;
        var lineNumber = 0;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                current = null;
            }
            else if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            {
                var path = line[6..];
                current = path.EndsWith(".cs", StringComparison.Ordinal) ? path : null;
            }
            else if (current is not null && Hunk.Match(line) is { Success: true } match)
            {
                lineNumber = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (current is not null && line.StartsWith('+') && !line.StartsWith("+++", StringComparison.Ordinal))
            {
                if (!changed.TryGetValue(current, out var lines))
                {
                    lines = [];
                    changed[current] = lines;
                }
                lines.Add(lineNumber++);
            }
            else if (current is not null && line.StartsWith(' '))
            {
                lineNumber++;
            }
        }

        var targets = new List<RankedMethod>();
        foreach (var (path, lines) in changed)
        {
            var full = Path.GetFullPath(path, root);
            if (!File.Exists(full) || !Path.GetFullPath(full).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                continue;
            }
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(full));
            foreach (var method in tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var span = tree.GetLineSpan(method.Span);
                if (!lines.Any(n => n >= span.StartLinePosition.Line + 1 && n <= span.EndLinePosition.Line + 1))
                {
                    continue;
                }
                var type = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
                if (type is not null)
                {
                    var branches = method.DescendantNodes().Count(node => node is IfStatementSyntax or SwitchStatementSyntax
                        or ConditionalExpressionSyntax or ForStatementSyntax or ForEachStatementSyntax
                        or WhileStatementSyntax or DoStatementSyntax or CatchClauseSyntax);
                    var guards = method.DescendantNodes().Count(node => node is ThrowStatementSyntax or ThrowExpressionSyntax);
                    var calls = method.DescendantNodes().OfType<InvocationExpressionSyntax>().Count();
                    var awaits = method.DescendantNodes().OfType<AwaitExpressionSyntax>().Count();
                    var changedLines = lines.Count(n => n >= span.StartLinePosition.Line + 1 && n <= span.EndLinePosition.Line + 1);
                    var score = Math.Min(100, 10 + Math.Min(35, branches * 9)
                        + Math.Min(20, guards * 7) + Math.Min(15, awaits * 8)
                        + Math.Min(10, calls * 2) + Math.Min(10, changedLines * 2));
                    var reasons = new List<string>();
                    if (branches > 0) reasons.Add($"branches:{branches}");
                    if (guards > 0) reasons.Add($"guards:{guards}");
                    if (awaits > 0) reasons.Add($"awaits:{awaits}");
                    if (calls > 0) reasons.Add($"calls:{calls}");
                    reasons.Add($"changed-lines:{changedLines}");
                    targets.Add(new RankedMethod($"{type.Identifier.ValueText}.{method.Identifier.ValueText}", score, reasons));
                }
            }
        }
        return [.. targets.GroupBy(x => x.Target, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(x => x.Risk).First())
            .OrderByDescending(x => x.Risk).ThenBy(x => x.Target, StringComparer.Ordinal)];
    }
}
