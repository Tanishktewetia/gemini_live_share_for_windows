using SkiaSharp;
using Windows.Graphics.Imaging;

namespace GeminiLiveShare.Core.Vision;

public interface IOcrCredentialDetector
{
    Task<IReadOnlyList<SKRect>> DetectAsync(SoftwareBitmap frame, CancellationToken cancellationToken);
}