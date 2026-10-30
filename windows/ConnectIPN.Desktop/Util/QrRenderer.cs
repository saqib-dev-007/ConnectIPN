using System.IO;
using System.Windows.Media.Imaging;
using QRCoder;

namespace ConnectIPN.Util;

internal static class QrRenderer
{
    public static BitmapSource Create(string text, int pixelsPerModule = 8)
    {
        using var generator = new QRCodeGenerator();
        using QRCodeData data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data);
        byte[] bytes = png.GetGraphic(pixelsPerModule);

        var image = new BitmapImage();
        using (var stream = new MemoryStream(bytes))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
        }
        image.Freeze();
        return image;
    }
}
