using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TestWizz.Core.Proposals;
using TestWizz.Core.Configuration;

namespace TestWizz.Cli.Commands;

/// <summary>
/// Shared atomic, fingerprinted progress for independent, review-first generators.
/// A checkpoint is a cache, never authority to approve or apply a proposal.
/// </summary>
internal static class BatchResume
{
    public static string Fingerprint(IEnumerable<string> values)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in values)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            sha.AppendData(BitConverter.GetBytes(bytes.Length));
            sha.AppendData(bytes);
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>
    /// Fingerprints only allowlisted generation settings, never credential values, URI user-info,
    /// query strings or environment-variable contents. The key environment variable name
    /// controls transport authorization, not the semantics of generated tests.
    /// </summary>
    public static string PublicConfigFingerprint(TestWizzConfig config) => Fingerprint(new[]
    {
        config.Ai.Provider.ToString(),
        config.Ai.Endpoint.Scheme,
        config.Ai.Endpoint.Host,
        config.Ai.Endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        config.Ai.Endpoint.AbsolutePath,
        config.Ai.Model,
        config.Ai.TimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        config.Ai.MaxContextTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
        config.Run.Runtime.ToString(),
        config.Run.MaxRepairAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
        config.Output,
    });

    /// <summary>ADO trace exports carry a new generatedAt value on every read.</summary>
    public static string StableAdoJson(string json)
    {
        try
        {
            var data = JsonNode.Parse(json)?.AsObject()
                ?? throw new CliException("resume.input_invalid", "The ADO snapshot is not a JSON object.");
            data.Remove("generatedAt");
            return data.ToJsonString();
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new CliException("resume.input_invalid", "The ADO snapshot is not a valid JSON object.");
        }
    }

    public static string? ShaFile(string file)
    {
        try
        {
            if (!File.Exists(file) || new FileInfo(file).Length is < 1 or > 10485760)
                return null;
            return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static bool Intact(string root, string output, string? file, string? digest)
    {
        if (string.IsNullOrWhiteSpace(file) || string.IsNullOrWhiteSpace(digest) || digest.Length != 64)
            return false;
        try
        {
            var full = Path.GetFullPath(file);
            return PathGuard.IsWithin(output, full) && !PathGuard.IsWithin(root, full) &&
                string.Equals(ShaFile(full), digest, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>A manifest digest alone is insufficient: proposed test files might have been edited.</summary>
    public static bool IntactProposal(string root, string output, string? proposal, string? digest)
    {
        if (string.IsNullOrWhiteSpace(proposal) ||
            !Intact(root, output, Path.Combine(proposal, ProposalBundle.ManifestFile), digest))
            return false;
        try
        {
            var loaded = ProposalReader.Load(proposal);
            return loaded.Edited.Count == 0 && ProposalReader.StaleSources(root, loaded).Count == 0;
        }
        catch (Exception ex) when (ex is ProposalException or IOException or UnauthorizedAccessException or
            System.Text.Json.JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    public static JsonArray Read(string path, string schema, string fingerprint)
    {
        if (!File.Exists(path))
            throw new CliException("resume.no_checkpoint",
                "No checkpoint found in the output directory. Start without --resume.");
        try
        {
            var state = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            if ((string?)state?["schema"] != schema ||
                state?["items"] is not JsonArray items)
                throw new CliException("resume.checkpoint_invalid", "Invalid batch checkpoint format.");
            // Protect every batch implementation against duplicate, missing or malformed
            // entries before any caller converts them to a dictionary.
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in items)
            {
                if (entry is not JsonObject row)
                    throw new CliException("resume.checkpoint_invalid", "Invalid checkpoint entry.");
                var identity = (string?)row["target"] ?? (string?)row["key"];
                if (string.IsNullOrWhiteSpace(identity) || !keys.Add(identity))
                    throw new CliException("resume.checkpoint_invalid", "Missing or duplicate checkpoint target.");
            }
            if ((string?)state["fingerprint"] != fingerprint)
                throw new CliException("resume.changed",
                    "Inputs, source revisions, model settings, route or scope changed. " +
                    "Use a fresh output directory and start without --resume.");
            return items;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException
            or ArgumentException or IOException or UnauthorizedAccessException)
        {
            throw new CliException("resume.checkpoint_invalid", "Invalid or inaccessible batch checkpoint JSON.");
        }
    }

    public static void Save(string path, string schema, string fingerprint, JsonArray items)
    {
        var state = new JsonObject
        {
            ["schema"] = schema,
            ["fingerprint"] = fingerprint,
            ["items"] = items.DeepClone(),
        };
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, state.ToJsonString() + "\n");
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
