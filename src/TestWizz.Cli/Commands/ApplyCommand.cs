using System.Text;
using System.Text.Json.Nodes;
using TestWizz.Core.Configuration;
using TestWizz.Core.Proposals;

namespace TestWizz.Cli.Commands;

public static class ApplyCommand
{
    private const string ProposalOption = "--proposal";
    private const string OutOption = "--out";
    private const string AllowUnverifiedOption = "--allow-unverified";

    public static CommandDefinition Definition { get; } = new(
        "apply",
        "Copy a proposal's test files into the repository after checking that its sources are unchanged, its last run passed and no file would be overwritten. Nothing is committed.",
        "testwizz apply [path] --proposal <id|dir> [--allow-unverified] [--out <dir>]",
        Options:
        [
            new(ProposalOption, "Proposal id (a folder in the output directory) or the bundle directory itself.", "id"),
            new(AllowUnverifiedOption, "Apply even if the last run did not pass, no run was recorded, or files were edited after it."),
            new(OutOption, "Directory that holds proposal bundles (default: 'output' in testwizz.json).", "dir"),
        ],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        if (!context.Options.TryGetValue(ProposalOption, out var reference) || string.IsNullOrWhiteSpace(reference))
        {
            throw new CliException("usage", $"Missing {ProposalOption} <id>. Usage: {Definition.Usage}");
        }

        var root = context.PathArgument();
        var config = ConfigCommand.Load(Path.Combine(root, TestWizzConfig.FileName));
        var output = context.Options.TryGetValue(OutOption, out var outText) && !string.IsNullOrWhiteSpace(outText)
            ? Path.GetFullPath(outText, context.WorkingDirectory)
            : Path.GetFullPath(config.Output, root);

        IReadOnlyList<string> files;
        LoadedProposal proposal;
        try
        {
            proposal = ProposalReader.Load(ProposalReader.Resolve(reference, output, context.WorkingDirectory));
            files = ProposalApplier.Apply(root, proposal, context.Has(AllowUnverifiedOption));
        }
        catch (ProposalException ex)
        {
            throw new CliException(ex.Code, ex.Message);
        }

        var text = new StringBuilder();
        text.Append("Applied proposal ").Append(proposal.Id).Append(":\n");
        foreach (var file in files)
        {
            text.Append("  + ").Append(file).Append('\n');
        }

        text.Append("Nothing was committed. Review with 'git status' and 'git diff'.\n");
        context.Output.Result(
            "apply",
            new JsonObject
            {
                ["proposalId"] = proposal.Id,
                ["files"] = new JsonArray([.. files.Select(f => (JsonNode)f)]),
            },
            text.ToString());
        return ExitCodes.Success;
    }
}
