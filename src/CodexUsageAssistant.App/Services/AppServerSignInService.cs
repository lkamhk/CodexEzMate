using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;

namespace CodexUsageAssistant.Services;

public interface IAppServerSignInService
{
    Task<bool> SignInAsync(string? executable, CancellationToken token);
}

public sealed class AppServerSignInService : IAppServerSignInService
{
    public async Task<bool> SignInAsync(string? executable, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lifetime.CancelAfter(TimeSpan.FromMinutes(5));
        var message = new TextBlock { Text = L("正在開啟 Codex 瀏覽器登入…", "Opening Codex browser sign-in…"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) };
        var cancel = new System.Windows.Controls.Button { Content = L("取消", "Cancel"), Padding = new Thickness(18, 7, 18, 7), HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new StackPanel { Margin = new Thickness(24) }; panel.Children.Add(message); panel.Children.Add(cancel);
        var window = new Window
        {
            Title = L("Codex EzMate v1.21.4 — 登入 Codex", "Codex EzMate v1.21.4 — Sign in to Codex"),
            Width = 480, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = panel,
            Background = new SolidColorBrush(Color.FromRgb(234, 241, 245)), Foreground = new SolidColorBrush(Color.FromRgb(36, 59, 73))
        };
        var userCancelled = false;
        cancel.Click += (_, _) => { userCancelled = true; lifetime.Cancel(); };
        window.Closed += (_, _) => { userCancelled = true; lifetime.Cancel(); };
        window.Show(); window.Activate();
        try
        {
            using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            connectionTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var client = await AppServerClient.ConnectAsync(executable, connectionTimeout.Token);
            return await RunFlowAsync(client.RequestAsync, client.WaitForLoginAsync,
                uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }),
                text => message.Text = text, lifetime.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            if (!userCancelled) throw new InvalidOperationException("Codex sign-in timed out.");
            return false;
        }
        finally { window.Close(); }
    }

    internal static Uri ValidateAuthUri(string value)
    {
        var uri = new Uri(value);
        if (uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) ||
            !(uri.Host == "chatgpt.com" || uri.Host == "openai.com" || uri.Host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Unexpected authentication URL.");
        return uri;
    }

    internal static async Task<bool> RunFlowAsync(
        Func<string, object?, CancellationToken, Task<JsonElement>> request,
        Func<string, CancellationToken, Task<bool>> waitForLogin,
        Action<Uri> openBrowser, Action<string> progress, CancellationToken token)
    {
        string? loginId = null;
        try
        {
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await request("account/login/start", new { type = "chatgpt" }, requestTimeout.Token);
            loginId = result.GetProperty("loginId").GetString();
            if (string.IsNullOrWhiteSpace(loginId)) throw new InvalidDataException("Missing login identifier.");
            var uri = ValidateAuthUri(result.GetProperty("authUrl").GetString()!);
            openBrowser(uri);
            progress(L("請在瀏覽器完成登入；完成後會自動取得額度。", "Complete sign-in in your browser; usage will refresh automatically."));
            if (!await waitForLogin(loginId, token)) throw new InvalidOperationException("Codex sign-in did not complete.");
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var account = await request("account/read", new { refreshToken = false }, checkTimeout.Token);
            if (AppServerHostRuntime.ClassifyAccount(account) != HostProbe.Ready) throw new InvalidOperationException("Codex is not signed in.");
            loginId = null;
            return true;
        }
        finally
        {
            if (loginId is not null)
            {
                try
                {
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await request("account/login/cancel", new { loginId }, cancellation.Token);
                }
                catch (Exception ex) when (CodexAppServerHost.IsExpected(ex)) { }
            }
        }
    }

    private static string L(string zh, string en) => LocalizationService.Pick(zh, en);
}
