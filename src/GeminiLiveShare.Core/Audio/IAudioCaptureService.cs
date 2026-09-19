namespace GeminiLiveShare.Core.Audio;

public sealed record AudioInputDeviceInfo(int DeviceNumber, string Name);

public interface IAudioCaptureService : IDisposable
{
    event EventHandler<byte[]>? AudioCaptured;

    event EventHandler<AudioCaptureFailedEventArgs>? CaptureFailed;

    event EventHandler<bool>? UserSpeakingChanged;

    bool IsCapturing { get; }

    bool IsEchoCancellationActive { get; }

    IReadOnlyList<AudioInputDeviceInfo> InputDevices { get; }

    int SelectedInputDeviceNumber { get; set; }

    void Start();

    void Stop();
}