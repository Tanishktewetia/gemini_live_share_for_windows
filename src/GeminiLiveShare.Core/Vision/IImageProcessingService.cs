using Windows.Graphics.Imaging;

namespace GeminiLiveShare.Core.Vision;

public interface IImageProcessingService
{
    /// <summary>JPEG quality for the next frames; the session lowers it while uploads are slow.</summary>
    int JpegQuality { get; set; }

    /// <summary>Forget the last sent frame so the next frame is always sent (e.g. when sharing starts).</summary>
    void ResetChangeDetection();

    Task<FrameEncodeResult> EncodeForGeminiAsync(SoftwareBitmap frame, CancellationToken cancellationToken);
}

public enum FrameEncodeStatus
{
    /// <summary>Sanitized and encoded; send it.</summary>
    Encoded,

    /// <summary>The screen has not visibly changed since the last sent frame; nothing to send.</summary>
    Unchanged,

    /// <summary>Privacy protection failed or processing was too slow; the frame must not be sent.</summary>
    Dropped
}

public sealed record FrameEncodeResult(FrameEncodeStatus Status, string? Base64Jpeg = null, int JpegBytes = 0)
{
    public static FrameEncodeResult Dropped { get; } = new(FrameEncodeStatus.Dropped);

    public static FrameEncodeResult Unchanged { get; } = new(FrameEncodeStatus.Unchanged);

    public static FrameEncodeResult Encoded(byte[] jpeg) => new(FrameEncodeStatus.Encoded, Convert.ToBase64String(jpeg), jpeg.Length);
}
