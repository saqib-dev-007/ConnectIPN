using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// Generates every ConnectIPN icon from Icon/Icon.png.
//
// The source is a 512x512 RGBA image whose background is already transparent.
// Earlier versions painted the corners with the artwork colour; this version keeps
// the alpha channel intact so the Windows .ico, the Android mipmaps and the setup
// program all get a genuinely transparent icon.

string root = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
string sourcePath = Path.Combine(root, "Icon", "Icon.png");

var frame = BitmapFrame.Create(new Uri(sourcePath, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
int sw = converted.PixelWidth;
int sh = converted.PixelHeight;
var source = new byte[sw * sh * 4];
converted.CopyPixels(source, sw * 4, 0);
Console.WriteLine($"source {sw}x{sh}");

int Alpha(int x, int y) => source[(y * sw + x) * 4 + 3];

// --- artwork bounds: everything with a meaningful alpha value ---
int minX = sw, minY = sh, maxX = -1, maxY = -1;
for (int y = 0; y < sh; y++)
{
    for (int x = 0; x < sw; x++)
    {
        if (Alpha(x, y) < 8) continue;
        if (x < minX) minX = x;
        if (x > maxX) maxX = x;
        if (y < minY) minY = y;
        if (y > maxY) maxY = y;
    }
}
if (maxX < 0)
{
    minX = 0; minY = 0; maxX = sw - 1; maxY = sh - 1;
}
int aw = maxX - minX + 1;
int ah = maxY - minY + 1;
Console.WriteLine($"artwork {aw}x{ah} at {minX},{minY}");

var art = new byte[aw * ah * 4];
for (int y = 0; y < ah; y++)
{
    Buffer.BlockCopy(source, ((minY + y) * sw + minX) * 4, art, y * aw * 4, aw * 4);
}

// --- background colour: average of four opaque patches just inside the artwork ---
long sb = 0, sg = 0, sr = 0;
int samples = 0;
foreach ((double fx, double fy) in new[] { (0.10, 0.10), (0.90, 0.10), (0.10, 0.90), (0.90, 0.90) })
{
    int px = (int)(fx * aw), py = (int)(fy * ah);
    for (int y = py - 3; y <= py + 3; y++)
    {
        for (int x = px - 3; x <= px + 3; x++)
        {
            if (x < 0 || y < 0 || x >= aw || y >= ah) continue;
            int i = (y * aw + x) * 4;
            if (art[i + 3] < 200) continue;
            sb += art[i];
            sg += art[i + 1];
            sr += art[i + 2];
            samples++;
        }
    }
}
Color background = samples > 0
    ? Color.FromRgb((byte)(sr / samples), (byte)(sg / samples), (byte)(sb / samples))
    : Color.FromRgb(0x2E, 0x3B, 0x44);
Console.WriteLine($"background #{background.R:X2}{background.G:X2}{background.B:X2}");

double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

// Alpha-weighted bilinear sample, so transparent pixels never darken the edges.
(double B, double G, double R, double A) Sample(double sx, double sy)
{
    sx = Clamp(sx, 0, aw - 1);
    sy = Clamp(sy, 0, ah - 1);
    int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
    int x1 = Math.Min(x0 + 1, aw - 1), y1 = Math.Min(y0 + 1, ah - 1);
    double fx = sx - x0, fy = sy - y0;
    double a = 0, b = 0, g = 0, r = 0;
    void Tap(int px, int py, double w)
    {
        if (w <= 0) return;
        int i = (py * aw + px) * 4;
        double wa = art[i + 3] / 255.0 * w;
        if (wa <= 0) return;
        a += wa;
        b += art[i] * wa;
        g += art[i + 1] * wa;
        r += art[i + 2] * wa;
    }
    Tap(x0, y0, (1 - fx) * (1 - fy));
    Tap(x1, y0, fx * (1 - fy));
    Tap(x0, y1, (1 - fx) * fy);
    Tap(x1, y1, fx * fy);
    if (a <= 0) return (0, 0, 0, 0);
    return (b / a, g / a, r / a, a);
}

// Square canvas, transparent background, artwork uniformly scaled to fill.
byte[] Square(int size, double fill)
{
    var dst = new byte[size * size * 4];
    double scale = size * fill / Math.Max(aw, ah);
    double originX = (size - aw * scale) / 2.0;
    double originY = (size - ah * scale) / 2.0;
    for (int y = 0; y < size; y++)
    {
        for (int x = 0; x < size; x++)
        {
            double sx = (x + 0.5 - originX) / scale;
            double sy = (y + 0.5 - originY) / scale;
            (double B, double G, double R, double A) = Sample(sx, sy);
            if (A <= 0.002) continue;
            int o = (y * size + x) * 4;
            dst[o] = (byte)Clamp(B, 0, 255);
            dst[o + 1] = (byte)Clamp(G, 0, 255);
            dst[o + 2] = (byte)Clamp(R, 0, 255);
            dst[o + 3] = (byte)Clamp(A * 255, 0, 255);
        }
    }
    return dst;
}

// Adaptive foreground: solid brand colour, artwork scaled into the 72dp safe zone.
byte[] AdaptiveForeground(int size)
{
    var dst = new byte[size * size * 4];
    for (int i = 0; i < size * size; i++)
    {
        dst[i * 4] = background.B;
        dst[i * 4 + 1] = background.G;
        dst[i * 4 + 2] = background.R;
        dst[i * 4 + 3] = 255;
    }

    double safe = size * 72.0 / 108.0;
    double scale = safe / Math.Max(aw, ah);
    double originX = (size - aw * scale) / 2.0;
    double originY = (size - ah * scale) / 2.0;

    for (int y = 0; y < size; y++)
    {
        for (int x = 0; x < size; x++)
        {
            double sx = (x + 0.5 - originX) / scale;
            double sy = (y + 0.5 - originY) / scale;
            (double B, double G, double R, double A) = Sample(sx, sy);
            if (A <= 0.002) continue;
            int o = (y * size + x) * 4;
            dst[o] = (byte)Clamp(B * A + background.B * (1 - A), 0, 255);
            dst[o + 1] = (byte)Clamp(G * A + background.G * (1 - A), 0, 255);
            dst[o + 2] = (byte)Clamp(R * A + background.R * (1 - A), 0, 255);
        }
    }
    return dst;
}

byte[] EncodePng(byte[] bgra, int w, int h)
{
    BitmapSource bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(bitmap));
    using var ms = new MemoryStream();
    encoder.Save(ms);
    return ms.ToArray();
}

void SavePng(string path, byte[] bgra, int size)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllBytes(path, EncodePng(bgra, size, size));
    Console.WriteLine("  " + Path.GetRelativePath(root, path) + " (" + size + "px)");
}

