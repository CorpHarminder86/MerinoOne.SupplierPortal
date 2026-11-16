using QRCoder;

namespace MerinoOne.SupplierPortal.Infrastructure.Services;

/// <summary>
/// R19 — renders a string as a QR code PNG for embedding in a generated PDF. Uses QRCoder's
/// <see cref="PngByteQRCode"/>, which is pure-managed (no System.Drawing), so it runs on any host OS.
/// </summary>
public static class QrCodeImage
{
    /// <summary>
    /// PNG bytes of a QR code for <paramref name="value"/> (error correction level M, UTF-8 payload). Black modules on a
    /// white background including the standard quiet zone. <paramref name="pixelsPerModule"/> sets the raster
    /// resolution only; a PDF scales the image to whatever size it is placed at.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is null, empty or whitespace.</exception>
    public static byte[] Png(string value, int pixelsPerModule = 10)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A QR code needs a non-blank value.", nameof(value));
        if (pixelsPerModule < 1)
            throw new ArgumentOutOfRangeException(nameof(pixelsPerModule), pixelsPerModule, "Must be at least 1.");

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.M, forceUtf8: true);
        return new PngByteQRCode(data).GetGraphic(pixelsPerModule);
    }
}
