using System.Drawing;

namespace GeminiLiveShare.Core.Desktop;

public interface IHighlightOverlayService : IAsyncDisposable
{
    bool IsVisible { get; }

    Task ShowAsync(Rectangle bounds, string label, TimeSpan duration, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class NullHighlightOverlayService : IHighlightOverlayService
{
    public static NullHighlightOverlayService Instance { get; } = new();

    public bool IsVisible => false;

    public Task ShowAsync(Rectangle bounds, string label, TimeSpan duration, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