string winAssets = Path.Combine(root, "windows", "ConnectIPN.Desktop", "Assets");
string resDir = Path.Combine(root, "android", "app", "src", "main", "res");
Directory.CreateDirectory(winAssets);

// --- Windows .ico (multi-resolution, PNG compressed, transparent) ---
int[] icoSizes = { 16, 24, 32, 48, 64, 128, 256 };
var entries = new List<(int size, byte[] png)>();
foreach (int size in icoSizes)
{
    entries.Add((size, EncodePng(Square(size, 0.98), size, size)));
}

using (var ico = new MemoryStream())
{
    var writer = new BinaryWriter(ico);
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)entries.Count);
    int offset = 6 + entries.Count * 16;
    foreach ((int size, byte[] png) in entries)
    {
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(png.Length);
        writer.Write(offset);
        offset += png.Length;
    }
    foreach ((int _, byte[] png) in entries)
    {
        writer.Write(png);
    }
    writer.Flush();
    File.WriteAllBytes(Path.Combine(winAssets, "app.ico"), ico.ToArray());
}
Console.WriteLine("  windows/ConnectIPN.Desktop/Assets/app.ico");

// A plain PNG for in-app headers; WPF picks the 16px frame out of an .ico when the
// Image control is smaller than the icon's largest frame, which looks blurry.
SavePng(Path.Combine(winAssets, "logo.png"), Square(256, 1.0), 256);

// --- Android launcher icons ---
var densities = new (string Dpi, int Legacy, int Adaptive)[]
{
    ("mdpi", 48, 108),
    ("hdpi", 72, 162),
    ("xhdpi", 96, 216),
    ("xxhdpi", 144, 324),
    ("xxxhdpi", 192, 432),
};

foreach ((string dpi, int legacy, int adaptive) in densities)
{
    SavePng(Path.Combine(resDir, "mipmap-" + dpi, "ic_launcher.png"), Square(legacy, 0.96), legacy);
    SavePng(Path.Combine(resDir, "mipmap-" + dpi, "ic_launcher_round.png"), Square(legacy, 0.96), legacy);
    SavePng(Path.Combine(resDir, "mipmap-" + dpi, "ic_launcher_foreground.png"), AdaptiveForeground(adaptive), adaptive);
}

// in-app logo for the header of the Android UI
SavePng(Path.Combine(resDir, "drawable-nodpi", "ic_logo.png"), Square(256, 1.0), 256);

// adaptive icon definitions
string anyDpi = Path.Combine(resDir, "mipmap-anydpi-v26");
Directory.CreateDirectory(anyDpi);
string adaptiveXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
    "<adaptive-icon xmlns:android=\"http://schemas.android.com/apk/res/android\">\n" +
    "    <background android:drawable=\"@color/ic_launcher_background\" />\n" +
    "    <foreground android:drawable=\"@mipmap/ic_launcher_foreground\" />\n" +
    "</adaptive-icon>\n";
File.WriteAllText(Path.Combine(anyDpi, "ic_launcher.xml"), adaptiveXml);
File.WriteAllText(Path.Combine(anyDpi, "ic_launcher_round.xml"), adaptiveXml);
Console.WriteLine("  android .../mipmap-anydpi-v26/ic_launcher.xml");

string colorsXml = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<resources>\n" +
    "    <color name=\"ic_launcher_background\">#" + background.R.ToString("X2") + background.G.ToString("X2") + background.B.ToString("X2") + "</color>\n" +
    "</resources>\n";
File.WriteAllText(Path.Combine(resDir, "values", "colors.xml"), colorsXml);
Console.WriteLine("  android .../values/colors.xml");
Console.WriteLine("done");
