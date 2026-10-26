using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TestWizz.Core.Context;

namespace TestWizz.Cli.Commands;

/// <summary>Read-only static extractor for Vue composables and Pinia stores.</summary>
public static class VueTypeScriptContext
{
    public static ContextDocument Load(string root, string target, int maxChars)
    {
        string relative = target.Replace('\\\\', '/');
        if (Path.IsPathRooted(relative) || relative.Split('/').Any(part => part is "." or ".." or "")
            || !relative.EndsWith(".ts", StringComparison.OrdinalIgnoreCase)
            || !relative.Split('/').Any(part => part is "composables" or "stores"))
            throw new ContextException("target.invalid", "Vue composable/store target must be a project-relative .ts file under a composables or stores directory.");

        string full = Path.GetFullPath(relative, root);
        if (!File.Exists(full) || !Path.GetFullPath(full).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            var candidates = Directory.EnumerateFiles(root, "*.ts", new EnumerationOptions
                { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(path => path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "composables" or "stores")
                    && !path.Split(Path.DirectorySeparatorChar).Any(segment => segment is "node_modules" or "dist" or "build" or ".git"))
                .Select(path => Path.GetRelativePath(root, path).Replace('\\\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal).Take(20).ToArray();
            throw new ContextException("target.not_found", $"Vue composable/store not found: {relative}.", candidates);
        }

        var bytes = File.ReadAllBytes(full);
        using Stream resource = typeof(VueTypeScriptContext).Assembly.GetManifestResourceStream("vue-ts-context.mjs")
            ?? throw new ContextException("vue.helper_missing", "Bundled Vue TypeScript helper not found.");
        string helper = Path.Combine(Path.GetTempPath(), "testwizz-vue-ts-" + Guid.NewGuid().ToString("N") + ".mjs");
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
                throw new ContextException("vue.timeout", "Vue TypeScript parser exceeded its 15-second limit.");
            }
            if (process.ExitCode != 0)
                throw new ContextException("vue.parser_failed", "Vue TypeScript parser: " + stderr.GetAwaiter().GetResult().Trim());

            using JsonDocument parsed = JsonDocument.Parse(stdout.GetAwaiter().GetResult());
            JsonElement data = parsed.RootElement;
            string name = data.GetProperty("name").GetString() ?? Path.GetFileNameWithoutExtension(full);
            string kind = data.GetProperty("kind").GetString() ?? "composable";
            var members = new List<MemberInfo>();
            if (data.TryGetProperty("members", out JsonElement extracted) && extracted.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in extracted.EnumerateArray())
                    members.Add(new MemberInfo(
                        item.GetProperty("id").GetString() ?? "",
                        item.GetProperty("signature").GetString() ?? "",
                        relative,
                        item.GetProperty("line").GetInt32()));
            if (data.TryGetProperty("imports", out JsonElement imports) && imports.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in imports.EnumerateArray())
                {
                    string value = item.GetString() ?? "";
                    members.Add(new MemberInfo("imports:" + value, "imports " + value, relative, 1));
                }
            if (data.TryGetProperty("dependencies", out JsonElement dependencies) && dependencies.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in dependencies.EnumerateArray())
                {
                    string value = item.GetString() ?? "";
                    members.Add(new MemberInfo("dependency:" + value, "dependency " + value, relative, 1));
                }
            var relatedTests = new List<RelatedTestInfo>();
            if (data.TryGetProperty("relatedTests", out JsonElement tests) && tests.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in tests.EnumerateArray())
                    relatedTests.Add(new RelatedTestInfo(
                        item.GetProperty("name").GetString() ?? "",
                        item.GetProperty("file").GetString() ?? "",
                        item.GetProperty("line").GetInt32(),
                        item.GetProperty("matchedOn").GetString() ?? "import"));

            string source = Encoding.UTF8.GetString(bytes);
            bool truncated = source.Length > maxChars;
            return new ContextDocument("vue", new Provenance("typescript-ast", "project", CliApp.Version),
                null, new TargetInfo("type", "vue:" + relative, name, null, null,
                    kind == "pinia-store" ? "Pinia store " + name : "Vue composable " + name,
                    "public", [], null, []),
                new SourceInfo(relative, 1, source.Split('\\n').Length,
                    Convert.ToHexStringLower(SHA256.HashData(bytes)), truncated ? source[..maxChars] : source, truncated, null),
                null, members.OrderBy(m => m.Id, StringComparer.Ordinal).ToArray(), [], [], [], relatedTests,
                [new("coverage", "No coverage measurement available."),
                 new("runtimeBehavior", "Composable and store behavior is statically inspected; code is not executed.")], []);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new ContextException("vue.node_missing", "Node.js is required. Install Node and run npm install in the target Vue project.");
        }
        catch (JsonException ex)
        {
            throw new ContextException("vue.invalid_output", "Vue TypeScript helper returned invalid JSON: " + ex.Message);
        }
        finally
        {
            File.Delete(helper);
        }
    }
}
