using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CodexUsageAssistant.Models;
using CodexUsageAssistant.Services;
using CodexUsageAssistant.Views;
using Xunit;

namespace CodexUsageAssistant.Tests;

public sealed class HotkeyTests
{
    [Fact]
    public void RegistrationFailure_KeepsOriginalAndReleasesPartialChanges()
    {
        var live = new Dictionary<int, uint>();
        using var service = new GlobalHotkeyService((id, mods, key) =>
        {
            if (key == 0x43) return false;
            live.Add(id, key); return true;
        }, id => live.Remove(id));
        Assert.True(service.TryApply(true, [new(HotkeyAction.SessionBrowser, 3, 0x41)], out _));
        Assert.False(service.TryApply(true, [new(HotkeyAction.GoalMonitor, 3, 0x42), new(HotkeyAction.ServerHost, 3, 0x43)], out var error));
        Assert.NotEmpty(error);
        Assert.Equal(0x41u, Assert.Single(live).Value);
    }

    [Fact]
    public void SwappingActions_ReusesRegistrations_DisablingAndDisposeReleaseThem()
    {
        var live = new HashSet<int>(); var calls = 0;
        using var service = new GlobalHotkeyService((id, mods, key) => { calls++; return live.Add(id); }, id => live.Remove(id));
        Assert.True(service.TryApply(true, [new(HotkeyAction.SessionBrowser, 3, 0x41), new(HotkeyAction.GoalMonitor, 3, 0x42)], out _));
        Assert.True(service.TryApply(true, [new(HotkeyAction.SessionBrowser, 3, 0x42), new(HotkeyAction.GoalMonitor, 3, 0x41)], out _));
        Assert.Equal(2, calls);
        Assert.True(service.TryApply(false, [new(HotkeyAction.SessionBrowser, 3, 0x42)], out _));
        Assert.Empty(live);
        Assert.True(service.TryApply(true, [new(HotkeyAction.SessionBrowser, 3, 0x42)], out _));
        service.Dispose(); Assert.Empty(live);
    }

    [Theory]
    [InlineData(0, 65)]
    [InlineData(4, 65)]
    [InlineData(3, 123)]
    [InlineData(3, 17)]
    [InlineData(32, 65)]
    public void InvalidOrReservedKey_DoesNotRegister(uint modifiers, uint key)
    {
        using var service = new GlobalHotkeyService((id, mods, value) => throw new Exception("Unexpected registration"), _ => { });
        Assert.False(service.TryApply(true, [new(HotkeyAction.Settings, modifiers, key)], out _));
    }

    [Fact]
    public void DuplicateChordsOrActions_AreRejectedBeforeRegistration()
    {
        using var service = new GlobalHotkeyService((id, mods, key) => throw new Exception("Unexpected registration"), _ => { });
        Assert.False(service.TryApply(true, [new(HotkeyAction.Settings, 3, 65), new(HotkeyAction.Updates, 3, 65)], out _));
        Assert.False(service.TryApply(true, [new(HotkeyAction.Settings, 3, 65), new(HotkeyAction.Settings, 3, 66)], out _));
    }

    [Fact]
    public async Task SettingsRoundTrip_PreservesDisabledBindingsAndOtherSettings()
    {
        var path = Path.Combine(Path.GetTempPath(), "ezmate-hotkeys-" + Guid.NewGuid() + ".json");
        try
        {
            var service = new JsonSettingsService(path);
            await service.SaveAsync(new WindowPosition { HostPort = 4567, Language = AppLanguage.English,
                HotkeysEnabled = false, Hotkeys = [new(HotkeyAction.GoalMonitor, 3, 0x47)] }, CancellationToken.None);
            var actual = await new JsonSettingsService(path).LoadAsync(CancellationToken.None);
            Assert.NotNull(actual); Assert.False(actual.HotkeysEnabled); Assert.Equal(4567, actual.HostPort);
            Assert.Equal(AppLanguage.English, actual.Language);
            Assert.Equal(new HotkeyBinding(HotkeyAction.GoalMonitor, 3, 0x47), Assert.Single(actual.Hotkeys));
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Editor_RendersEveryActionAndSaveButton()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var service = new GlobalHotkeyService((_, _, _) => true, _ => { });
                var window = new HotkeySettingsWindow(null!, service);
                var content = (ScrollViewer)window.Content;
                content.Measure(new Size(700, 550)); content.Arrange(new Rect(0, 0, 700, 550)); content.UpdateLayout();
                var panel = (StackPanel)content.Content;
                Assert.Equal(7, panel.Children.OfType<StackPanel>().Count(p => p.Children.OfType<ComboBox>().Any()));
                Assert.Equal(2, panel.Children.OfType<StackPanel>().Last().Children.OfType<Button>().Count());
                var output = Environment.GetEnvironmentVariable("CODEX_UI_PREVIEW_ROOT");
                if (!string.IsNullOrWhiteSpace(output))
                {
                    Directory.CreateDirectory(output);
                    var bitmap = new RenderTargetBitmap(700, 550, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var stream = File.Create(Path.Combine(output, "hotkey-settings.png")); encoder.Save(stream);
                }
                window.Close();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
        if (failure is not null) throw failure;
    }
}
