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
    private const string AllOption = "--all";
    private const string VerifiedOnlyOption = "--verified-only";

    public static CommandDefinition Definition { get; } = new(
        "apply",
        "Copy a proposal's test files into the repository after checking that its sources are unchanged, its last run passed and no file would be overwritten. Nothing is committed.",
        "testwizz apply [path] (--proposal <id|dir> | --all (--verified-only | --allow-unverified)) [--allow-unverified] [--out <dir>]",
        Options:
        [
            new(ProposalOption, "Proposal id (a folder in the output directory) or the bundle directory itself.", "id"),
            new(AllOption, "Process every proposal bundle in the configured output directory."),
            new(VerifiedOnlyOption, "In batch mode, apply only proposals whose latest run passed and whose generated files are unchanged."),
            new(AllowUnverifiedOption, "Apply even if the last run did not pass, no run was recorded, or files were edited after it."),
            new(OutOption, "Directory that holds proposal bundles (default: 'output' in testwizz.json).", "dir"),
        ],
        MaxArguments: 1,
        Handler: Run);

    private static int Run(CommandContext context)
    {
        var all = context.Has(AllOption);
        var verifiedOnly = context.Has(VerifiedOnlyOption);
        var allowUnverified = context.Has(AllowUnverifiedOption);
        var hasProposal = context.Options.TryGetValue(ProposalOption, out var reference) && !string.IsNullOrWhiteSpace(reference);
        if (all && (hasProposal || verifiedOnly == allowUnverified))
            throw new CliException("usage", "--all requires exactly one of --verified-only or --allow-unverified and cannot be combined with --proposal.");
        if (!all && (verifiedOnly || !hasProposal))
            throw new CliException("usage", $"Use {Definition.Usage}");

        var root = context.PathArgument();
        var config = ConfigCommand.Load(Path.Combine(root, TestWizzConfig.FileName));
        var output = context.Options.TryGetValue(OutOption, out var outText) && !string.IsNullOrWhiteSpace(outText)
            ? Path.GetFullPath(outText, context.WorkingDirectory)
            : Path.GetFullPath(config.Output, root);

        if (all)
            return ApplyAll(context, root, output, allowUnverified);

        IReadOnlyList<string> files;
        LoadedProposal proposal;
        try
        {
            proposal = ProposalReader.Load(ProposalReader.Resolve(reference!, output, context.WorkingDirectory));
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
    private static int ApplyAll(CommandContext context, string root, string output, bool allowUnverified)
    {
        if (!Directory.Exists(output))
            throw new CliException("apply.output_not_found", $"Proposal directory does not exist: {output}");

        var entries = new JsonArray();
        var applied = 0;
        var skipped = 0;
        var conflicted = 0;
        var filesApplied = 0;
        var text = new StringBuilder(allowUnverified ? "Batch apply (unverified proposals allowed):\n" : "Batch apply (verified proposals only):\n");

        // Stable order makes the first proposal win when two proposals target the same file.
        foreach (var directory in Directory.EnumerateDirectories(output).OrderBy(p => p, StringComparer.Ordinal))
        {
            if (!File.Exists(Path.Combine(directory, ProposalBundle.ManifestFile)))
                continue;
            var id = Path.GetFileName(directory);
            string status;
            string? reason = null;
            IReadOnlyList<string> files = [];
            try
            {
                var proposal = ProposalReader.Load(directory);
                id = proposal.Id;
                files = ProposalApplier.Apply(root, proposal, allowUnverified);
                status = "applied";
                applied++;
                filesApplied += files.Count;
            }
            catch (ProposalException ex)
            {
                reason = ex.Code + ": " + ex.Message;
                if (ex.Code is "apply.file_exists" or "apply.path_escape" or "apply.outside_test_project")
                {
                    status = "conflict";
                    conflicted++;
                }
                else
                {
                    status = "skipped";
                    skipped++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
            {
                status = "skipped";
                reason = "proposal.read_error: " + ex.Message;
                skipped++;
            }

            entries.Add(new JsonObject
            {
                ["proposalId"] = id,
                ["status"] = status,
                ["reason"] = reason,
                ["files"] = new JsonArray([.. files.Select(f => (JsonNode)f)]),
            });
            text.Append("  ").Append(status).Append(": ").Append(id);
            if (reason is not null) text.Append(" (").Append(reason).Append(')');
            text.Append('\n');
        }

        text.Append(System.Globalization.CultureInfo.InvariantCulture, $"Summary: {applied} applied, {skipped} skipped, {conflicted} conflicted; {filesApplied} files added.\n");
        text.Append("Nothing was committed. Review with 'git status' and 'git diff'.\n");
        context.Output.Result("apply", new JsonObject
        {
            ["schema"] = "testwizz.apply-batch/v1",
            ["applied"] = applied,
            ["skipped"] = skipped,
            ["conflicted"] = conflicted,
            ["filesApplied"] = filesApplied,
            ["items"] = entries,
        }, text.ToString(), conflicted == 0 ? "ok" : "failed");
        return conflicted == 0 ? ExitCodes.Success : ExitCodes.ValidationFailed;
    }

}
