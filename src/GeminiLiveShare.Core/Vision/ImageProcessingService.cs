using System.Runtime.InteropServices;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GeminiLiveShare.Core.Vision;

public sealed class ImageProcessingService : IImageProcessingService
{
    private const int TargetWidth = 1280;
    private const int JpegQuality = 82;

    private readonly ICredentialBlurService _credentialBlur;

    public ImageProcessingService(ICredentialBlurService credentialBlur)
    {
        _credentialBlur = credentialBlur;
    }

    public async Task<string?> EncodeForGeminiAsync(
        SoftwareBitmap frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();

        using SoftwareBitmap bgraFrame = SoftwareBitmap.Convert(
            frame,
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied);

        int byteCount = checked(bgraFrame.PixelWidth * bgraFrame.PixelHeight * 4);
        Windows.Storage.Streams.Buffer pixelBuffer = new((uint)byteCount);
        bgraFrame.CopyToBuffer(pixelBuffer);
        byte[] pixels = new byte[byteCount];
        using (DataReader reader = DataReader.FromBuffer(pixelBuffer))
        {
            reader.ReadBytes(pixels);
        }

        using SKBitmap source = new(
            bgraFrame.PixelWidth,
            bgraFrame.PixelHeight,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        Marshal.Copy(pixels, 0, source.GetPixels(), pixels.Length);

        // Privacy-critical ordering: blur the full-resolution frame before any resize or encoding.
        if (!await _credentialBlur.BlurPasswordFieldsAsync(source, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        int outputWidth = Math.Min(TargetWidth, source.Width);
        int outputHeight = Math.Max(1, (int)Math.Round(source.Height * (outputWidth / (double)source.Width)));
        using SKBitmap? resized = source.Resize(
            new SKImageInfo(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Opaque),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (resized is null)
        {
            throw new InvalidOperationException("Unable to resize the captured screen frame.");
        }

        using SKImage image = SKImage.FromBitmap(resized);
        using SKData jpeg = image.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        return Convert.ToBase64String(jpeg.ToArray());
    }
}