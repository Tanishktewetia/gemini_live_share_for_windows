namespace GeminiLiveShare.Core.Audio;

public interface IAudioCaptureService : IDisposable
{
    event EventHandler<byte[]>? AudioCaptured;

    event EventHandler<AudioCaptureFailedEventArgs>? CaptureFailed;

    bool IsCapturing { get; }

    bool IsEchoCancellationActive { get; }

    void Start();

    void Stop();
}