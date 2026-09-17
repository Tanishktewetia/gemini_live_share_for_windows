using System.Diagnostics;
using System.Runtime.InteropServices;
using GeminiLiveShare.Core.Security;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace GeminiLiveShare.Core.Vision;

public sealed class ImageProcessingService : IImageProcessingService
{
    // Send ordinary 1080p/1440p monitors at native resolution (desktop labels occupy very few pixels) and only
    // reduce larger captures. Frames are never upscaled: the old 2048 px minimum stretched 1920 px screens, adding
    // bytes but no detail. Frames share the WebSocket with microphone audio, and a measured 537 KB frame (q98,
    // upscaled) cost 11% of microphone audio versus 0.3% at native resolution q90 (317 KB).
    private const int MaximumOutputWidth = 2560;
    public const int DefaultJpegQuality = 90;
    private static readonly TimeSpan FrameProcessingBudget = TimeSpan.FromMilliseconds(1000);

    // Unchanged screens are not re-sent every second, but are refreshed periodically so Gemini's view stays current.
    internal static readonly TimeSpan UnchangedFrameRefreshInterval = TimeSpan.FromSeconds(5);
    private const int ChangeThumbnailWidth = 480;
    private const int ChangeThumbnailHeight = 270;
    private const int ChangedPixelThreshold = 8;
    private const int MinimumChangedPixels = 2;
    private readonly object _changeLock = new();
    private byte[]? _lastSentThumbnail;
    private long _lastSentTimestamp;

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

    public int JpegQuality { get; set; } = DefaultJpegQuality;

    public void ResetChangeDetection()
    {
        lock (_changeLock)
        {
            _lastSentThumbnail = null;
        }
    }

    public async Task<FrameEncodeResult> EncodeForGeminiAsync(
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
                return FrameEncodeResult.Dropped;
            }

            ApplyBlackBoxes(source, ocrBounds);
        }

        if (processingTime.Elapsed > FrameProcessingBudget)
        {
            Trace.WriteLine($"Frame processing exceeded {FrameProcessingBudget.TotalMilliseconds:0} ms; dropping frame.");
            return FrameEncodeResult.Dropped;
        }

        // Compare the sanitized image, so a change is judged on exactly what Gemini would receive.
        byte[] thumbnail = CreateChangeThumbnail(source);
        if (!ShouldSend(thumbnail))
        {
            return FrameEncodeResult.Unchanged;
        }

        int outputWidth = Math.Min(MaximumOutputWidth, source.Width);
        SKBitmap? resized = null;
        if (outputWidth != source.Width)
        {
            int outputHeight = Math.Max(1, (int)Math.Round(source.Height * (outputWidth / (double)source.Width)));
            resized = source.Resize(
                new SKImageInfo(outputWidth, outputHeight, SKColorType.Bgra8888, SKAlphaType.Opaque),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (resized is null)
            {
                throw new InvalidOperationException("Unable to resize the captured screen frame.");
            }
        }

        SKBitmap output = resized ?? source;
        byte[] encodedBytes;
        try
        {
            using SKImage image = SKImage.FromBitmap(output);
            using SKData encodedImage = image.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(JpegQuality, 40, 100));
            encodedBytes = encodedImage.ToArray();
        }
        finally
        {
            resized?.Dispose();
        }

        if (processingTime.Elapsed > FrameProcessingBudget)
        {
            Trace.WriteLine($"Frame processing exceeded {FrameProcessingBudget.TotalMilliseconds:0} ms; dropping frame.");
            return FrameEncodeResult.Dropped;
        }

        lock (_changeLock)
        {
            _lastSentThumbnail = thumbnail;
            _lastSentTimestamp = Stopwatch.GetTimestamp();
        }

        return FrameEncodeResult.Encoded(encodedBytes);
    }

    private bool ShouldSend(byte[] thumbnail)
    {
        lock (_changeLock)
        {
            if (_lastSentThumbnail is null ||
                Stopwatch.GetElapsedTime(_lastSentTimestamp) >= UnchangedFrameRefreshInterval)
            {
                return true;
            }

            return HasVisibleChange(_lastSentThumbnail, thumbnail);
        }
    }

    internal static bool HasVisibleChange(byte[] previous, byte[] current)
    {
        if (previous.Length != current.Length)
        {
            return true;
        }

        // Any small real change counts (a typed character, a moved cursor, a new tooltip), so compare pixels
        // individually instead of an average that would hide a tiny but important update.
        int changed = 0;
        for (int index = 0; index < current.Length; index++)
        {
            if (Math.Abs(current[index] - previous[index]) > ChangedPixelThreshold && ++changed >= MinimumChangedPixels)
            {
                return true;
            }
        }

        return false;
    }

    internal static byte[] CreateChangeThumbnail(SKBitmap source)
    {
        using SKBitmap small = source.Resize(
            new SKImageInfo(ChangeThumbnailWidth, ChangeThumbnailHeight, SKColorType.Gray8, SKAlphaType.Opaque),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None))
            ?? throw new InvalidOperationException("Unable to create the change-detection thumbnail.");
        return small.Bytes;
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
