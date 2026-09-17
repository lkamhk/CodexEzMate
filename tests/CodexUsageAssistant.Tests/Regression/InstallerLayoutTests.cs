using System.IO;
using CodexEzMate.UpdateCore;
using CodexUsageAssistant.Services;
using Xunit;

namespace CodexUsageAssistant.Tests;

public sealed class InstallerLayoutTests
{
    [Fact]
    public void CompactPackage_UsesRootForUpdatesAndIntegratedBrowserData()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezmate-layout-" + Guid.NewGuid());
        try
        {
            var app = Path.Combine(root, "app"); Directory.CreateDirectory(app);
            File.WriteAllText(Path.Combine(root, UpdateContract.ReleaseFile), "{}");
            File.WriteAllText(Path.Combine(root, UpdateContract.Executable), "fixture");
            Assert.Equal(root, InstallationPaths.ResolveRoot(app));
            Assert.Equal(root, SessionBrowserStore.ResolveDataRoot(app));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void FolderCalledApp_WithoutReleaseIdentityDoesNotResolveParent()
    {
        var app = Path.Combine(Path.GetTempPath(), "ezmate-no-package-" + Guid.NewGuid(), "app");
        Assert.Equal(app, InstallationPaths.ResolveRoot(app));
        var legacy = Path.Combine(Path.GetTempPath(), "ezmate-legacy-" + Guid.NewGuid());
        Assert.Equal(legacy, InstallationPaths.ResolveRoot(legacy));
    }

    [Fact]
    public void Update_PreservesInstallerRegistrationAndUserDataWithoutCopyingOldPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), "ezmate-upgrade-" + Guid.NewGuid());
        try
        {
            var old = Path.Combine(root, "old"); var next = Path.Combine(root, "next");
            foreach (var relative in new[] { "app/uninstall/unins000.exe", "app/uninstall/unins000.dat", "settings/pending-reset.json", "codexSessionBrowser/codex_session_bkup/conversation.jsonl", "app/old.dll" })
            {
                var path = Path.Combine(old, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, relative);
            }
            InstallTransaction.CopyUserData(old, next);
            Assert.True(File.Exists(Path.Combine(next, "app/uninstall/unins000.exe")));
            Assert.True(File.Exists(Path.Combine(next, "app/uninstall/unins000.dat")));
            Assert.True(File.Exists(Path.Combine(next, "settings/pending-reset.json")));
            Assert.True(File.Exists(Path.Combine(next, "codexSessionBrowser/codex_session_bkup/conversation.jsonl")));
            Assert.False(File.Exists(Path.Combine(next, "app/old.dll")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
