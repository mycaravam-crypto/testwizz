namespace TestWizz.UnitTests;

internal static class TestSymlinks
{
    // ERROR_PRIVILEGE_NOT_HELD: Windows needs Developer Mode or an assigned symlink privilege.
    private const int PrivilegeNotHeld = 1314;

    public static void CreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
        }
        catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xFFFF) == PrivilegeNotHeld)
        {
            Assert.Skip("Creating symbolic links requires Windows Developer Mode or SeCreateSymbolicLinkPrivilege.");
        }
    }

    public static void CreateFileLink(string link, string target)
    {
        try
        {
            System.IO.File.CreateSymbolicLink(link, target);
        }
        catch (IOException ex) when (OperatingSystem.IsWindows() && (ex.HResult & 0xFFFF) == PrivilegeNotHeld)
        {
            Assert.Skip("Creating symbolic links requires Windows Developer Mode or SeCreateSymbolicLinkPrivilege.");
        }
    }
}

internal static class TestTempDirectories
{
    public static void Delete(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // Git can create read-only object files on Windows, which block recursive deletion.
        // Do not follow reparse points when clearing attributes in temporary test repositories.
        if (OperatingSystem.IsWindows())
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
            {
                var attributes = System.IO.File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    System.IO.File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }
        }

        Directory.Delete(path, recursive: true);
    }
}
