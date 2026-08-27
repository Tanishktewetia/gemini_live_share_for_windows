using System.Diagnostics;
using System.Runtime.InteropServices;
using GeminiLiveShare.Core.Security;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GeminiLiveShare.Core.Vision;

public sealed class ImageProcessingService : IImageProcessingService
{
    private const int TargetWidth = 1280;
    private const int JpegQuality = 82;
    private static readonly TimeSpan FrameProcessingBudget = TimeSpan.FromMilliseconds(1000);

    private readonly ICredentialBlurService _credentialBlur;
    private readonly IOcrCredentialDetector _ocrCredentialDetector;
    private readonly ISensitiveContentFilterSettings _filterSettings;

    public ImageProcessingService(
        ICredentialBlurService credentialBlur,
        IOcrCredentialDetector ocrCredentialDetector,
        ISensitiveContentFilterSettings filterSettings)
    {
        _credentialBlur = credentialBlur;
        _ocrCredentialDetector = ocrCredentialDetector;
        _filterSettings = filterSettings;
    }

    public async Task<string?> EncodeForGeminiAsync(
        SoftwareBitmap frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        cancellationToken.ThrowIfCancellationRequested();
        Stopwatch processingTime = Stopwatch.StartNew();

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

        if (_filterSettings.IsEnabled)
        {
            // Both passes inspect the same captured instant and use separate pixel representations.
            // Running them concurrently keeps their independent 500 ms ceilings inside the 1 FPS
            // budget. Only UI Automation mutates source; OCR reads bgraFrame and returns rectangles.
            Task<bool> uiAutomationTask = _credentialBlur
                .BlurPasswordFieldsAsync(source, cancellationToken);
            Task<IReadOnlyList<SKRect>?> ocrTask = DetectOcrBoundsAsync(bgraFrame, cancellationToken);

            bool uiAutomationSucceeded = await uiAutomationTask.ConfigureAwait(false);
            IReadOnlyList<SKRect>? ocrBounds = await ocrTask.ConfigureAwait(false);

            if (!uiAutomationSucceeded || ocrBounds is null)
            {
                return null;
            }

            ApplyBlackBoxes(source, ocrBounds);
        }

        if (processingTime.Elapsed > FrameProcessingBudget)
        {
            Trace.WriteLine($"Frame processing exceeded {FrameProcessingBudget.TotalMilliseconds:0} ms; dropping frame.");
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
        if (processingTime.Elapsed > FrameProcessingBudget)
        {
            Trace.WriteLine($"Frame processing exceeded {FrameProcessingBudget.TotalMilliseconds:0} ms; dropping frame.");
            return null;
        }

        return Convert.ToBase64String(jpeg.ToArray());
    }

    private async Task<IReadOnlyList<SKRect>?> DetectOcrBoundsAsync(
        SoftwareBitmap frame,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _ocrCredentialDetector.DetectAsync(frame, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Sandboxed OCR pass failed; dropping frame before encoding: {ex}");
            return null;
        }
    }

    private static void ApplyBlackBoxes(SKBitmap frame, IReadOnlyList<SKRect> bounds)
    {
        using SKCanvas canvas = new(frame);
        using SKPaint blackPaint = new()
        {
            Color = SKColors.Black,
            BlendMode = SKBlendMode.Src,
            IsAntialias = false
        };

        foreach (SKRect rectangle in bounds)
        {
            SKRect clipped = SKRect.Intersect(rectangle, SKRect.Create(frame.Width, frame.Height));
            if (!clipped.IsEmpty)
            {
                canvas.DrawRect(clipped, blackPaint);
            }
        }
    }
}