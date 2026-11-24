using System.Text.RegularExpressions;
using TestWizz.Cli;

namespace TestWizz.UnitTests;

public sealed class BatchProgressTests
{
    [Fact]
    public void RedirectedOutput_UsesReadableMilestonesWithoutEscapeSequences()
    {
        using var output = new StringWriter();
        using (var progress = new BatchProgress(output, "generate", interactive: false))
        {
            progress.Stage("generate", 20, detail: "20 methods");
            progress.Update(1, "Method.A");
            progress.Update(2, "Method.B");
            progress.Update(3, "Method.C");
            progress.Update(20, "Method.Z", failed: 1);
            progress.Finish(20, failed: 1, detail: "19 successful");
        }
        var text = output.ToString();
        Assert.Contains("testwizz generate: generate 0/20 (0%)", text, StringComparison.Ordinal);
        Assert.Contains("20/20 (100%)", text, StringComparison.Ordinal);
        Assert.Contains("1 failed", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
        Assert.DoesNotContain('\u001b', text);
        Assert.True(text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length < 10);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(60)]
    [InlineData(80)]
    [InlineData(100)]
    public void InteractiveBar_FitsTerminalWidthAndClearsPreviousLine(int width)
    {
        using var output = new StringWriter();
        using (var progress = new BatchProgress(output, "journey-draft", interactive: true,
                   width: width, color: true))
        {
            progress.Stage("draft", 37, detail: "WI 4711 / case 512");
            progress.Update(11, "Case 512, a very long untrusted title that must be clipped", active: 2);
            progress.Update(37, "Case 513", failed: 1, skipped: 2);
            progress.Finish(37, failed: 1, skipped: 2, detail: "Review required");
        }
        var result = output.ToString();
        Assert.Contains("\r\u001b[2K", result, StringComparison.Ordinal);
        Assert.Contains("\u001b[", result, StringComparison.Ordinal);
        Assert.EndsWith("\n", result, StringComparison.Ordinal);
        var ansi = new Regex(@"\x1b\[[0-9;]*[A-Za-z]", RegexOptions.CultureInvariant);
        foreach (var redraw in result.Split("\r\u001b[2K", StringSplitOptions.RemoveEmptyEntries))
        {
            var visible = ansi.Replace(redraw.TrimEnd('\r', '\n'), "");
            Assert.True(visible.Length <= width - 1, $"Terminal {width}: {visible.Length} visible chars: {visible}");
        }
    }

    [Fact]
    public void NoColor_TerminalStillRedrawsWithoutColorStyling()
    {
        using var output = new StringWriter();
        using (var progress = new BatchProgress(output, "bootstrap", interactive: true,
                   width: 95, color: false))
        {
            progress.Stage("generate", 10);
            progress.Update(5, "Half done");
            progress.Finish(10);
        }
        var rendered = output.ToString();
        Assert.Contains("\r\u001b[2K", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[36m", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[32m", rendered, StringComparison.Ordinal);
        Assert.Contains("━", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void UntrustedTargetCannotInjectTerminalControls()
    {
        using var output = new StringWriter();
        using (var progress = new BatchProgress(output, "generate", interactive: false))
        {
            progress.Stage("generate", 1, detail: "start");
            progress.Update(1, "Suspicious\u001b[31m\n\rmessage");
            progress.Finish(1);
        }
        Assert.DoesNotContain('\u001b', output.ToString());
        Assert.DoesNotContain('\r', output.ToString());
        Assert.Contains("Suspicious [31m", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticsPreserveMultilineDebugAndSanitizeControlCharacters()
    {
        using var output = new StringWriter();
        const string debug = "BEGIN generated test:\n    public void Test() {}\nEND generated test";
        using (var progress = new BatchProgress(output, "bootstrap", interactive: true,
                   width: 85, color: false))
        {
            progress.Stage("generate", 1);
            progress.Diagnostics(debug + "\u001b[31m");
            progress.Finish(1);
        }
        var result = output.ToString();
        Assert.Contains(debug, result, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[31m", result, StringComparison.Ordinal);
        Assert.Contains("\r\u001b[2K", result, StringComparison.Ordinal);
    }

    [Fact]
    public void IndeterminateAndQuietModesDoNotFakeProgress()
    {
        using var output = new StringWriter();
        using (var progress = new BatchProgress(output, "bootstrap", interactive: false))
        {
            progress.Stage("scan", 0, detail: "Loading sources");
            progress.Finish(0);
        }
        Assert.Contains("0/?", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("(100%)", output.ToString(), StringComparison.Ordinal);

        using var silent = new StringWriter();
        using (var progress = new BatchProgress(silent, "bootstrap", interactive: true, quiet: true))
        {
            progress.Stage("scan", 10);
            progress.Update(9);
            progress.Finish(10);
        }
        Assert.Equal("", silent.ToString());
    }

    [Fact]
    public void IncompleteInteractiveProgressEndsWithCleanDiagnostic()
    {
        using var output = new StringWriter();
        using (var progress = new BatchProgress(output, "apply", interactive: true, width: 90))
        {
            progress.Stage("apply", 5);
            progress.Update(2, "Proposal B");
        }
        Assert.Contains("interrupted at 2/5", output.ToString(), StringComparison.Ordinal);
    }
}
