using SkiaSharp;

namespace GeminiLiveShare.Core.Vision;

public interface ICredentialBlurService
{
    Task<bool> BlurPasswordFieldsAsync(SKBitmap fullResolutionFrame, CancellationToken cancellationToken);
}