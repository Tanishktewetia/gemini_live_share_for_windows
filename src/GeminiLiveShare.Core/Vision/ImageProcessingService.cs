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
    private bool _forceSendNextFrame;

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
            _forceSendNextFrame = false;
        }
    }

    public void ForceSendNextFrame()
    {
        lock (_changeLock)
        {
            _forceSendNextFrame = true;
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
        byte[] fullResolutionJpeg;
        using SKBitmap uploadFrame = output.Copy();
        try
        {
            // The grid is only sent to Gemini. The user's screen and the full-resolution zoom source
            // remain untouched, so zoom_region can read the original pixels.
            DrawZoomGrid(uploadFrame);
            encodedBytes = EncodeJpeg(uploadFrame, Math.Clamp(JpegQuality, 40, 100));
            fullResolutionJpeg = EncodeJpeg(source, Math.Clamp(JpegQuality, 40, 100));
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
            _forceSendNextFrame = false;
        }

        return FrameEncodeResult.Encoded(
            encodedBytes,
            fullResolutionJpeg,
            source.Width,
            source.Height);
    }

#pragma warning disable CS0618
    private static void DrawZoomGrid(SKBitmap bitmap)
    {
        // Tiny synthetic/test frames are not useful to Gemini and a grid could cover a protected
        // OCR rectangle; real desktop captures are large enough for the labels to be meaningful.
        if (bitmap.Width < 320 || bitmap.Height < 200)
        {
            return;
        }

        using SKCanvas canvas = new(bitmap);
        using SKPaint linePaint = new()
        {
            Color = SKColors.White.WithAlpha(55),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1, bitmap.Width / 1400f),
            IsAntialias = true
        };
        using SKPaint labelBackground = new()
        {
            Color = SKColors.Black.WithAlpha(110),
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };
        using SKPaint labelPaint = new()
        {
            Color = SKColors.White.WithAlpha(220),
            TextSize = Math.Max(14, bitmap.Width / 110f),
            IsAntialias = true
        };

        float cellWidth = bitmap.Width / 4f;
        float cellHeight = bitmap.Height / 4f;
        for (int column = 1; column < 4; column++)
        {
            float x = column * cellWidth;
            canvas.DrawLine(x, 0, x, bitmap.Height, linePaint);
        }
        for (int row = 1; row < 4; row++)
        {
            float y = row * cellHeight;
            canvas.DrawLine(0, y, bitmap.Width, y, linePaint);
        }

        for (int row = 0; row < 4; row++)
        {
            for (int column = 0; column < 4; column++)
            {
                string label = $"{(char)('A' + column)}{row + 1}";
                float x = column * cellWidth + 6;
                float y = row * cellHeight + labelPaint.TextSize + 6;
                SKRect textBounds = new();
                labelPaint.MeasureText(label, ref textBounds);
                canvas.DrawRect(new SKRect(x - 3, y - labelPaint.TextSize - 3, x + textBounds.Width + 3, y + 3), labelBackground);
                canvas.DrawText(label, x, y, labelPaint);
            }
        }
    }

#pragma warning restore CS0618

    private static byte[] EncodeJpeg(SKBitmap bitmap, int quality)
    {
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encodedImage = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return encodedImage.ToArray();
    }

    private bool ShouldSend(byte[] thumbnail)
    {
        lock (_changeLock)
        {
            if (_forceSendNextFrame ||
                _lastSentThumbnail is null ||
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
