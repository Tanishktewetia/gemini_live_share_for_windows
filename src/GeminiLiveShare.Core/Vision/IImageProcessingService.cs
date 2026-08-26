using Windows.Graphics.Imaging;

namespace GeminiLiveShare.Core.Vision;

public interface IImageProcessingService
{
    Task<string?> EncodeForGeminiAsync(SoftwareBitmap frame, CancellationToken cancellationToken);
}