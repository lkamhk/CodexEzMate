using System.Diagnostics;
using System.Text.Json;
using CodexEzMate.UpdateCore;

namespace CodexEzMate.Updater;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1) return;
        string? job = null;
        try
        {
            using var updateMutex = new Mutex(false, @"Local\CodexEzMate.Updating");
            if (Mutex.TryOpenExisting(@"Local\CodexEzMate.Setup", out var setupMutex))
            {
                setupMutex.Dispose();
                throw new IOException("Close Setup before applying an update.");
            }
            var requestPath = Path.GetFullPath(args[0]);
            job = Path.GetDirectoryName(requestPath)!;
            var allowed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexEzMate", "updates") + Path.DirectorySeparatorChar;
            if (!requestPath.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid update job directory.");
            InstallTransaction.ValidateDirectory(job);
            var request = JsonSerializer.Deserialize<UpdateRequest>(File.ReadAllText(requestPath)) ?? throw new InvalidDataException("Invalid update request.");
            Install(request, job);
            WriteLog(job, "update_succeeded");
        }
        catch (Exception ex)
        {
            if (job is not null) WriteLog(job, "update_failed_" + ex.GetType().Name);
            MessageBox.Show("更新未完成，已保留或嘗試還原舊版本。請查看 logs/updater.log，關閉會話瀏覽器後重試。\nUpdate failed. The previous version was retained or rollback was attempted. Check logs/updater.log.",
                "Codex EzMate Updater", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void Install(UpdateRequest request, string job)
    {
        var target = Path.GetFullPath(request.TargetDirectory).TrimEnd(Path.DirectorySeparatorChar);
        InstallTransaction.ValidateDirectory(target);
        var parent = Path.GetDirectoryName(target)!;
        using var installationLock = new FileStream(Path.Combine(parent, "." + Path.GetFileName(target) + ".update.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (AppContext.BaseDirectory.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Updater must run outside the installation directory.");
        if (Directory.Exists(Path.Combine(target, ".git")) || File.Exists(Path.Combine(target, "CodexUsageAssistant.sln")))
            throw new IOException("Cannot replace a source checkout.");
        var identity = JsonSerializer.Deserialize<ReleaseIdentity>(File.ReadAllText(Path.Combine(target, UpdateContract.ReleaseFile)));
        if (identity?.AppId != UpdateContract.AppId || identity.Channel != UpdateContract.Channel || identity.Version != request.CurrentVersion)
            throw new InvalidDataException("Installed release identity mismatch.");
        var executable = Path.Combine(target, UpdateContract.Executable);
        var installed = FileVersionInfo.GetVersionInfo(executable);
        if ($"{installed.FileMajorPart}.{installed.FileMinorPart}.{installed.FileBuildPart}" != request.CurrentVersion)
            throw new InvalidDataException("Installed executable version mismatch.");
        UpdateContract.ValidateManifest(request.Manifest, request.CurrentVersion);
        var key = UpdateContract.PublicKey ?? throw new InvalidDataException("Updater public key is not embedded.");
        if (!string.Equals(Path.GetFullPath(request.ZipPath), Path.Combine(job, "download.zip"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ZIP path is outside the update job.");
        var suffix = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(parent, ".ezmate-stage-" + suffix);
        var backup = Path.Combine(parent, ".ezmate-backup-" + suffix);
        // PID reuse must never cause an unrelated process to be stopped or waited on.
        try
        {
            using var app = Process.GetProcessById(request.ParentPid);
            if (app.StartTime.ToUniversalTime().Ticks != request.ParentStartTicks) throw new IOException("Parent process identity changed.");
            if (!app.WaitForExit(30000)) throw new IOException("Application has not exited.");
        }
        catch (ArgumentException) { }
        try
        {
            SignedPackage.ExtractVerified(request.ZipPath, request.Manifest.Signature, key, staging, request.Manifest.Version);
            var nextVersion = FileVersionInfo.GetVersionInfo(Path.Combine(staging, UpdateContract.Executable));
            if ($"{nextVersion.FileMajorPart}.{nextVersion.FileMinorPart}.{nextVersion.FileBuildPart}" != request.Manifest.Version)
                throw new InvalidDataException("New executable version mismatch.");
            InstallTransaction.CopyUserData(target, staging);
            InstallTransaction.Swap(target, staging, backup, () =>
            {
                var ready = Path.Combine(job, "ready-" + suffix);
                var start = new ProcessStartInfo(Path.Combine(target, UpdateContract.Executable)) { UseShellExecute = false, WorkingDirectory = target, CreateNoWindow = true };
                start.ArgumentList.Add("--update-ready"); start.ArgumentList.Add(ready); start.ArgumentList.Add(suffix);
                using var process = Process.Start(start) ?? throw new IOException("Updated app did not start.");
                var deadline = Stopwatch.StartNew();
                while (deadline.Elapsed < TimeSpan.FromSeconds(30))
                {
                    if (File.Exists(ready) && File.ReadAllText(ready) == suffix) return;
                    if (process.HasExited) break;
                    Thread.Sleep(200);
                }
                if (!process.HasExited) { process.Kill(true); process.WaitForExit(5000); }
                throw new IOException("Updated app did not acknowledge startup.");
            });
        }
        catch
        {
            // Only restart the original app if rollback restored its signed release identity.
            var restored = JsonSerializer.Deserialize<ReleaseIdentity>(File.ReadAllText(Path.Combine(target, UpdateContract.ReleaseFile)));
            if (restored?.Version == request.CurrentVersion)
                Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = target, CreateNoWindow = true });
            try
            {
                File.Delete(request.ZipPath);
                if (Directory.Exists(staging))
                {
                    InstallTransaction.ValidateDirectory(staging);
                    // This exact sibling path was generated above and contains only this attempt's files.
                    if (Path.GetDirectoryName(staging) != parent || Path.GetFileName(staging) != ".ezmate-stage-" + suffix)
                        throw new IOException("Unexpected staging path.");
                    Directory.Delete(staging, true);
                }
            }
            catch (IOException) { WriteLog(job, "staging_cleanup_failed"); }
            throw;
        }
        try { File.Delete(request.ZipPath); File.Delete(Path.Combine(job, "request.json")); }
        catch (IOException) { WriteLog(job, "temporary_cleanup_failed"); }
    }

    private static void WriteLog(string job, string result)
    {
        try { Directory.CreateDirectory(Path.Combine(job, "logs")); File.AppendAllText(Path.Combine(job, "logs", "updater.log"), $"{DateTimeOffset.Now:O} {result}\n"); }
        catch (IOException) { }
    }
}
