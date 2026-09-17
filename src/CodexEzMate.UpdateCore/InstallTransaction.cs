namespace CodexEzMate.UpdateCore;

public static class InstallTransaction
{
    public static void ValidateDirectory(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (full.Length < 8 || full == Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar))
            throw new IOException("Cannot update a filesystem root.");
        for (var directory = new DirectoryInfo(full); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Update directories must not traverse links.");
    }

    public static void CopyUserData(string source, string destination)
    {
        foreach (var relative in new[] { "settings", "logs", "_browser_profiles", "codex_session_bkup", "codexSessionBrowser/codex_session_bkup", "app/uninstall" })
        {
            var from = Path.Combine(source, relative); var to = Path.Combine(destination, relative);
            if (Directory.Exists(from)) CopyDirectory(from, to);
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        ValidateDirectory(from); ValidateDirectory(to);
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("User data contains a link.");
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        }
        foreach (var folder in Directory.EnumerateDirectories(from)) CopyDirectory(folder, Path.Combine(to, Path.GetFileName(folder)));
    }

    public static void Swap(string target, string staging, string backup, Action verifyStarted,
        Action<string, string>? move = null)
    {
        foreach (var path in new[] { target, staging, backup }) ValidateDirectory(path);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(target)), Path.GetDirectoryName(Path.GetFullPath(staging)), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(Path.GetFullPath(target)), Path.GetDirectoryName(Path.GetFullPath(backup)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Atomic update paths must be siblings.");
        if (Directory.Exists(backup)) throw new IOException("Backup destination already exists.");
        move ??= MoveWithRetry;
        var oldMoved = false; var newMoved = false;
        try
        {
            move(target, backup); oldMoved = true;
            move(staging, target); newMoved = true;
            verifyStarted();
        }
        catch
        {
            if (newMoved) move(target, staging);
            if (oldMoved) move(backup, target);
            throw;
        }
    }

    private static void MoveWithRetry(string from, string to)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { Directory.Move(from, to); return; }
            catch (IOException) when (attempt < 4) { Thread.Sleep(300); }
        }
    }
}
