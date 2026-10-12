using System.IO;

namespace CodexUsageAssistant.Services;

internal static class CodexExecutableLocator
{
    internal static string DesktopRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");

    internal static bool IsManagedPath(string? path, string? desktopRoot = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            if (!Path.GetFileName(path).Equals("codex.exe", StringComparison.OrdinalIgnoreCase)) return false;
            var parent = Directory.GetParent(path)?.Parent?.FullName;
            if (string.Equals(parent, Path.GetFullPath(desktopRoot ?? DesktopRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) return true;
            var storeRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps") + Path.DirectorySeparatorChar;
            return path.StartsWith(storeRoot + "OpenAI.Codex_", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return false; }
    }

    internal static string? LatestDesktop(string? root = null)
    {
        root ??= DesktopRoot;
        if (!Directory.Exists(root)) return null;
        try
        {
            return Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "codex.exe"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(p => p, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static string ResolveManaged(string path, string? root = null) =>
        IsManagedPath(path, root) ? LatestDesktop(root) ?? path : path;
}
