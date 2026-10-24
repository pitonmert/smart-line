using SkiaSharp;
namespace SmartLine.API.Tests.Infrastructure;
internal static class TestImages
{
    public static byte[] Jpeg()
    {
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 80);
        return encoded.ToArray();
    }
}
