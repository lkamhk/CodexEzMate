using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length is < 1 or > 3)
{
    Console.Error.WriteLine("Usage: IconGenerator <output.ico> [app-icon.png] [large-logo.png]");
    return 1;
}
var outputPath = Path.GetFullPath(args[0]);
var root = new DirectoryInfo(Path.GetDirectoryName(outputPath)!);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "CodexUsageAssistant.sln"))) root = root.Parent;
if (root is null && args.Length < 3) throw new InvalidOperationException("Specify both source PNG paths outside the project.");
var iconPath = args.Length > 1 ? Path.GetFullPath(args[1]) : Path.Combine(root!.FullName, "assets", "branding", "codex-ezmate-app-icon.png");
var logoPath = args.Length > 2 ? Path.GetFullPath(args[2]) : Path.Combine(root!.FullName, "assets", "branding", "codex-ezmate-logo-mate.png");
using var iconSource = new Bitmap(iconPath);
using var logoSource = new Bitmap(logoPath);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
var images = sizes.Select(size => RenderPng(iconSource, size)).ToArray();
using (var output = File.Create(outputPath))
using (var writer = new BinaryWriter(output))
{
    writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)images.Length);
    var offset = 6 + images.Length * 16;
    for (var index = 0; index < images.Length; index++)
    {
        var size = sizes[index];
        writer.Write((byte)(size == 256 ? 0 : size)); writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)0); writer.Write((byte)0);
        writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(images[index].Length); writer.Write(offset); offset += images[index].Length;
    }
    foreach (var image in images) writer.Write(image);
}
File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(outputPath)!, "CodexUsageAssistant-preview.png"), RenderPng(logoSource, 256));
Console.WriteLine("Created ten icon sizes and the mate logo preview.");
return 0;

static byte[] RenderPng(Bitmap source, int size)
{
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.Transparent);
    graphics.CompositingMode = CompositingMode.SourceCopy;
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    using var attributes = new ImageAttributes();
    attributes.SetWrapMode(WrapMode.TileFlipXY);
    graphics.DrawImage(source, new Rectangle(0, 0, size, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
    using var stream = new MemoryStream(); bitmap.Save(stream, ImageFormat.Png); return stream.ToArray();
}
