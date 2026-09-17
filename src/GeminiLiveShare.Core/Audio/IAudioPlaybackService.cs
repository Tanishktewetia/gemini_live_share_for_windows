namespace GeminiLiveShare.Core.Audio;

public interface IAudioPlaybackService : IDisposable
{
    /// <summary>True while received speech is still waiting to reach the speaker.</summary>
    bool HasQueuedAudio { get; }

    void Start();

    void Play(byte[] pcmAudio);

    void CompleteResponse();

    void Clear();

    void Stop();
}