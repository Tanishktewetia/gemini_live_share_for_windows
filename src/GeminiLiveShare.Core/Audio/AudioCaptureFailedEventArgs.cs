namespace GeminiLiveShare.Core.Audio;

public sealed class AudioCaptureFailedEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}