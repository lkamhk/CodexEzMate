using System.IO;
using CodexEzMate.UpdateCore;

namespace CodexUsageAssistant.Services;

public static class InstallationPaths
{
    public static string Root => ResolveRoot(AppContext.BaseDirectory);

    internal static string ResolveRoot(string applicationDirectory)
    {
        var directory = new DirectoryInfo(applicationDirectory);
        // Debug and older portable releases keep their original layout.
        if (directory.Name.Equals("app", StringComparison.OrdinalIgnoreCase) && directory.Parent is { } parent
            && File.Exists(Path.Combine(parent.FullName, UpdateContract.ReleaseFile))
            && File.Exists(Path.Combine(parent.FullName, UpdateContract.Executable)))
            return parent.FullName;
        return directory.FullName;
    }
}
