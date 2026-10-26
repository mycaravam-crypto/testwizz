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
        {
            var candidates = Directory.EnumerateFiles(root, "*.vue", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Where(path => !path.Split(Path.DirectorySeparatorChar)
                    .Any(segment => segment is "node_modules" or "dist" or "build" or ".git"))
                .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Take(20)
                .ToArray();
            throw new ContextException("target.not_found", $"Vue component not found: {relative}.", candidates);
        }
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
            foreach (string kind in new[] { "props", "emits", "slots" })
                foreach (JsonElement item in data.GetProperty(kind).EnumerateArray())
                {
                    string nameValue = item.GetString() ?? "";
                    if (nameValue.Length > 0)
                        members = [.. members, new MemberInfo(kind + ":" + nameValue, kind + " " + nameValue, relative, 1)];
                }
            if (data.TryGetProperty("propDetails", out JsonElement propDetails) && propDetails.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in propDetails.EnumerateArray())
                {
                    string propName = item.GetProperty("name").GetString() ?? "";
                    string type = item.GetProperty("type").GetString() ?? "unknown";
                    bool required = item.GetProperty("required").GetBoolean();
                    string defaultValue = item.GetProperty("defaultValue").ValueKind == JsonValueKind.Null ? "none" : item.GetProperty("defaultValue").GetString() ?? "unknown";
                    string validator = item.GetProperty("validator").ValueKind == JsonValueKind.Null ? "none" : item.GetProperty("validator").GetString() ?? "unknown";
                    members = [.. members, new MemberInfo("prop-detail:" + propName, $"prop {propName}: {type} (required: {required.ToString().ToLowerInvariant()}, default: {defaultValue}, validator: {validator})", relative, 1)];
                }
            if (data.TryGetProperty("emitDetails", out JsonElement emitDetails) && emitDetails.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in emitDetails.EnumerateArray())
                {
                    string emitName = item.GetProperty("name").GetString() ?? "";
                    string payload = item.GetProperty("payload").GetString() ?? "";
                    members = [.. members, new MemberInfo("emit-detail:" + emitName, $"emit {emitName}({payload})", relative, 1)];
                }
            if (data.TryGetProperty("emitCalls", out JsonElement emitCalls) && emitCalls.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in emitCalls.EnumerateArray())
                {
                    string emitName = item.GetProperty("name").GetString() ?? "";
                    int line = item.GetProperty("line").GetInt32();
                    int payloadCount = item.GetProperty("payloadCount").GetInt32();
                    members = [.. members, new MemberInfo($"emit-call:{emitName}:{line}", $"emit call {emitName} at line {line} (payload arguments: {payloadCount})", relative, line)];
                }
            foreach (string kind in new[] { "exposed", "computed", "watched", "imports", "mockCandidates" })
                if (data.TryGetProperty(kind, out JsonElement values) && values.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement item in values.EnumerateArray())
                    {
                        string value = item.GetString() ?? "";
                        if (value.Length > 0)
                            members = [.. members, new MemberInfo(kind + ":" + value, kind + " " + value, relative, 1)];
                    }

            if (data.TryGetProperty("resolvedImports", out JsonElement resolvedImports) && resolvedImports.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in resolvedImports.EnumerateArray())
                {
                    string specifier = item.GetProperty("specifier").GetString() ?? "";
                    string kind = item.GetProperty("kind").GetString() ?? "unresolved-or-external";
                    string resolvedPath = item.GetProperty("path").ValueKind == JsonValueKind.Null
                        ? "unresolved"
                        : item.GetProperty("path").GetString() ?? "unresolved";
                    members = [.. members, new MemberInfo("import-resolution:" + specifier, $"import {specifier} -> {resolvedPath} ({kind})", relative, 1)];
                }
            var relatedTests = new List<RelatedTestInfo>();
            if (data.TryGetProperty("relatedTests", out JsonElement testItems) && testItems.ValueKind == JsonValueKind.Array)
                foreach (JsonElement item in testItems.EnumerateArray())
                {
                    relatedTests.Add(new RelatedTestInfo(
                        item.GetProperty("name").GetString() ?? "",
                        item.GetProperty("file").GetString() ?? "",
                        item.GetProperty("line").GetInt32(),
                        item.GetProperty("matchedOn").GetString() ?? "import"));
                }
            string source = Encoding.UTF8.GetString(bytes);
            bool truncated = source.Length > maxChars;
            return new ContextDocument("vue", new Provenance("vue-compiler-sfc", "project", CliApp.Version),
                null, new TargetInfo("type", "vue:" + relative, name, null, null, name, "public", [], null, []),
                new SourceInfo(relative, 1, source.Split('\n').Length, Convert.ToHexStringLower(SHA256.HashData(bytes)),
                    truncated ? source[..maxChars] : source, truncated, null), null,
                members, [], [], [], relatedTests,
                [new("emitPaths", "Template event paths and static payload types are not fully mapped; script-level emit call sites include source lines and payload argument counts."),
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
