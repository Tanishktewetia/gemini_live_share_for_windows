namespace GeminiLiveShare.Core.Audio;

public interface IAudioPlaybackService : IDisposable
{
    void Start();

    void Play(byte[] pcmAudio);

    void CompleteResponse();

    void Clear();

    void Stop();
}