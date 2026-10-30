using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

string root = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
string sourcePath = Path.Combine(root, "Icon", "Icon.png");

var frame = BitmapFrame.Create(new Uri(sourcePath, UriKind.Absolute), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
int sw = converted.PixelWidth;
int sh = converted.PixelHeight;
var source = new byte[sw * sh * 4];
converted.CopyPixels(source, sw * 4, 0);
Console.WriteLine($"source {sw}x{sh}");

double Luminance(byte b, byte g, byte r) => (0.0722 * b + 0.7152 * g + 0.2126 * r) / 255.0;
double PixelLuminance(int i) => Luminance(source[i], source[i + 1], source[i + 2]);
bool IsLight(int i) => source[i + 3] < 8 || (source[i] > 232 && source[i + 1] > 232 && source[i + 2] > 232);

// --- background colour: average of four patches just inside the rounded square ---
long sr = 0, sg = 0, sb = 0;
int samples = 0;
foreach ((double fx, double fy) in new[] { (0.12, 0.12), (0.88, 0.12), (0.12, 0.88), (0.88, 0.88) })
{
    int px = (int)(fx * sw), py = (int)(fy * sh);
    for (int y = py - 2; y <= py + 2; y++)
    {
        for (int x = px - 2; x <= px + 2; x++)
        {
            int i = (y * sw + x) * 4;
            if (IsLight(i)) continue;
            sb += source[i];
            sg += source[i + 1];
            sr += source[i + 2];
            samples++;
        }
    }
}
Color background = samples > 0
    ? Color.FromRgb((byte)(sr / samples), (byte)(sg / samples), (byte)(sb / samples))
    : Color.FromRgb(0x2E, 0x3B, 0x44);
Console.WriteLine($"background #{background.R:X2}{background.G:X2}{background.B:X2}");

// --- flood fill the light border that sits outside the rounded square ---
var outside = new bool[sw * sh];
var stack = new Stack<int>();
void Seed(int x, int y)
{
    int i = y * sw + x;
    if (!outside[i] && IsLight(i * 4))
    {
        outside[i] = true;
        stack.Push(i);
    }
}
for (int x = 0; x < sw; x++)
{
    Seed(x, 0);
    Seed(x, sh - 1);
}
for (int y = 0; y < sh; y++)
{
    Seed(0, y);
    Seed(sw - 1, y);
}
while (stack.Count > 0)
{
    int i = stack.Pop();
    int x = i % sw, y = i / sw;
    if (x > 0) Seed(x - 1, y);
    if (x < sw - 1) Seed(x + 1, y);
    if (y > 0) Seed(x, y - 1);
    if (y < sh - 1) Seed(x, y + 1);
}

// The tile has a lighter rim right at its edge; grow the outside mask a few pixels
// so that rim can never be mistaken for artwork. The art itself sits well inside.
var edgeBand = (bool[])outside.Clone();
for (int pass = 0; pass < 12; pass++)
{
    var next = (bool[])edgeBand.Clone();
    for (int y = 1; y < sh - 1; y++)
    {
        for (int x = 1; x < sw - 1; x++)
        {
            int i = y * sw + x;
            if (edgeBand[i]) continue;
            if (edgeBand[i - 1] || edgeBand[i + 1] || edgeBand[i - sw] || edgeBand[i + sw])
            {
                next[i] = true;
            }
        }
    }
    edgeBand = next;
}

double bgLum = Luminance(background.B, background.G, background.R);
double artLum = 0;
for (int i = 0; i < sw * sh; i++)
{
    if (edgeBand[i]) continue;
    artLum = Math.Max(artLum, PixelLuminance(i * 4));
}
if (artLum <= bgLum + 0.05) artLum = bgLum + 0.05;

double Smooth(double v, double lo, double hi) => Math.Clamp((v - lo) / (hi - lo), 0, 1);

double Artness(int index, byte b, byte g, byte r)
{
    if (index < 0 || edgeBand[index]) return 0;
    double bright = Math.Clamp((Luminance(b, g, r) - bgLum) / (artLum - bgLum), 0, 1);
    return Smooth(bright, 0.10, 0.30);
}

// Bounding box of the artwork, used to centre and scale the adaptive foreground.
int minX = sw, minY = sh, maxX = -1, maxY = -1;
for (int y = 0; y < sh; y++)
{
    for (int x = 0; x < sw; x++)
    {
        int i = y * sw + x;
        if (outside[i]) continue;
        if (Artness(i, source[i * 4], source[i * 4 + 1], source[i * 4 + 2]) < 0.5) continue;
        if (x < minX) minX = x;
        if (x > maxX) maxX = x;
        if (y < minY) minY = y;
        if (y > maxY) maxY = y;
    }
}
if (maxX < 0)
{
    minX = 0;
    minY = 0;
    maxX = sw - 1;
    maxY = sh - 1;
}
double artWidth = maxX - minX + 1;
double artHeight = maxY - minY + 1;
double artCenterX = minX + artWidth / 2;
double artCenterY = minY + artHeight / 2;
Console.WriteLine($"artwork {artWidth}x{artHeight} centred at {artCenterX:F0},{artCenterY:F0}");

string winAssets = Path.Combine(root, "windows", "ConnectIPN.Desktop", "Assets");
string resDir = Path.Combine(root, "android", "app", "src", "main", "res");
Directory.CreateDirectory(winAssets);

// Rounded-square artwork with the white corners replaced by the icon's own background.
var clean = new byte[source.Length];
for (int i = 0; i < sw * sh; i++)
{
    int o = i * 4;
    if (outside[i])
    {
        clean[o] = background.B;
        clean[o + 1] = background.G;
        clean[o + 2] = background.R;
        clean[o + 3] = 255;
    }
    else
    {
        clean[o] = source[o];
        clean[o + 1] = source[o + 1];
        clean[o + 2] = source[o + 2];
        clean[o + 3] = source[o + 3];
    }
}

byte[] Resize(int dw, int dh)
{
    var dst = new byte[dw * dh * 4];
    double xr = (double)sw / dw, yr = (double)sh / dh;
    for (int y = 0; y < dh; y++)
    {
        int y0 = (int)(y * yr);
        int y1 = Math.Max(y0 + 1, Math.Min(sh, (int)((y + 1) * yr)));
        for (int x = 0; x < dw; x++)
        {
            int x0 = (int)(x * xr);
            int x1 = Math.Max(x0 + 1, Math.Min(sw, (int)((x + 1) * xr)));
            double a = 0, b = 0, g = 0, r = 0;
            int count = 0;
            for (int yy = y0; yy < y1; yy++)
            {
                for (int xx = x0; xx < x1; xx++)
                {
                    int i = (yy * sw + xx) * 4;
                    double alpha = clean[i + 3] / 255.0;
                    b += clean[i] * alpha;
                    g += clean[i + 1] * alpha;
                    r += clean[i + 2] * alpha;
                    a += alpha;
                    count++;
                }
            }
            int o = (y * dw + x) * 4;
            if (a > 0)
            {
                dst[o] = (byte)(b / a);
                dst[o + 1] = (byte)(g / a);
                dst[o + 2] = (byte)(r / a);
                dst[o + 3] = (byte)(a / count * 255);
            }
        }
    }
    return dst;
}

/// Adaptive foreground: a solid background with the artwork scaled into the 66dp
/// safe zone, so no launcher mask can clip it.
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

    double safe = size * 68.0 / 108.0;
    double scale = Math.Min(safe / artWidth, safe / artHeight);
    double originX = size / 2.0 - artCenterX * scale;
    double originY = size / 2.0 - artCenterY * scale;

    int Nearest(int x, int y, double ox, double oy, double sc)
    {
        int sx = (int)Math.Round((x + 0.5 - ox) / sc);
        int sy = (int)Math.Round((y + 0.5 - oy) / sc);
        if (sx < 0 || sy < 0 || sx > sw - 1 || sy > sh - 1)
        {
            return -1;
        }
        return sy * sw + sx;
    }

    (double B, double G, double R) Sample(double sx, double sy)
    {
        if (sx < 0 || sy < 0 || sx > sw - 1 || sy > sh - 1)
        {
            return (background.B, background.G, background.R);
        }
        int x0 = (int)Math.Floor(sx), y0 = (int)Math.Floor(sy);
        int x1 = Math.Min(x0 + 1, sw - 1), y1 = Math.Min(y0 + 1, sh - 1);
        double fx = sx - x0, fy = sy - y0;
        double B = 0, G = 0, R = 0;
        foreach ((int px, int py, double w) in new[]
                 {
                     (x0, y0, (1 - fx) * (1 - fy)),
                     (x1, y0, fx * (1 - fy)),
                     (x0, y1, (1 - fx) * fy),
                     (x1, y1, fx * fy),
                 })
        {
            int i = py * sw + px;
            if (outside[i])
            {
                B += background.B * w;
                G += background.G * w;
                R += background.R * w;
            }
            else
            {
                B += source[i * 4] * w;
                G += source[i * 4 + 1] * w;
                R += source[i * 4 + 2] * w;
            }
        }
        return (B, G, R);
    }

    for (int y = 0; y < size; y++)
    {
        for (int x = 0; x < size; x++)
        {
            double sx = (x + 0.5 - originX) / scale;
            double sy = (y + 0.5 - originY) / scale;
            (double B, double G, double R) = Sample(sx, sy);
            double alpha = Artness(Nearest(x, y, originX, originY, scale), (byte)B, (byte)G, (byte)R);
            if (alpha <= 0)
            {
                continue;
            }
            int o = (y * size + x) * 4;
            if (alpha <= 0) continue;
            dst[o] = (byte)(dst[o] + (B - dst[o]) * alpha);
            dst[o + 1] = (byte)(dst[o + 1] + (G - dst[o + 1]) * alpha);
            dst[o + 2] = (byte)(dst[o + 2] + (R - dst[o + 2]) * alpha);
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
    Console.WriteLine("  " + Path.GetRelativePath(root, path) + $" ({size}px)");
}

// --- Windows .ico (multi-resolution, PNG compressed) ---
int[] icoSizes = { 16, 24, 32, 48, 64, 128, 256 };
var entries = new List<(int size, byte[] png)>();
foreach (int size in icoSizes)
{
    entries.Add((size, EncodePng(Resize(size, size), size, size)));
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

// A plain PNG for in-app headers: WPF picks the 16px frame out of an .ico when the
// Image control is smaller than the icon's largest frame, which looks blurry.
SavePng(Path.Combine(winAssets, "logo.png"), Resize(256, 256), 256);

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
    SavePng(Path.Combine(resDir, "mipmap-" + dpi, "ic_launcher.png"), Resize(legacy, legacy), legacy);
    SavePng(Path.Combine(resDir, "mipmap-" + dpi, "ic_launcher_round.png"), Resize(legacy, legacy), legacy);
    SavePng(Path.Combine(resDir, "mipmap-" + dpi, "ic_launcher_foreground.png"), AdaptiveForeground(adaptive), adaptive);
}

// in-app logo for the header of the Android UI
SavePng(Path.Combine(resDir, "drawable-nodpi", "ic_logo.png"), Resize(256, 256), 256);

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
    $"    <color name=\"ic_launcher_background\">#{background.R:X2}{background.G:X2}{background.B:X2}</color>\n" +
    "</resources>\n";
File.WriteAllText(Path.Combine(resDir, "values", "colors.xml"), colorsXml);
Console.WriteLine("  android .../values/colors.xml");
Console.WriteLine("done");
