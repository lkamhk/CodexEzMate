using System.Drawing;

namespace CodexUsageAssistant.Services;

public static class ScreenCoordinateValidator
{
    public static bool IsInside(int x, int y, Rectangle bounds) =>
        x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom;
}
