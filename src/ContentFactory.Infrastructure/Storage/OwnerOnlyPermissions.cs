namespace ContentFactory.Infrastructure.Storage;

/// <summary>Owner-only modes (0600 files, 0700 directories) on Unix-like systems.</summary>
internal static class OwnerOnlyPermissions
{
    public static void File(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            System.IO.File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    public static void Directory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            System.IO.File.SetUnixFileMode(
                path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
