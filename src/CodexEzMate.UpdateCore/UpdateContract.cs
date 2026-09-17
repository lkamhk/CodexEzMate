using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexEzMate.UpdateCore;

public static class UpdateContract
{
    public const string AppId = "codex-ezmate";
    public const string Channel = "codex-ezmate-stable";
    public const string Endpoint = "https://nfqkislweudltvckonog.supabase.co/functions/v1/codex-ezmate-update";
    public const string Executable = "CodexEzMate.exe";
    public const string ReleaseFile = "ezmate-release.json";
    public const string SignaturePrefix = "rsa-pss-sha256:";
    public const long MaxZipBytes = 1024L * 1024 * 1024;
    public const long MaxExpandedBytes = 3L * 1024 * 1024 * 1024;

    public static string? PublicKey
    {
        get
        {
            using var stream = typeof(UpdateContract).Assembly.GetManifestResourceStream("CodexEzMate.UpdaterPublicKey.pem");
            if (stream is null) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    public static Version ParseVersion(string value)
    {
        if (!Regex.IsMatch(value, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(value, out var parsed)) throw new InvalidDataException("Invalid release version.");
        return parsed;
    }

    public static bool IsDropboxUri(Uri uri) => uri.Scheme == "https" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host == "dropbox.com" || uri.Host.EndsWith(".dropbox.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host == "dropboxusercontent.com" || uri.Host.EndsWith(".dropboxusercontent.com", StringComparison.OrdinalIgnoreCase));

    public static void ValidateManifest(UpdateManifest manifest, string current)
    {
        if (string.IsNullOrWhiteSpace(manifest.Version) || string.IsNullOrWhiteSpace(manifest.Url) ||
            string.IsNullOrWhiteSpace(manifest.Signature) || manifest.Notes is null) throw new InvalidDataException("Incomplete update manifest.");
        if (ParseVersion(manifest.Version) <= ParseVersion(current)) throw new InvalidDataException("Release is not newer.");
        if (!Uri.TryCreate(manifest.Url, UriKind.Absolute, out var uri) || !IsDropboxUri(uri)) throw new InvalidDataException("Invalid Dropbox URL.");
        if (!manifest.Signature.StartsWith(SignaturePrefix, StringComparison.Ordinal) || manifest.Signature.Length > 4096)
            throw new InvalidDataException("Unsupported signature format.");
        if (manifest.Notes.Length > 32768) throw new InvalidDataException("Release notes exceed size limit.");
    }
}

public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("notes")] string Notes = "",
    [property: JsonPropertyName("pub_date")] string? PublishedAt = null);

public sealed record ReleaseIdentity(string AppId, string Channel, string Version);
public sealed record UpdateRequest(string TargetDirectory, string ZipPath, UpdateManifest Manifest,
    string CurrentVersion, int ParentPid, long ParentStartTicks);
