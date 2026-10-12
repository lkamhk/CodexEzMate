using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexUsageAssistant.Models;

public sealed class ToolbarIconSettings
{
    public bool Enabled { get; set; } = true;
    public bool FixedMode { get; set; } = true;
    public int PlacementVersion { get; set; } = 2;
    public double Right { get; set; } = 160;
    public double Top { get; set; } = 2;
    public void ResetPosition() { Right = 160; Top = 2; PlacementVersion = 2; }

    public static ToolbarIconSettings Load(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var settings = JsonSerializer.Deserialize<ToolbarIconSettings>(json) ?? new();
            using var document = JsonDocument.Parse(json);
            // Old offsets used the client area; migrate once to the visible title-bar frame.
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty(nameof(PlacementVersion), out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var value) || value < 2)
                settings.ResetPosition();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Normalize()
    {
        Right = double.IsFinite(Right) ? Math.Clamp(Right, 0, 10000) : 160;
        Top = double.IsFinite(Top) ? Math.Clamp(Top, 0, 10000) : 2;
    }

    public void Save(string path)
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}

internal readonly record struct ToolbarIconBounds(int X, int Y, int Size);

internal static class ToolbarIconPlacement
{
    internal static ToolbarIconBounds CalculateFixed(int left, int top, int width, int height, double dpiScale)
    {
        var scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        var size = (int)Math.Round(32 * scale);
        // Coordinates are relative to the client origin, never the maximized outer frame.
        // Match the sidebar header's 32-DIP buttons and their 40-DIP spacing.
        var x = (int)Math.Round(300 * scale);
        var y = (int)Math.Round(68 * scale);
        return new(left + Math.Clamp(x, 0, Math.Max(0, width - size)),
            top + Math.Clamp(y, 0, Math.Max(0, height - size)), size);
    }

    internal static ToolbarIconBounds Calculate(int left, int top, int width, int height, double dpiScale, ToolbarIconSettings settings)
    {
        settings.Normalize();
        var scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        var size = Math.Max(1, (int)Math.Round(28 * scale));
        var x = Math.Clamp(width - size - (int)Math.Round(settings.Right * scale), 0, Math.Max(0, width - size));
        var y = Math.Clamp((int)Math.Round(settings.Top * scale), 0, Math.Max(0, height - size));
        return new(left + x, top + y, size);
    }
}
