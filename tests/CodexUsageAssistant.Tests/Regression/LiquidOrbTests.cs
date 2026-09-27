using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexUsageAssistant.Views;
using Xunit;

namespace CodexUsageAssistant.Tests.Regression;

public sealed class LiquidOrbTests
{
    [Fact]
    public void WavesChangeAndStopWhenHiddenEmptyOrClosed()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var orb = new LiquidQuotaOrb { Value = 50, Width = 72, Height = 72 };
            var window = new Window { Content = orb, Width = 80, Height = 80, Left = -32000, Top = -32000,
                ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
            try
            {
                window.Show(); window.UpdateLayout();
                Assert.True(orb.IsAnimating);
                byte[] Pixels()
                {
                    var image = new RenderTargetBitmap(72, 72, 96, 96, PixelFormats.Pbgra32); image.Render(orb);
                    var bytes = new byte[72 * 72 * 4]; image.CopyPixels(bytes, 72 * 4, 0); return bytes;
                }
                var first = Pixels();
                var frame = new DispatcherFrame();
                var stop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                stop.Tick += (_, _) => { stop.Stop(); frame.Continue = false; };
                stop.Start(); Dispatcher.PushFrame(frame);
                Assert.False(first.SequenceEqual(Pixels()));
                window.Hide(); Assert.False(orb.IsAnimating);
                window.Show(); Assert.True(orb.IsAnimating);
                orb.Value = 0; Assert.False(orb.IsAnimating);
                orb.Value = 100; Assert.False(orb.IsAnimating);
                orb.Value = double.NaN; Assert.False(orb.IsAnimating);
                orb.Value = 25; Assert.True(orb.IsAnimating);
                window.Close(); Assert.False(orb.IsAnimating);
            }
            catch (Exception ex) { failure = ex; }
            finally { window.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
    }
}
