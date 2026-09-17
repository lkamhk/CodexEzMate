using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexEzMate.UpdateCore;

public static class SignedPackage
{
    public static void Verify(Stream zip, string signature, string publicKey)
    {
        if (!signature.StartsWith(UpdateContract.SignaturePrefix, StringComparison.Ordinal)) throw new CryptographicException("Unsupported signature format.");
        using var rsa = RSA.Create(); rsa.ImportFromPem(publicKey);
        if (rsa.KeySize < 3072) throw new CryptographicException("RSA key must be at least 3072 bits.");
        var bytes = Convert.FromBase64String(signature[UpdateContract.SignaturePrefix.Length..]);
        if (!rsa.VerifyData(zip, bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw new CryptographicException("Update signature verification failed.");
        zip.Position = 0;
    }

    public static string SafeEntryPath(string root, string name)
    {
        var relative = name.Replace('\\', '/');
        if (relative.Length == 0 || relative.StartsWith('/') || relative.Contains(':')) throw new InvalidDataException("Unsafe ZIP path.");
        var parts = relative.TrimEnd('/').Split('/');
        foreach (var part in parts)
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 || part.Any(char.IsControl) ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Unsafe ZIP path.");
        var full = Path.GetFullPath(Path.Combine(root, Path.Combine(parts)));
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("ZIP path escapes staging.");
        return full;
    }

    public static void ExtractVerified(string zipPath, string signature, string publicKey, string destination, string expectedVersion)
    {
        if (Directory.Exists(destination)) throw new IOException("Staging directory must be new.");
        using var input = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > UpdateContract.MaxZipBytes) throw new InvalidDataException("Update ZIP exceeds size limit.");
        Verify(input, signature, publicKey);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read);
        if (archive.Entries.Count > 50000) throw new InvalidDataException("Too many ZIP entries.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var target = SafeEntryPath(destination, entry.FullName);
            if (!names.Add(target)) throw new InvalidDataException("Duplicate ZIP path.");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("ZIP links are not allowed.");
            total = checked(total + entry.Length);
            if (total > UpdateContract.MaxExpandedBytes) throw new InvalidDataException("Expanded ZIP exceeds size limit.");
        }
        var identityEntry = archive.GetEntry(UpdateContract.ReleaseFile) ?? throw new InvalidDataException("Missing release identity.");
        if (identityEntry.Length > 4096) throw new InvalidDataException("Invalid release identity.");
        using (var identityStream = identityEntry.Open())
        {
            var identity = JsonSerializer.Deserialize<ReleaseIdentity>(identityStream) ?? throw new InvalidDataException("Invalid release identity.");
            if (identity.AppId != UpdateContract.AppId || identity.Channel != UpdateContract.Channel || identity.Version != expectedVersion)
                throw new InvalidDataException("Signed release identity does not match manifest.");
        }
        if (archive.GetEntry(UpdateContract.Executable) is null) throw new InvalidDataException("Missing application executable.");
        Directory.CreateDirectory(destination);
        long copied = 0;
        foreach (var entry in archive.Entries)
        {
            var target = SafeEntryPath(destination, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open(); using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920]; int read;
            while ((read = source.Read(buffer)) != 0)
            {
                copied = checked(copied + read);
                if (copied > UpdateContract.MaxExpandedBytes) throw new InvalidDataException("Expanded ZIP exceeds size limit.");
                output.Write(buffer, 0, read);
            }
        }
    }
}
