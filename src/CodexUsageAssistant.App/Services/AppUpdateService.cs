using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using CodexEzMate.UpdateCore;

namespace CodexUsageAssistant.Services;

public sealed class AppUpdateService(ISettingsService settings)
{
    public static string CurrentVersion => typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(UpdateContract.PublicKey);
    public static bool CanInstall => IsConfigured && File.Exists(Path.Combine(InstallationPaths.Root, UpdateContract.ReleaseFile));

    private async Task<HttpClient> CreateClientAsync(CancellationToken token)
    {
        var options = await settings.LoadAsync(token);
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        if (options?.ProxyEnabled == true && ProxyConfiguration.NormalizeAndValidate(options.ProxyServer) is { } proxyUri)
        {
            var proxy = new WebProxy(proxyUri);
            if (!string.IsNullOrWhiteSpace(options.ProxyUsername)) proxy.Credentials = new NetworkCredential(options.ProxyUsername, CredentialProtector.Unprotect(options.EncryptedProxyPassword));
            handler.Proxy = proxy; handler.UseProxy = true;
        }
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<UpdateManifest?> CheckAsync(CancellationToken token)
    {
        if (!IsConfigured) throw new InvalidOperationException("Update public key has not been configured.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var client = await CreateClientAsync(timeout.Token);
        var url = $"{UpdateContract.Endpoint}?channel={UpdateContract.Channel}&target=windows&arch=x86_64&current_version={CurrentVersion}";
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NoContent) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var bytes = new MemoryStream(); await CopyLimitedAsync(stream, bytes, 65536, null, timeout.Token);
        var manifest = JsonSerializer.Deserialize<UpdateManifest>(bytes.ToArray()) ?? throw new InvalidDataException("Empty update manifest.");
        UpdateContract.ValidateManifest(manifest, CurrentVersion);
        return manifest;
    }

    public async Task<string> DownloadAsync(UpdateManifest manifest, IProgress<string> progress, CancellationToken token)
    {
        if (!CanInstall) throw new InvalidOperationException("Automatic installation is only available in signed release packages.");
        UpdateContract.ValidateManifest(manifest, CurrentVersion);
        var job = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexEzMate", "updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(job);
        var zip = Path.Combine(job, "download.zip");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromMinutes(15));
            using var client = await CreateClientAsync(timeout.Token);
            var uri = new Uri(manifest.Url);
            for (var redirects = 0; ; redirects++)
            {
                if (!UpdateContract.IsDropboxUri(uri) || redirects > 8) throw new InvalidDataException("Unexpected download redirect.");
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if ((int)response.StatusCode is >= 300 and < 400)
                {
                    uri = response.Headers.Location is { } location ? new Uri(uri, location) : throw new InvalidDataException("Missing redirect location.");
                    continue;
                }
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > UpdateContract.MaxZipBytes) throw new InvalidDataException("Update is too large.");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using (var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await CopyLimitedAsync(input, output, UpdateContract.MaxZipBytes, progress, timeout.Token);
                break;
            }
            progress.Report(LocalizationService.Pick("正在驗證更新簽章…", "Verifying update signature…"));
            await Task.Run(() =>
            {
                using var file = File.OpenRead(zip); SignedPackage.Verify(file, manifest.Signature, UpdateContract.PublicKey!);
            }, token);
            return job;
        }
        catch { if (File.Exists(zip)) File.Delete(zip); throw; }
    }

    private static async Task CopyLimitedAsync(Stream input, Stream output, long limit, IProgress<string>? progress, CancellationToken token)
    {
        var buffer = new byte[81920]; long total = 0; int read;
        while ((read = await input.ReadAsync(buffer, token)) != 0)
        {
            total += read; if (total > limit) throw new InvalidDataException("Download exceeds size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token);
            progress?.Report($"{total / 1048576.0:0.0} MB");
        }
    }

    public async Task StartInstallerAsync(string job, UpdateManifest manifest)
    {
        var source = Path.Combine(AppContext.BaseDirectory, "runtime", "updater");
        if (!File.Exists(Path.Combine(source, "CodexEzMate.Updater.exe"))) throw new FileNotFoundException("Updater runtime is missing.");
        var copy = Path.Combine(job, "updater");
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(copy, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target, true);
        }
        using var current = Process.GetCurrentProcess();
        var request = new UpdateRequest(InstallationPaths.Root, Path.Combine(job, "download.zip"), manifest, CurrentVersion,
            current.Id, current.StartTime.ToUniversalTime().Ticks);
        var requestPath = Path.Combine(job, "request.json");
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), System.Text.Encoding.UTF8);
        var start = new ProcessStartInfo(Path.Combine(copy, "CodexEzMate.Updater.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = job };
        start.ArgumentList.Add(requestPath);
        using var installer = Process.Start(start) ?? throw new IOException("Installer did not start.");
        await ((App)System.Windows.Application.Current).RequestShutdownAsync();
    }

    public static void AcknowledgeUpdateStartup(string[] args)
    {
        if (args.Length != 3 || args[0] != "--update-ready" || !Guid.TryParseExact(args[2], "N", out _)) return;
        var allowed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexEzMate", "updates") + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(args[1]);
        if (path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path) == "ready-" + args[2])
            File.WriteAllText(path, args[2], System.Text.Encoding.UTF8);
    }
}
