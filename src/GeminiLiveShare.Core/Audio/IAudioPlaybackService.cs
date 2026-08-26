namespace GeminiLiveShare.Core.Audio;

public interface IAudioPlaybackService : IDisposable
{
    void Start();

    void Play(byte[] pcmAudio);

    void Clear();

    void Stop();
}