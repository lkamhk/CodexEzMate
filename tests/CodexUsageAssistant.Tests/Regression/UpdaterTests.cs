using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using CodexEzMate.UpdateCore;
using Xunit;

namespace CodexUsageAssistant.Tests;

public sealed class UpdaterTests
{
#if CODEX_RELEASE_TOOLS
    [Fact]
    public void EncryptedReleaseKey_SignsBytesAcceptedByClient()
    {
        var root = NewRoot();
        try
        {
            var privatePath = Path.Combine(root, "fixture.private.pem"); var publicPath = Path.Combine(root, "fixture.public.pem");
            var password = Guid.NewGuid().ToString("N");
            CodexEzMate.ReleaseTools.Signing.CreateKey(privatePath, publicPath, password);
            Assert.StartsWith("-----BEGIN ENCRYPTED PRIVATE KEY-----", File.ReadAllText(privatePath));
            var error = Assert.Throws<CodexEzMate.ReleaseTools.SigningKeyException>(() => CodexEzMate.ReleaseTools.Signing.OpenKey(privatePath, publicPath, "invalid-test-password"));
            Assert.Equal("decrypt_failed", error.Code);
            using var rsa = CodexEzMate.ReleaseTools.Signing.OpenKey(privatePath, publicPath, password);
            var zip = Package(root, rsa, out var signature);
            Assert.True(CodexEzMate.ReleaseTools.Signing.Verify(zip, publicPath, signature));
            using var input = File.OpenRead(zip); SignedPackage.Verify(input, signature, File.ReadAllText(publicPath));
            using var otherKey = RSA.Create(3072);
            File.WriteAllText(publicPath, otherKey.ExportSubjectPublicKeyInfoPem());
            var mismatch = Assert.Throws<CodexEzMate.ReleaseTools.SigningKeyException>(() => CodexEzMate.ReleaseTools.Signing.OpenKey(privatePath, publicPath, password));
            Assert.Equal("key_mismatch", mismatch.Code);
        }
        finally { Directory.Delete(root, true); }
    }
#endif
    [Theory]
    [InlineData("../escape.exe")]
    [InlineData("/absolute.exe")]
    [InlineData("C:\\outside.exe")]
    [InlineData("dir/../../escape.exe")]
    [InlineData("file.exe:stream")]
    [InlineData("file. ")]
    [InlineData("CON.txt")]
    [InlineData("folder\\..\\escape")]
    public void ZipPaths_RejectTraversalAndWindowsAliases(string name) =>
        Assert.Throws<InvalidDataException>(() => SignedPackage.SafeEntryPath(Path.GetTempPath(), name));

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "ezmate-updater-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    private static string Package(string root, RSA rsa, out string signature, string? unsafeName = null, string version = "2.0.0")
    {
        var zip = Path.Combine(root, "test.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(archive.CreateEntry(UpdateContract.ReleaseFile).Open()))
                writer.Write(JsonSerializer.Serialize(new ReleaseIdentity(UpdateContract.AppId, UpdateContract.Channel, version)));
            using (var writer = new StreamWriter(archive.CreateEntry(UpdateContract.Executable).Open())) writer.Write("test fixture, not executable");
            if (unsafeName is not null) { using var writer = new StreamWriter(archive.CreateEntry(unsafeName).Open()); writer.Write("unsafe"); }
        }
        using var input = File.OpenRead(zip);
        signature = UpdateContract.SignaturePrefix + Convert.ToBase64String(rsa.SignData(input, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        return zip;
    }

    [Fact]
    public void ValidSignedPackage_ExtractsOnlyAfterVerification()
    {
        var root = NewRoot();
        try
        {
            using var rsa = RSA.Create(3072); var zip = Package(root, rsa, out var signature);
            var destination = Path.Combine(root, "stage");
            SignedPackage.ExtractVerified(zip, signature, rsa.ExportSubjectPublicKeyInfoPem(), destination, "2.0.0");
            Assert.True(File.Exists(Path.Combine(destination, UpdateContract.Executable)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TamperedPackage_NeverCreatesStaging()
    {
        var root = NewRoot();
        try
        {
            using var rsa = RSA.Create(3072); var zip = Package(root, rsa, out var signature);
            File.AppendAllText(zip, "tampered"); var stage = Path.Combine(root, "stage");
            Assert.Throws<CryptographicException>(() => SignedPackage.ExtractVerified(zip, signature, rsa.ExportSubjectPublicKeyInfoPem(), stage, "2.0.0"));
            Assert.False(Directory.Exists(stage));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("../outside.exe", "2.0.0")]
    [InlineData("codexeZmate.exe", "2.0.0")]
    [InlineData(null, "3.0.0")]
    public void SignedButInvalidPackage_IsRejected(string? entry, string expectedVersion)
    {
        var root = NewRoot();
        try
        {
            using var rsa = RSA.Create(3072); var zip = Package(root, rsa, out var signature, entry);
            Assert.Throws<InvalidDataException>(() => SignedPackage.ExtractVerified(zip, signature, rsa.ExportSubjectPublicKeyInfoPem(), Path.Combine(root, "stage"), expectedVersion));
            Assert.False(File.Exists(Path.Combine(root, "outside.exe")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AtomicSwap_RestoresOldVersionWhenMoveOrStartupFails(bool startupFailure)
    {
        var root = NewRoot();
        try
        {
            var target = Path.Combine(root, "app"); var stage = Path.Combine(root, "stage"); var backup = Path.Combine(root, "backup");
            Directory.CreateDirectory(target); Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(target, "old.txt"), "old"); File.WriteAllText(Path.Combine(stage, "new.txt"), "new");
            Assert.Throws<IOException>(() => InstallTransaction.Swap(target, stage, backup,
                () => { if (startupFailure) throw new IOException("Simulated startup failure"); },
                (from, to) => { if (!startupFailure && from == stage) throw new IOException("Simulated locked file"); Directory.Move(from, to); }));
            Assert.Equal("old", File.ReadAllText(Path.Combine(target, "old.txt")));
            Assert.False(File.Exists(Path.Combine(target, "new.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Success_PreservesSettingsAndKeepsBackup()
    {
        var root = NewRoot();
        try
        {
            var target = Path.Combine(root, "app"); var stage = Path.Combine(root, "stage"); var backup = Path.Combine(root, "backup");
            Directory.CreateDirectory(Path.Combine(target, "settings")); Directory.CreateDirectory(stage);
            File.WriteAllText(Path.Combine(target, "settings", "session-browser.json"), "saved preferences");
            File.WriteAllText(Path.Combine(stage, "new.txt"), "new");
            InstallTransaction.CopyUserData(target, stage);
            InstallTransaction.Swap(target, stage, backup, () => { });
            Assert.Equal("saved preferences", File.ReadAllText(Path.Combine(target, "settings", "session-browser.json")));
            Assert.True(Directory.Exists(backup));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Manifest_RejectsDowngradeAndUntrustedUrl()
    {
        var manifest = new UpdateManifest("2.0.0", "https://www.dropbox.com/s/test.zip?dl=1", "rsa-pss-sha256:AA==");
        UpdateContract.ValidateManifest(manifest, "1.0.0");
        Assert.Throws<InvalidDataException>(() => UpdateContract.ValidateManifest(manifest, "2.0.0"));
        Assert.Throws<InvalidDataException>(() => UpdateContract.ValidateManifest(manifest with { Url = "https://dropbox.com.attacker.example/test.zip" }, "1.0.0"));
        Assert.Throws<InvalidDataException>(() => UpdateContract.ValidateManifest(manifest with { Signature = "unsigned" }, "1.0.0"));
    }
}
