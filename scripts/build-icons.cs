#:property TargetFramework=net10.0-windows
#:property TreatWarningsAsErrors=false
#:package System.Drawing.Common

// Builds the app icon and UI images from assets/branding/severino-rosto.png.
//
//   dotnet run scripts/build-icons.cs [preview.png]
//
// Writes src/Severino.App/Assets/severino.ico (16 to 256 px) and severino-512.png. With an
// argument, also writes a sheet showing the small sizes enlarged, on light and dark.

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

var root = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", ".."));
var source = Path.Combine(root, "assets", "branding", "severino-rosto.png");
var assets = Path.Combine(root, "src", "Severino.App", "Assets");
Directory.CreateDirectory(assets);

using var original = new Bitmap(source);
SnapNearOpaque(original);
using var square = TrimToSquare(original, marginRatio: 0.02);
Console.WriteLine($"source {original.Width}x{original.Height}, content {square.Width}x{square.Height}");

int[] iconSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
var frames = iconSizes.Select(size => (size, image: Resize(square, size))).ToList();
WriteIco(Path.Combine(assets, "severino.ico"), frames);
using (var large = Resize(square, 512))
    large.Save(Path.Combine(assets, "severino-512.png"), ImageFormat.Png);
Console.WriteLine($"wrote {assets}\\severino.ico and severino-512.png");

if (args.Length > 0)
    WritePreview(args[0], frames.Where(f => f.size <= 48).ToList());

foreach (var (_, image) in frames)
    image.Dispose();

// The generated artwork leaves its "opaque" fill at alpha 247-250, which lets the taskbar
// show through. Anything that close to opaque becomes fully opaque; edges stay soft.
static void SnapNearOpaque(Bitmap image)
{
    const int threshold = 235;
    var rect = new Rectangle(0, 0, image.Width, image.Height);
    var data = image.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
    var pixels = new int[image.Width * image.Height];
    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
    for (var i = 0; i < pixels.Length; i++)
        if ((pixels[i] >>> 24) >= threshold)
            pixels[i] |= unchecked((int)0xFF000000);
    System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
    image.UnlockBits(data);
}

// Crops to the non-transparent content and centres it on a transparent square.
static Bitmap TrimToSquare(Bitmap image, double marginRatio)
{
    int minX = image.Width, minY = image.Height, maxX = 0, maxY = 0;
    var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
    var pixels = new int[image.Width * image.Height];
    System.Runtime.InteropServices.Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
    image.UnlockBits(data);
    for (var y = 0; y < image.Height; y++)
        for (var x = 0; x < image.Width; x++)
            if ((pixels[y * image.Width + x] >>> 24) > 16)
            {
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }

    var contentWidth = maxX - minX + 1;
    var contentHeight = maxY - minY + 1;
    var side = (int)(Math.Max(contentWidth, contentHeight) * (1 + 2 * marginRatio));
    var result = new Bitmap(side, side, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(result);
    g.DrawImage(image,
        new Rectangle((side - contentWidth) / 2, (side - contentHeight) / 2, contentWidth, contentHeight),
        new Rectangle(minX, minY, contentWidth, contentHeight), GraphicsUnit.Pixel);
    return result;
}

// Halves repeatedly before the final step: one big bicubic jump from 1200 px to 16 px aliases.
static Bitmap Resize(Bitmap image, int size)
{
    Bitmap current = image;
    while (current.Width / 2 >= size * 2)
    {
        var half = Draw(current, current.Width / 2);
        if (!ReferenceEquals(current, image))
            current.Dispose();
        current = half;
    }
    var result = Draw(current, size);
    if (!ReferenceEquals(current, image))
        current.Dispose();
    return result;

    static Bitmap Draw(Bitmap from, int side)
    {
        var bitmap = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        using var attributes = new ImageAttributes();
        attributes.SetWrapMode(WrapMode.TileFlipXY); // no dark fringe at the edges
        g.DrawImage(from, new Rectangle(0, 0, side, side), 0, 0, from.Width, from.Height, GraphicsUnit.Pixel, attributes);
        return bitmap;
    }
}

// ICO with PNG frames: supported by Windows since Vista for every size.
static void WriteIco(string path, IReadOnlyList<(int size, Bitmap image)> frames)
{
    var payloads = frames.Select(f =>
    {
        using var stream = new MemoryStream();
        f.image.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }).ToList();

    using var file = File.Create(path);
    using var writer = new BinaryWriter(file);
    writer.Write((short)0);            // reserved
    writer.Write((short)1);            // type: icon
    writer.Write((short)frames.Count);
    var offset = 6 + 16 * frames.Count;
    for (var i = 0; i < frames.Count; i++)
    {
        var size = frames[i].size;
        writer.Write((byte)(size >= 256 ? 0 : size)); // 0 means 256
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);         // palette
        writer.Write((byte)0);         // reserved
        writer.Write((short)1);        // planes
        writer.Write((short)32);       // bits per pixel
        writer.Write(payloads[i].Length);
        writer.Write(offset);
        offset += payloads[i].Length;
    }
    foreach (var payload in payloads)
        writer.Write(payload);
}

static void WritePreview(string path, IReadOnlyList<(int size, Bitmap image)> frames)
{
    const int zoom = 8, gap = 24;
    var width = frames.Sum(f => f.size * zoom + gap) + gap;
    var height = 2 * (frames.Max(f => f.size) * zoom + 2 * gap);
    using var sheet = new Bitmap(width, height);
    using var g = Graphics.FromImage(sheet);
    g.Clear(Color.White);
    using (var dark = new SolidBrush(Color.FromArgb(32, 32, 32)))
        g.FillRectangle(dark, 0, height / 2, width, height / 2);
    g.InterpolationMode = InterpolationMode.NearestNeighbor;
    g.PixelOffsetMode = PixelOffsetMode.Half;
    var x = gap;
    foreach (var (size, image) in frames)
    {
        g.DrawImage(image, x, gap, size * zoom, size * zoom);
        g.DrawImage(image, x, height / 2 + gap, size * zoom, size * zoom);
        x += size * zoom + gap;
    }
    sheet.Save(path, ImageFormat.Png);
    Console.WriteLine($"preview {path}");
}
