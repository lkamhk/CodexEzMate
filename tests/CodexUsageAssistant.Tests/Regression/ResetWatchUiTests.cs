using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using CodexUsageAssistant.ViewModels;
using CodexUsageAssistant.Views;
using Xunit;
using static CodexUsageAssistant.Tests.Regression.ResetWatchTestData;

namespace CodexUsageAssistant.Tests.Regression;

public sealed class ResetWatchUiTests
{
    [Theory]
    [InlineData("en", AppLanguage.English)]
    [InlineData("zh-Hant", AppLanguage.TraditionalChinese)]
    [InlineData("zh-Hans", AppLanguage.SimplifiedChinese)]
    public void CompactActivityKeepsOneActionAndRendersWithoutSideEffects(string resource, AppLanguage language)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            ResetWatchWindow? window = null;
            try
            {
                var feed = PresentationFeed(DateTimeOffset.UtcNow);
                var store = new Store { State = Baseline(feed) };
                var client = new Client();
                using var watch = new ResetWatchService(client, store);
                watch.InitializeAsync().GetAwaiter().GetResult();
                var refreshes = 0; var notices = 0;
                watch.AnnouncementsAvailable += _ => notices++;
                watch.UsageRefreshRequested += () => refreshes++;
                window = new ResetWatchWindow(watch, language);
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri($"pack://application:,,,/CodexEzMate;component/Resources/Strings.{resource}.xaml") });
                var model = (ResetWatchViewModel)window.DataContext;
                Assert.True(model.Enabled); Assert.True(model.CanCheck); Assert.False(model.NotifyCommunity);
                Assert.True(model.NotifyAccountChanges);
                Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                Assert.Null(window.Owner);
                var row = Assert.Single(model.Events);
                Assert.Equal("Banked Reset", row.Title);
                Assert.Empty(row.Summary); Assert.Empty(row.Timing); Assert.Empty(row.Scope); Assert.Empty(row.Warning);
                Assert.False(row.HasDetails);
                Assert.Equal("codex-resets.com", Assert.Single(row.Links).Label);
                Assert.Null(typeof(ResetWatchViewModel).GetProperty("ShowSampleCommand"));
                Assert.Null(typeof(ResetWatchViewModel).GetProperty("ShowLiveCommand"));
                Assert.Null(typeof(ResetWatchViewModel).GetProperty("RefreshAccountCommand"));
                Assert.Null(typeof(ResetWatchViewModel).GetProperty("FetchStatus"));
                Assert.Equal(0, refreshes); Assert.Equal(0, notices);
                Assert.Equal(0, client.Calls); Assert.Equal(0, store.Writes);
                Assert.Equal(Bytes(feed), Bytes(watch.State.Feed!));
                // Capture the real background in a zero-margin root; the window content itself has margins.
                var content = (FrameworkElement)window.Content;
                window.Content = null;
                var panel = new Border { Child = content, Background = window.Background };
                window.Content = panel;
                Layout(panel, 780, 610);
                Assert.Null(window.FindName("SampleButton"));
                Assert.Single(Descendants<Button>((Grid)window.FindName("ActivityToolbar")));
                Assert.True(((Button)window.FindName("RefreshFeedButton")).ActualWidth > 0);
                Assert.Single(((ItemsControl)window.FindName("EventList")).Items);
                Assert.Equal(2, ((TabControl)window.FindName("ResetTabs")).Items.Count);
                foreach (var tab in ((TabControl)window.FindName("ResetTabs")).Items.Cast<TabItem>())
                {
                    var header = (ContentPresenter)tab.Template.FindName("HeaderPresenter", tab);
                    var text = new FormattedText(tab.Header.ToString()!, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(tab.FontFamily, tab.FontStyle, tab.IsSelected ? FontWeights.SemiBold : tab.FontWeight, tab.FontStretch),
                        tab.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(tab).PixelsPerDip);
                    Assert.True(header.ActualWidth + 1 >= text.WidthIncludingTrailingWhitespace, $"Clipped tab header: {resource} / {tab.Header}");
                }
                var mainText = string.Join("\n", Descendants<TextBlock>(panel).Select(x => x.Text));
                Assert.DoesNotContain("Last attempt:", mainText);
                Assert.DoesNotContain("Last download:", mainText);
                Assert.DoesNotContain("Expected:", mainText);
                Assert.DoesNotContain("Scope: Unknown", mainText);
                foreach (var key in new[] { "ResetWatchTitle", "ResetWatchHeading", "ResetWatchIntro", "ResetWatchActivity", "ResetWatchSettings",
                    "ResetWatchCheck", "ResetWatchEmpty", "ResetWatchEmptyTitle", "ResetWatchEvidenceHint", "ResetWatchDetails", "ResetWatchPrivacyHeading",
                    "ResetWatchEnable", "ResetWatchUrl", "ResetWatchTrust", "ResetWatchInterval", "ResetWatchNotices", "ResetWatchPreviews",
                    "ResetWatchAnnouncements", "ResetWatchCommunity", "ResetWatchAccountNotices", "ResetWatchPrivacy", "ResetWatchSave", "ResetWatchAccountHint" })
                    Assert.False(string.IsNullOrWhiteSpace(window.TryFindResource(key)?.ToString()), key);
                Capture(panel, "activity-" + resource);
                Layout(panel, 700, 490);
                Assert.True(((Button)window.FindName("RefreshFeedButton")).ActualWidth > 0);
                Capture(panel, "activity-min-" + resource);
                ((TabItem)window.FindName("FeedSettingsTab")).IsSelected = true;
                Layout(panel, 780, 610);
                Capture(panel, "settings-default-" + resource);
                // Also check the supported minimum window width rather than only the default width.
                Layout(panel, 700, 490);
                Assert.True(((TextBox)window.FindName("FeedUrlBox")).ActualWidth > 300);
                Capture(panel, "settings-" + resource);
                var settingsScroll = (ScrollViewer)window.FindName("FeedSettingsScroll");
                settingsScroll.ScrollToEnd(); Layout(panel, 700, 490);
                var save = (Button)window.FindName("SaveFeedButton");
                Assert.True(save.ActualWidth > 50);
                var saveBottom = save.TransformToAncestor(panel).Transform(new Point(0, save.ActualHeight));
                Assert.InRange(saveBottom.Y, 0, panel.ActualHeight);
                Assert.Equal(0, refreshes); Assert.Equal(0, client.Calls); Assert.Equal(0, store.Writes);
                if (language == AppLanguage.English) Assert.Equal("Source healthy", model.Status);
            }
            catch (Exception error) { failure = error; }
            finally { window?.Close(); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF layout timed out");
        Assert.Null(failure);
    }

    [Theory]
    [InlineData("disabled", "neutral")]
    [InlineData("stale", "warning")]
    [InlineData("unavailable", "warning")]
    [InlineData("partial", "warning")]
    [InlineData("healthz", "error")]
    [InlineData("offline", "error")]
    public void CompactStatusRetainsFailures(string scenario, string tone)
    {
        OnSta(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var feed = PresentationFeed(now);
            var settings = Settings;
            if (scenario == "disabled") settings = settings with { Enabled = false };
            if (scenario == "stale") feed = feed with { GeneratedAt = now.AddHours(-2), Sources = [feed.Sources[0] with { LastSuccessfulCheckAt = now.AddHours(-2) }] };
            if (scenario == "unavailable") feed = feed with { Sources = [feed.Sources[0] with { Health = ResetSourceHealth.Unavailable }] };
            if (scenario == "partial") feed = feed with { Sources = [feed.Sources[0], new() { Id = "other", Name = "Other", Health = ResetSourceHealth.Degraded }] };
            if (scenario == "healthz") settings = settings with { FeedUrl = "https://example.com/healthz" };
            var store = new Store { Settings = settings, State = Baseline(feed) with { FeedUrl = settings.FeedUrl } };
            var client = new Client { Response = new(feed) };
            using var watch = new ResetWatchService(client, store);
            watch.InitializeAsync().GetAwaiter().GetResult();
            if (scenario is "healthz" or "offline")
            {
                client.Fetch = _ => throw scenario switch
                { "healthz" => new InvalidDataException("Not a feed"), _ => new System.Net.Http.HttpRequestException("Offline") };
                watch.RefreshAsync().GetAwaiter().GetResult();
            }
            var window = new ResetWatchWindow(watch, AppLanguage.English);
            try
            {
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/CodexEzMate;component/Resources/Strings.en.xaml") });
                var model = (ResetWatchViewModel)window.DataContext;
                Assert.Equal(tone, model.StatusTone);
                Assert.DoesNotContain("Source healthy", model.Status);
                Assert.Single(model.Events);
                if (scenario == "healthz") Assert.Contains("not /healthz", model.StatusDetail);
                if (scenario is "stale" or "unavailable") Assert.NotEmpty(model.Events[0].Warning);
                if (scenario == "offline") Assert.Contains("Previous data retained", model.StatusDetail);
                Assert.Equal(Bytes(feed), Bytes(watch.State.Feed!));
                if (scenario == "unavailable")
                {
                    var content = (FrameworkElement)window.Content; window.Content = null;
                    var panel = new Border { Child = content, Background = window.Background };
                    window.Content = panel; Layout(panel, 700, 490); Capture(panel, "source-unavailable-en");
                }
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void LongCorrectionAndRetractionRemainReadableAtMinimumWidth()
    {
        OnSta(() =>
        {
            var now = DateTimeOffset.UtcNow;
            var feed = PresentationFeed(now);
            var original = feed.Events[0];
            feed = feed with { Events = [original with { Id = "correction", Title = "Eligibility update", Status = ResetAnnouncementStatus.Corrected,
                Revision = 2, UpdatedAt = now, Summary = string.Concat(Enumerable.Repeat("This update applies only to eligible accounts. ", 12)),
                Scope = "Existing paid plans only." }, original with { Id = "withdrawn", Title = "Earlier announcement withdrawn",
                Status = ResetAnnouncementStatus.Retracted, Revision = 3, UpdatedAt = now, Summary = "The previous announcement was incorrect. No Reset is confirmed." }] };
            using var watch = new ResetWatchService(new Client(), new Store { State = Baseline(feed) });
            watch.InitializeAsync().GetAwaiter().GetResult();
            var window = new ResetWatchWindow(watch, AppLanguage.English);
            try
            {
                window.Resources.MergedDictionaries.Add(new ResourceDictionary
                { Source = new Uri("pack://application:,,,/CodexEzMate;component/Resources/Strings.en.xaml") });
                var model = (ResetWatchViewModel)window.DataContext;
                Assert.Contains("Existing paid plans only", model.Events[0].Scope);
                Assert.Contains(feed.Events[0].Summary, model.Events[0].Details);
                Assert.Equal("Retracted", model.Events[1].Status);
                Assert.Equal(feed.Events[1].Summary, model.Events[1].Summary);
                model.Events[0].DetailsExpanded = true;
                var content = (FrameworkElement)window.Content; window.Content = null;
                var panel = new Border { Child = content, Background = window.Background }; window.Content = panel;
                Layout(panel, 700, 490);
                var details = Descendants<Expander>(panel).First(x => x.DataContext is ResetEventRow);
                Assert.True(details.IsExpanded);
                Assert.True(details.ActualWidth > 300);
                Capture(panel, "correction-details-en");
            }
            finally { window.Close(); }
        });
    }

    internal static ResetFeed PresentationFeed(DateTimeOffset now)
    {
        var published = now.AddDays(-1);
        return new()
        {
            SchemaVersion = 1, FeedId = "layout-fixture", GeneratedAt = now,
            Sources = [new() { Id = "codex-resets-com", Name = "codex-resets.com", Health = ResetSourceHealth.Healthy, LastSuccessfulCheckAt = now }],
            Events = [new() { Id = "codex-resets-banked-layout", Revision = 1, Title = "Codex Banked reset", Status = ResetAnnouncementStatus.Announced,
                Evidence = ResetEvidence.Community, Impacts = [ResetImpact.Banked], PublishedAt = published, UpdatedAt = published,
                Summary = "codex-resets.com reports a Banked reset. This third-party report does not confirm receipt by your account.",
                TimeDescription = "Reset time reported by the tracker: " + published.ToString("O"),
                Links = [new() { SourceId = "codex-resets-com", Label = "codex-resets.com", Url = "https://codex-resets.com/" }] }]
        };
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }

    private static void Layout(FrameworkElement panel, double width, double height)
    { panel.Measure(new Size(width, height)); panel.Arrange(new Rect(0, 0, width, height)); panel.UpdateLayout(); }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        { try { action(); } catch (Exception ex) { failure = ex; } finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF layout timed out"); Assert.Null(failure);
    }

    private static void Capture(FrameworkElement panel, string name)
    {
        var output = Environment.GetEnvironmentVariable("CODEX_RESET_UI_PREVIEW_ROOT");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var bitmap = new RenderTargetBitmap((int)panel.ActualWidth, (int)panel.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(panel);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png"));
        encoder.Save(stream);
    }
}
