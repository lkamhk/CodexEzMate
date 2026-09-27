using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CodexUsageAssistant.Tests;

internal static class DocumentationScreenshot
{
    internal static RenderTargetBitmap Capture(Window window, int width, int height)
    {
        // Render a zero-margin root so the content's own margins cannot crop the image.
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        var canvas = new Border { Background = window.Background, Child = content, Resources = window.Resources };
        window.Content = canvas;
        canvas.Measure(new Size(width, height));
        canvas.Arrange(new Rect(0, 0, width, height)); canvas.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(canvas);
        return bitmap;
    }
}
