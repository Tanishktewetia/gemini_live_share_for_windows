using Windows.Graphics.Imaging;

namespace GeminiLiveShare.Core.Vision;

public interface IScreenCaptureService : IAsyncDisposable
{
    Task RunAsync(
        Func<SoftwareBitmap, CancellationToken, Task> frameHandler,
        CancellationToken cancellationToken);
}