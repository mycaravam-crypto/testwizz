using System.Text.Json.Nodes;
using TestWizz.Cli;
using TestWizz.Cli.Commands;

namespace TestWizz.UnitTests;

public sealed class BatchResumeTests
{
    [Fact]
    public void Checkpoint_IsAtomicAndRestoresCompletedRows()
    {
        var root = Path.Combine(Path.GetTempPath(), "testwizz-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "checkpoint.json");
            var digest = BatchResume.Fingerprint(["same inputs", "same settings"]);
            var original = new JsonArray(new JsonObject { ["target"] = "A", ["status"] = "ok" });
            BatchResume.Save(path, "testwizz.batch/v1", digest, original);
            original[0]!["status"] = "failed";
            var loaded = BatchResume.Read(path, "testwizz.batch/v1", digest);
            Assert.Equal("ok", (string?)loaded[0]?["status"]);

            BatchResume.Save(path, "testwizz.batch/v1", digest,
                new JsonArray(new JsonObject { ["target"] = "B", ["status"] = "ok" }));
            var second = BatchResume.Read(path, "testwizz.batch/v1", digest);
            Assert.Single(second);
            Assert.Equal("B", (string?)second[0]?["target"]);
            Assert.Single(Directory.GetFiles(root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Resume_RejectsDifferentInputsOrMissingCheckpoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "testwizz-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "checkpoint.json");
            Assert.Equal("resume.no_checkpoint",
                Assert.Throws<CliException>(() => BatchResume.Read(path, "x/v1", "abc")).Code);
            BatchResume.Save(path, "x/v1", "old-inputs", new JsonArray());
            Assert.Equal("resume.changed",
                Assert.Throws<CliException>(() => BatchResume.Read(path, "x/v1", "new-inputs")).Code);
            Assert.Equal("resume.checkpoint_invalid",
                Assert.Throws<CliException>(() => BatchResume.Read(path, "different-schema", "old-inputs")).Code);
            File.WriteAllText(path, "{invalid json");
            Assert.Equal("resume.checkpoint_invalid",
                Assert.Throws<CliException>(() => BatchResume.Read(path, "x/v1", "old-inputs")).Code);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void RejectsDuplicateAndMalformedCheckpointEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "testwizz-resume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "checkpoint.json");
            BatchResume.Save(file, "x/v1", "fingerprint", new JsonArray(
                new JsonObject { ["target"] = "same" },
                new JsonObject { ["target"] = "same" }));
            Assert.Equal("resume.checkpoint_invalid",
                Assert.Throws<CliException>(() => BatchResume.Read(file, "x/v1", "fingerprint")).Code);
            BatchResume.Save(file, "x/v1", "fingerprint", new JsonArray(new JsonObject
            {
                ["status"] = "drafted",
            }));
            Assert.Equal("resume.checkpoint_invalid",
                Assert.Throws<CliException>(() => BatchResume.Read(file, "x/v1", "fingerprint")).Code);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("{invalid")]
    [InlineData("[]")]
    [InlineData("null")]
    public void AdoJson_InvalidDataFailsWithDomainError(string raw)
    {
        Assert.Equal("resume.input_invalid",
            Assert.Throws<CliException>(() => BatchResume.StableAdoJson(raw)).Code);
    }

    [Fact]
    public void Fingerprint_DoesNotConfuseConcatenatedParts()
    {
        Assert.NotEqual(BatchResume.Fingerprint(["ab", "c"]), BatchResume.Fingerprint(["a", "bc"]));
        Assert.Equal(BatchResume.Fingerprint(["a", "b"]), BatchResume.Fingerprint(["a", "b"]));
        Assert.NotEqual(BatchResume.Fingerprint(["a", "b"]), BatchResume.Fingerprint(["b", "a"]));
    }

    [Fact]
    public void AdoFingerprint_IgnoresOnlyVolatileGeneratedAt()
    {
        const string first = """
            {"format":"ado.test-traceability/v1","planId":12,"generatedAt":"2026-01-01T10:00:00Z","requirements":[{"id":4711,"testCaseIds":[512]}]}
            """;
        const string newer = """
            {"format":"ado.test-traceability/v1","planId":12,"generatedAt":"2026-01-02T10:00:00Z","requirements":[{"id":4711,"testCaseIds":[512]}]}
            """;
        const string changed = """
            {"format":"ado.test-traceability/v1","planId":12,"generatedAt":"2026-01-02T10:00:00Z","requirements":[{"id":4711,"testCaseIds":[513]}]}
            """;
        Assert.Equal(BatchResume.StableAdoJson(first), BatchResume.StableAdoJson(newer));
        Assert.NotEqual(BatchResume.StableAdoJson(first), BatchResume.StableAdoJson(changed));
    }

    [Fact]
    public void ArtifactReuse_RejectsEditedDeletedOrEscapedFiles()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "testwizz-resume-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(baseDir, "repo");
        var outDir = Path.Combine(baseDir, "out");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outDir);
        try
        {
            var artifact = Path.Combine(outDir, "manifest.json");
            File.WriteAllText(artifact, """{"schema":"testwizz.proposal/v1"}""");
            var digest = BatchResume.ShaFile(artifact);
            Assert.NotNull(digest);
            Assert.True(BatchResume.Intact(root, outDir, artifact, digest));
            File.AppendAllText(artifact, "\n");
            Assert.False(BatchResume.Intact(root, outDir, artifact, digest));
            Assert.False(BatchResume.Intact(root, outDir, Path.Combine(root, "manifest.json"), digest));
            File.Delete(artifact);
            Assert.False(BatchResume.Intact(root, outDir, artifact, digest));
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void ManifestAlone_DoesNotAuthorizeReusingEditedGeneratedTests()
    {
        var rootDir = Path.Combine(Path.GetTempPath(), "testwizz-resume-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(rootDir, "source");
        var output = Path.Combine(rootDir, "output");
        var bundle = Path.Combine(output, "proposal");
        var testsDir = Path.Combine(bundle, "files", "tests");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(testsDir);
        try
        {
            const string generated = "using Xunit; public class SampleTests { [Fact] public void Test() {} }";
            var generatedFile = Path.Combine(testsDir, "SampleTests.cs");
            File.WriteAllText(generatedFile, generated);
            var fileSha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(generated)));
            var manifest = new JsonObject
            {
                ["schema"] = "testwizz.proposal/v1",
                ["id"] = "proposal",
                ["sources"] = new JsonArray(),
                ["files"] = new JsonArray(new JsonObject
                {
                    ["path"] = "tests/SampleTests.cs", ["sha256"] = fileSha,
                }),
            };
            var manifestPath = Path.Combine(bundle, "manifest.json");
            File.WriteAllText(manifestPath, manifest.ToJsonString());
            var manifestSha = BatchResume.ShaFile(manifestPath);
            Assert.True(BatchResume.IntactProposal(root, output, bundle, manifestSha));

            File.AppendAllText(generatedFile, "\n// intentionally edited");
            Assert.True(BatchResume.Intact(root, output, manifestPath, manifestSha));
            Assert.False(BatchResume.IntactProposal(root, output, bundle, manifestSha));
        }
        finally { Directory.Delete(rootDir, recursive: true); }
    }

    [Fact]
    public void ResumeFlags_RejectNonBatchCalls()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var app = new CliApp(stdout, stderr, Path.GetTempPath(), _ => null);
        Assert.Equal(2, app.Run(["generate", "--resume", "--for", "Foo.M", "--format", "json"],
            TestContext.Current.CancellationToken));
        Assert.Contains("usage", stdout.ToString(), StringComparison.Ordinal);

        stdout.GetStringBuilder().Clear();
        Assert.Equal(2, app.Run(["journey-draft", "--resume", "--workitem", "4711",
            "--plan", "../plan.json", "--case", "512", "--format", "json"],
            TestContext.Current.CancellationToken));
        Assert.Contains("usage", stdout.ToString(), StringComparison.Ordinal);

        stdout.GetStringBuilder().Clear();
        Assert.Equal(2, app.Run(["journey-draft", "--plan-id", "12", "--list-only",
            "--resume", "--format", "json"], TestContext.Current.CancellationToken));
        Assert.Contains("usage", stdout.ToString(), StringComparison.Ordinal);
    }
}
