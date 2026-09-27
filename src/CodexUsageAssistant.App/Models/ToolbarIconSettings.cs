using System.IO;
using System.Text;
using System.Text.Json;

namespace CodexUsageAssistant.Models;

public sealed class ToolbarIconSettings
{
    public bool Enabled { get; set; } = true;
    public double Right { get; set; } = 200;
    public double Top { get; set; } = 62;

    public static ToolbarIconSettings Load(string path)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<ToolbarIconSettings>(File.ReadAllText(path, Encoding.UTF8)) ?? new();
            settings.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    public void Normalize()
    {
        Right = double.IsFinite(Right) ? Math.Clamp(Right, 0, 10000) : 200;
        Top = double.IsFinite(Top) ? Math.Clamp(Top, 0, 10000) : 62;
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
    internal static ToolbarIconBounds Calculate(int left, int top, int width, int height, double dpiScale, ToolbarIconSettings settings)
    {
        settings.Normalize();
        var scale = double.IsFinite(dpiScale) && dpiScale > 0 ? dpiScale : 1;
        var size = Math.Max(1, (int)Math.Round(32 * scale));
        var x = Math.Clamp(width - size - (int)Math.Round(settings.Right * scale), 0, Math.Max(0, width - size));
        var y = Math.Clamp((int)Math.Round(settings.Top * scale), 0, Math.Max(0, height - size));
        return new(left + x, top + y, size);
    }
}
