using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CodexUsageAssistant.Services;

internal static class TrayUsageIconRenderer
{
    internal static string SelectLabel(string? fiveHour, string? weekly)
    {
        var primary = Label(fiveHour);
        return primary != "—" ? primary : Label(weekly);
    }
    internal static string Label(string? percentage) =>
        double.TryParse(percentage?.Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? Math.Clamp(Math.Round(number), 0, 100).ToString("0", CultureInfo.InvariantCulture) : "—";

    internal static Bitmap Render(string label)
    {
        var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);
        var low = int.TryParse(label, out var amount) && amount <= 15;
        using var fill = new SolidBrush(low ? Color.FromArgb(180, 73, 50) : Color.FromArgb(38, 103, 131));
        graphics.FillRectangle(fill, 0, 0, 32, 32);
        using var brush = new SolidBrush(Color.White);
        using var family = new FontFamily("Segoe UI");
        using var glyph = new GraphicsPath();
        glyph.AddString(label, family, (int)FontStyle.Bold, 32, PointF.Empty, StringFormat.GenericTypographic);
        var bounds = glyph.GetBounds();
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            // Fit actual glyph outlines, avoiding the padding in DrawString's layout box.
            var scaleX = Math.Min(30 / bounds.Width, 27 / bounds.Height);
            var scaleY = label.Length >= 3 ? 27 / bounds.Height : scaleX;
            using var transform = new Matrix(scaleX, 0, 0, scaleY,
                (32 - bounds.Width * scaleX) / 2 - bounds.X * scaleX,
                (32 - bounds.Height * scaleY) / 2 - bounds.Y * scaleY);
            glyph.Transform(transform);
            graphics.FillPath(brush, glyph);
        }
        return bitmap;
    }

    internal static Icon Create(string label)
    {
        using var bitmap = Render(label);
        var handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
