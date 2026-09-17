using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: IconGenerator <output.ico>");
    return 1;
}

var outputPath = Path.GetFullPath(args[0]);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var images = sizes.Select(RenderPng).ToArray();

using (var output = File.Create(outputPath))
using (var writer = new BinaryWriter(output))
{
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)images.Length);
    var offset = 6 + images.Length * 16;
    for (var index = 0; index < images.Length; index++)
    {
        var size = sizes[index];
        writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(images[index].Length);
        writer.Write(offset);
        offset += images[index].Length;
    }
    foreach (var image in images) writer.Write(image);
}

File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(outputPath)!, "CodexUsageAssistant-preview.png"), images[^1]);
return 0;

static byte[] RenderPng(int size)
{
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.Transparent);
    graphics.SmoothingMode = SmoothingMode.AntiAlias;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

    var inset = Math.Max(1f, size * 0.035f);
    var bounds = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);
    using var background = RoundedRectangle(bounds, size * 0.22f);
    using var backgroundBrush = new SolidBrush(Color.FromArgb(255, 23, 32, 51));
    graphics.FillPath(backgroundBrush, background);

    var arcInset = size * 0.22f;
    var arcBounds = new RectangleF(arcInset, arcInset, size - arcInset * 2, size - arcInset * 2);
    using var arcPen = new Pen(Color.FromArgb(255, 147, 197, 253), Math.Max(2f, size * 0.12f))
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round
    };
    graphics.DrawArc(arcPen, arcBounds, 42, 276);

    var dotSize = Math.Max(2.5f, size * 0.125f);
    var dotCenterX = size * 0.735f;
    var dotCenterY = size * 0.775f;
    using var dotBrush = new SolidBrush(Color.FromArgb(255, 34, 197, 94));
    graphics.FillEllipse(dotBrush, dotCenterX - dotSize / 2, dotCenterY - dotSize / 2, dotSize, dotSize);

    using var stream = new MemoryStream();
    bitmap.Save(stream, ImageFormat.Png);
    return stream.ToArray();
}

static GraphicsPath RoundedRectangle(RectangleF bounds, float radius)
{
    var diameter = radius * 2;
    var path = new GraphicsPath();
    path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
    path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
    path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
    path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
    path.CloseFigure();
    return path;
}
