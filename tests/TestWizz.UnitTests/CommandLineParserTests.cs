using TestWizz.Cli;

namespace TestWizz.UnitTests;

public class CommandLineParserTests
{
    private static ParsedCommandLine Parse(params string[] args) => CommandLineParser.Parse(args, CliApp.CommandsByName);

    [Fact]
    public void Empty_HasNoCommand()
    {
        var parsed = Parse();

        Assert.Null(parsed.CommandName);
        Assert.Equal(OutputFormat.Text, parsed.Format);
    }

    [Fact]
    public void CommandArgumentsAndOptions_AreParsed()
    {
        var parsed = Parse("init", "some/dir", "--force", "--format", "json");

        Assert.Equal("init", parsed.CommandName);
        Assert.Equal(["some/dir"], parsed.Arguments);
        Assert.True(parsed.Options.ContainsKey("--force"));
        Assert.Equal(OutputFormat.Json, parsed.Format);
    }

    [Theory]
    [InlineData("--format=json")]
    [InlineData("--format", "json")]
    public void Format_AcceptsBothSpellings(params string[] formatArgs)
    {
        Assert.Equal(OutputFormat.Json, Parse(["config", .. formatArgs]).Format);
    }

    [Fact]
    public void GenerateBatch_ParsesAdaptiveWorkersAndDebug()
    {
        var parsed = Parse("generate", ".", "--since", "main", "--all-changed", "--max-workers", "3", "--debug", "--format", "json");
        Assert.Equal("3", parsed.Options["--max-workers"]);
        Assert.True(parsed.Options.ContainsKey("--debug"));
        Assert.Equal(OutputFormat.Json, parsed.Format);
    }

    [Fact]
    public void GenerateBatch_ParsesContinueOnError()
    {
        var parsed = Parse("generate", ".", "--since", "main", "--all-changed", "--continue-on-error");
        Assert.True(parsed.Options.ContainsKey("--continue-on-error"));
    }

    [Fact]
    public void Bootstrap_ParsesDebug()
    {
        Assert.True(Parse("bootstrap", ".", "--all", "--debug").Options.ContainsKey("--debug"));
    }

    [Fact]
    public void Bootstrap_ParsesAdaptiveWorkers()
    {
        var parsed = Parse("bootstrap", ".", "--all", "--max-workers", "3");
        Assert.Equal("3", parsed.Options["--max-workers"]);
    }

    [Fact]
    public void Bootstrap_ParsesJsonlProgressWithoutChangingJsonOutput()
    {
        var parsed = Parse("bootstrap", ".", "--all", "--progress-jsonl", "--format", "json");
        Assert.True(parsed.Options.ContainsKey("--progress-jsonl"));
        Assert.Equal(OutputFormat.Json, parsed.Format);
    }

    [Fact]
    public void DoubleDash_TreatsRestAsArguments()
    {
        Assert.Equal(["--odd-dir"], Parse("init", "--", "--odd-dir").Arguments);
    }

    [Theory]
    [InlineData("frob")]
    [InlineData("init", "--frob")]
    [InlineData("--frob")]
    [InlineData("config", "--force")]
    [InlineData("init", "--force=yes")]
    [InlineData("init", "--force", "--force")]
    [InlineData("init", "a", "b")]
    [InlineData("--format", "xml")]
    [InlineData("--format")]
    [InlineData("config", "--format", "--help")]
    public void InvalidCommandLine_IsUsageError(params string[] args)
    {
        var ex = Assert.Throws<CliException>(() => Parse(args));

        Assert.Equal("usage", ex.Code);
        Assert.Equal(ExitCodes.Error, ex.ExitCode);
    }

    [Theory]
    [InlineData(OutputFormat.Json, "frob", "--format", "json")]
    [InlineData(OutputFormat.Json, "--format=json", "--frob")]
    [InlineData(OutputFormat.Text, "--format", "xml")]
    [InlineData(OutputFormat.Text)]
    public void DetectFormat_WorksOnInvalidCommandLines(OutputFormat expected, params string[] args)
    {
        Assert.Equal(expected, CommandLineParser.DetectFormat(args));
    }
}
