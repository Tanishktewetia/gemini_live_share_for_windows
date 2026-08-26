using NAudio.Wave;

namespace GeminiLiveShare.Core.Audio;

public sealed class AudioPlaybackService : IAudioPlaybackService
{
    private const int OutputSampleRate = 24_000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    private readonly object _syncRoot = new();
    private BufferedWaveProvider? _buffer;
    private WaveOutEvent? _waveOut;

    public void Start()
    {
        lock (_syncRoot)
        {
            if (_waveOut is not null)
            {
                return;
            }

            _buffer = new BufferedWaveProvider(new WaveFormat(OutputSampleRate, BitsPerSample, Channels))
            {
                BufferDuration = TimeSpan.FromSeconds(30),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            _waveOut = new WaveOutEvent
            {
                DesiredLatency = 100,
                NumberOfBuffers = 2
            };
            _waveOut.Init(_buffer);
            _waveOut.Play();
        }
    }

    public void Play(byte[] pcmAudio)
    {
        ArgumentNullException.ThrowIfNull(pcmAudio);

        lock (_syncRoot)
        {
            _buffer?.AddSamples(pcmAudio, 0, pcmAudio.Length);
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _buffer?.ClearBuffer();
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            _waveOut?.Stop();
            _waveOut?.Dispose();
            _waveOut = null;
            _buffer = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}