using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexUsageAssistant.Services;
using CodexUsageAssistant.ViewModels;
using CodexUsageAssistant.Views;
using Xunit;

namespace CodexUsageAssistant.Tests;

public sealed class IntegrationUiTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hant")]
    [InlineData("zh-Hans")]
    public void SettingsWindow_RendersUsageSourceOptions(string language)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var scheduler = new AutoResumeScheduler(null!, null!, null!, null!, null!);
                var window = new SettingsWindow(null!, null!);
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/CodexEzMate;component/Resources/Strings.{language}.xaml")
                });
                var content = (System.Windows.Controls.DockPanel)window.Content;
                var tabs = content.Children.OfType<System.Windows.Controls.TabControl>().Single();
                ((System.Windows.Controls.TabItem)window.FindName("BasicSettingsTabItem")).IsSelected = true;
                content.Measure(new Size(820, 580));
                content.Arrange(new Rect(0, 0, 820, 580));
                content.UpdateLayout();
                Assert.True(((FrameworkElement)window.FindName("AutoRefreshMinutesTextBox")).ActualWidth > 0);
                Assert.True(((FrameworkElement)window.FindName("ServerNotificationCheckBox")).ActualWidth > 0);
                Assert.DoesNotContain("?", window.FindResource("BasicSettings").ToString()!);
                var basicOutput = Environment.GetEnvironmentVariable("CODEX_UI_PREVIEW_ROOT");
                if (!string.IsNullOrWhiteSpace(basicOutput))
                {
                    Directory.CreateDirectory(basicOutput);
                    var basicBitmap = new RenderTargetBitmap(820, 580, 96, 96, PixelFormats.Pbgra32);
                    basicBitmap.Render(content);
                    var basicEncoder = new PngBitmapEncoder();
                    basicEncoder.Frames.Add(BitmapFrame.Create(basicBitmap));
                    using var basicStream = File.Create(Path.Combine(basicOutput, $"basic-settings-{language}.png"));
                    basicEncoder.Save(basicStream);
                }
                Assert.Equal(3, tabs.Items.Count);
                Assert.Null(window.FindName("UsageSourceTabItem"));
                Assert.Null(window.FindName("ServerHostTabItem"));
                Assert.Null(window.FindName("GoalMonitorTabItem"));
                Assert.NotNull(window.FindName("TrayUsageCheckBox"));
                Assert.NotNull(window.FindName("AutoUpdateCheckBox"));
                var radio = (System.Windows.Controls.RadioButton)window.FindName("AppServerFallbackRadio");
                radio.IsChecked = true;
                content.Measure(new Size(820, 580));
                content.Arrange(new Rect(0, 0, 820, 580));
                content.UpdateLayout();
                Assert.True(radio.ActualWidth > 0);
                Assert.NotNull(window.FindName("CodexExecutableTextBox"));
                Assert.NotNull(window.FindName("SimplifiedLanguageRadio"));
                var output = Environment.GetEnvironmentVariable("CODEX_UI_PREVIEW_ROOT");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    var bitmap = new RenderTargetBitmap(820, 580, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, $"usage-source-settings-{language}.png"));
                    encoder.Save(stream);
                }
                foreach (var manager in new Window[] { new ServerHostWindow(null!, null), new GoalMonitorWindow(null!, null!, scheduler) })
                {
                    manager.Resources.MergedDictionaries.Add(new ResourceDictionary
                    { Source = new Uri($"pack://application:,,,/CodexEzMate;component/Resources/Strings.{language}.xaml") });
                    if (manager is GoalMonitorWindow)
                    {
                        var grid = (System.Windows.Controls.DataGrid)manager.FindName("ConversationsGrid");
                        grid.ItemsSource = new[] { new CodexUsageAssistant.Models.ConversationTarget { ProjectName = "Sample project", Title = "Sample conversation", Source = "vscode", GoalStatus = "active", Compatibility = "Original Desktop" } };
                        var keys = new[] { "MonitorColumn", "ProjectColumn", "ConversationColumn", "IdentityColumn", "LastResultColumn", "GoalSource", "Goal", "GoalCompatibility" };
                        for (var index = 0; index < keys.Length; index++) grid.Columns[index].Header = keys[index] == "Goal" ? "Goal" : manager.FindResource(keys[index]);
                    }
                    var panel = (FrameworkElement)manager.Content;
                    var width = (int)manager.Width; var height = (int)manager.Height - 40;
                    panel.Measure(new Size(width, height)); panel.Arrange(new Rect(0, 0, width, height)); panel.UpdateLayout();
                    Assert.Null(manager.Owner);
                    Assert.Equal(ResizeMode.CanResize, manager.ResizeMode);
                    if (manager is ServerHostWindow)
                    {
                        ((System.Windows.Controls.TextBox)manager.FindName("HostPathTextBox")).Text = @"C:\Tools\Codex\codex.exe";
                        ((System.Windows.Controls.TextBox)manager.FindName("HostPortTextBox")).Text = "4500";
                        ((System.Windows.Controls.TextBox)manager.FindName("HostWorkTextBox")).Text = @"C:\Users\Example\AppData\Local\CodexUsageAssistant\app-server-work";
                        panel.UpdateLayout();
                        Assert.True(((FrameworkElement)manager.FindName("HostPathTextBox")).ActualWidth > 0);
                        Assert.DoesNotContain("Saladict", manager.FindResource("HostHelp").ToString()!);
                    }
                    else
                    {
                        var grid = (System.Windows.Controls.DataGrid)manager.FindName("ConversationsGrid");
                        Assert.True(grid.ActualHeight > 0);
                        grid.SelectedItem = grid.Items[0];
                        var target = (CodexUsageAssistant.Models.ConversationTarget)grid.SelectedItem;
                        target.LastStatus = CodexUsageAssistant.Models.ConversationResumeStatus.Queued;
                        target.LastMessage = language == "en" ? "Recovery message queued; waiting for the original Desktop. Keep Desktop open."
                            : language == "zh-Hans" ? "已排入恢复消息，等待原 Desktop 执行；请保持 Desktop 开启。"
                            : "已排入恢復訊息，等待原 Desktop 執行；請保持 Desktop 開啟。";
                        panel.Measure(new Size(width, height)); panel.Arrange(new Rect(0, 0, width, height));
                        panel.UpdateLayout();
                        Assert.Equal(target.LastMessage, ((System.Windows.Controls.TextBox)manager.FindName("SelectedGoalResultText")).Text);
                    }
                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(panel);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var stream = File.Create(Path.Combine(output, $"{manager.GetType().Name}-{language}.png")); encoder.Save(stream);
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(failure);
    }

    [Fact]
    public void Browser_StandaloneDataPathsPreserveExistingBackupFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "session-launch-" + Guid.NewGuid());
        try
        {
            Directory.CreateDirectory(root);
            var store = new SessionBrowserStore(Path.Combine(root, "codex"), root);
            Assert.Equal(Path.Combine(root, "codexSessionBrowser", "codex_session_bkup"), store.BackupRoot);
            Assert.Equal(Path.Combine(root, "settings", "session-browser.json"), store.SettingsPath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Browser_DevelopmentFindsExistingDataFromDebugDirectory()
    {
        var root = SessionBrowserStore.ResolveDataRoot(AppContext.BaseDirectory);
        Assert.True(File.Exists(Path.Combine(root, "CodexUsageAssistant.sln")));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("zh-Hant")]
    [InlineData("zh-Hans")]
    public void DetailsWindow_RendersLocalizedUsageAndThreeResets(string language)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var vm = new FloatingBallViewModel(null!, null!, null!, null!, null!, null!, null!, null!)
                {
                    WeeklyValue = 64, WeeklyPercent = "64%", FiveHourValue = 77, FiveHourPercent = "77%",
                    WeeklyResetDisplay = "09-21 11:57", FiveHourResetDisplay = "09-15 04:21",
                    AvailableUsageResetDisplay = language == "en" ? "3 resets" : "3 次",
                    UsageResetExpiresDisplay = "Full reset: 2026-09-21\nFull reset: 2026-10-04\nFull reset: 2026-10-05",
                    LastUpdated = "00:02", StatusMessage = language == "en" ? "Usage updated" : "用量已更新"
                };
                vm.PlanDisplay = "Pro";
                var window = new TrayDetailsWindow(vm);
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/CodexEzMate;component/Resources/Strings.{language}.xaml")
                });
                var content = (FrameworkElement)window.Content;
                content.DataContext = vm;
                content.Measure(new Size(390, 660));
                content.Arrange(new Rect(0, 0, 390, 660));
                content.UpdateLayout();
                Assert.True(content.ActualHeight > 0);
                Assert.Equal("Pro", ((System.Windows.Controls.TextBlock)window.FindName("PlanText")).Text);
                Assert.NotNull(window.FindName("PlanSweep"));
                var bitmap = new RenderTargetBitmap(390, 660, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(content);
                var originalPixels = new byte[390 * 660 * 4]; bitmap.CopyPixels(originalPixels, 390 * 4, 0);
                var sweep = (TranslateTransform)window.FindName("PlanSweep");
                sweep.X = 40;
                content.UpdateLayout();
                var shimmerBitmap = new RenderTargetBitmap(390, 660, 96, 96, PixelFormats.Pbgra32); shimmerBitmap.Render(content);
                var shimmerPixels = new byte[originalPixels.Length]; shimmerBitmap.CopyPixels(shimmerPixels, 390 * 4, 0);
                Assert.False(originalPixels.SequenceEqual(shimmerPixels));
                sweep.X = -80;
                Assert.Equal(FontWeights.Light, ((System.Windows.Controls.TextBlock)window.FindName("PlanText")).FontWeight);
                content.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                window.UpdatePlanAnimation(true);
                Assert.True(window.IsPlanAnimationRunning);
                var startX = sweep.X;
                var frame = new System.Windows.Threading.DispatcherFrame();
                var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
                timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
                timer.Start();
                System.Windows.Threading.Dispatcher.PushFrame(frame);
                Assert.True(sweep.X > startX);
                window.UpdatePlanAnimation(false);
                Assert.False(window.IsPlanAnimationRunning);
                var output = Environment.GetEnvironmentVariable("CODEX_UI_PREVIEW_ROOT");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, $"usage-details-{language}.png"));
                    encoder.Save(stream);
                    var shimmerEncoder = new PngBitmapEncoder(); shimmerEncoder.Frames.Add(BitmapFrame.Create(shimmerBitmap));
                    using var shimmerStream = File.Create(Path.Combine(output, $"usage-plan-shimmer-{language}.png")); shimmerEncoder.Save(shimmerStream);
                }
                vm.TodayTokensDisplay = "482k tokens";
                vm.DailyTokenRows = FloatingBallViewModel.CreateDailyTokenRows(new long[] { 530000, 720000, 340000, 910000, 480000 }
                    .Select((count, index) => new CodexUsageAssistant.Models.DailyTokenUsage { Date = new DateOnly(2026, 9, 11 + index), Tokens = count, IsLocalEstimate = index == 4 }));
                vm.HasLocalTokenEstimate = true;
                vm.LifetimeTokensDisplay = "18.4M tokens";
                vm.StreakDisplay = language == "en" ? "12 days" : "12 天";
                var detailTabs = (System.Windows.Controls.TabControl)window.FindName("DetailsContentTabs");
                detailTabs.SelectedIndex = 1;
                content.UpdateLayout();
                Assert.Equal(1, vm.SelectedDetailsTab);
                Assert.DoesNotContain("?", window.FindResource("TokenToday").ToString()!);
                Assert.True(((FrameworkElement)window.FindName("FooterSettingsButton")).ActualWidth > 0);
                if (!string.IsNullOrWhiteSpace(output))
                {
                    var tokenBitmap = new RenderTargetBitmap(390, 660, 96, 96, PixelFormats.Pbgra32);
                    tokenBitmap.Render(content);
                    var tokenEncoder = new PngBitmapEncoder();
                    tokenEncoder.Frames.Add(BitmapFrame.Create(tokenBitmap));
                    using var tokenStream = File.Create(Path.Combine(output, $"usage-tokens-{language}.png"));
                    tokenEncoder.Save(tokenStream);
                }
                vm.CreditBalanceDisplay = "12.3456";
                vm.CreditAvailableDisplay = "Yes";
                vm.CreditUnlimitedDisplay = "No";
                detailTabs.SelectedIndex = 2;
                content.UpdateLayout();
                Assert.Equal(2, vm.SelectedDetailsTab);
                Assert.Equal(3, detailTabs.Items.Count);
                vm.NextDetailsCommand.Execute(null);
                Assert.Equal(0, vm.SelectedDetailsTab);
                vm.PreviousDetailsCommand.Execute(null);
                Assert.Equal(2, vm.SelectedDetailsTab);
                Assert.Equal(5, ((System.Windows.Controls.ItemsControl)window.FindName("TokenChart")).Items.Count);
                content.UpdateLayout();
                Assert.NotNull(window.FindResource("CreditIcon"));
                window.AnimateCarouselArrows(true);
                var previousButton = (System.Windows.Controls.Button)window.FindName("PreviousDetailsButton");
                Assert.True(previousButton.IsHitTestVisible);
                Assert.True(previousButton.HasAnimatedProperties);
                window.AnimateCarouselArrows(false);
                Assert.False(previousButton.IsHitTestVisible);
                if (!string.IsNullOrWhiteSpace(output))
                {
                    var creditBitmap = new RenderTargetBitmap(390, 660, 96, 96, PixelFormats.Pbgra32);
                    creditBitmap.Render(content);
                    var creditEncoder = new PngBitmapEncoder();
                    creditEncoder.Frames.Add(BitmapFrame.Create(creditBitmap));
                    using var creditStream = File.Create(Path.Combine(output, $"usage-credits-{language}.png"));
                    creditEncoder.Save(creditStream);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        Assert.Null(failure);
    }
}
