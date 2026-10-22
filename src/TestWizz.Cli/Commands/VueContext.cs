using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestWizz.Core.Context;

namespace TestWizz.Cli.Commands;

/// <summary>Read-only bridge to the project's own Vue SFC compiler.</summary>
public static class VueContext
{
    public static ContextDocument Load(string root, string target, int maxChars)
    {
        string relative = target.Replace('\\', '/');
        if (Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is "." or ".." or "") || !relative.EndsWith(".vue", StringComparison.OrdinalIgnoreCase))
            throw new ContextException("target.invalid", "Vue target must be a project-relative .vue path.");
        string full = Path.GetFullPath(relative, root);
        if (!File.Exists(full))
            throw new ContextException("target.not_found", $"Vue component not found: {relative}.");
        var bytes = File.ReadAllBytes(full);
        using Stream resource = typeof(VueContext).Assembly.GetManifestResourceStream("vue-context.mjs")
            ?? throw new ContextException("vue.helper_missing", "Bundled Vue helper not found.");
        string helper = Path.Combine(Path.GetTempPath(), "testwizz-vue-" + Guid.NewGuid().ToString("N") + ".mjs");
        try
        {
            using (var destination = File.Create(helper)) resource.CopyTo(destination);
            var start = new ProcessStartInfo("node")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string arg in new[] { helper, root, relative }) start.ArgumentList.Add(arg);
            using Process process = Process.Start(start)
                ?? throw new ContextException("vue.node_missing", "Node.js is required for Vue context extraction.");
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15_000))
            {
                process.Kill(entireProcessTree: true);
                throw new ContextException("vue.timeout", "Vue parser exceeded its 15-second limit.");
            }
            if (process.ExitCode != 0)
                throw new ContextException("vue.parser_failed", "Vue parser: " + stderr.GetAwaiter().GetResult().Trim());
            using JsonDocument parsed = JsonDocument.Parse(stdout.GetAwaiter().GetResult());
            JsonElement data = parsed.RootElement;
            string name = data.GetProperty("name").GetString() ?? Path.GetFileNameWithoutExtension(full);
            var members = data.GetProperty("bindings").EnumerateArray()
                .Select(x => new MemberInfo(x.GetProperty("name").GetString() ?? "",
                    x.GetProperty("kind").GetString() ?? "unknown", relative, 1)).ToArray();
            string source = Encoding.UTF8.GetString(bytes);
            bool truncated = source.Length > maxChars;
            return new ContextDocument("vue", new Provenance("vue-compiler-sfc", "project", CliApp.Version),
                null, new TargetInfo("type", "vue:" + relative, name, null, null, name, "public", [], null, []),
                new SourceInfo(relative, 1, source.Split('\n').Length, Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    truncated ? source[..maxChars] : source, truncated, null), null,
                members, [], [], [], [],
                [new("props", "Detailed props and validators not yet extracted."),
                 new("emits", "Emit paths not yet extracted."),
                 new("relatedTests", "Vue test relationship mapping not yet available."),
                 new("coverage", "No coverage measurement available.")], []);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ContextException("vue.node_missing", "Node.js is required. Install Node and run npm install in the target Vue project.");
        }
        catch (JsonException ex)
        {
            throw new ContextException("vue.invalid_output", "Vue helper returned invalid JSON: " + ex.Message);
        }
        finally
        {
            File.Delete(helper);
        }
    }
}
