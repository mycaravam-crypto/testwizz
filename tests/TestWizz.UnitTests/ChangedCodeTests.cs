using System.Diagnostics;
using TestWizz.DotNet;

namespace TestWizz.UnitTests;

public sealed class ChangedCodeTests
{
    [Fact]
    public void SelectsOnlyMethodIntersectingChangedLines()
    {
        var root = Directory.CreateTempSubdirectory("testwizz-since-").FullName;
        try
        {
            Git(root, "init", "-q");
            Git(root, "config", "user.name", "TestWizz");
            Git(root, "config", "user.email", "testwizz@example.invalid");
            File.WriteAllText(Path.Combine(root, "Demo.cs"), "class Demo { int A() => 1; int B() => 2; }\n");
            Git(root, "add", "Demo.cs");
            Git(root, "commit", "-qm", "initial");
            var baseline = Git(root, "rev-parse", "HEAD").Trim();
            File.WriteAllText(Path.Combine(root, "Demo.cs"), "class Demo {\n int A() => 1;\n int B() => 3;\n}\n");
            Git(root, "add", "Demo.cs");
            Git(root, "commit", "-qm", "change");

            Assert.Equal(["Demo.B"], ChangedCode.Methods(root, baseline));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string Git(string root, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output;
    }
}
