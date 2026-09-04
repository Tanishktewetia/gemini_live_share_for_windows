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
    private byte? _pendingSampleByte;

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
                // Bound the amount of audio that can sit behind the speaker. If the
                // network briefly outruns playback, BufferedWaveProvider discards the
                // oldest samples instead of allowing conversational delay to grow.
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            _waveOut = new WaveOutEvent
            {
                DesiredLatency = 100,
                NumberOfBuffers = 3
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
            if (_buffer is null || pcmAudio.Length == 0)
            {
                return;
            }

            int offset = 0;
            if (_pendingSampleByte.HasValue)
            {
                byte[] firstSample = [_pendingSampleByte.Value, pcmAudio[0]];
                _buffer.AddSamples(firstSample, 0, firstSample.Length);
                _pendingSampleByte = null;
                offset = 1;
            }

            int remaining = pcmAudio.Length - offset;
            if ((remaining & 1) != 0)
            {
                _pendingSampleByte = pcmAudio[^1];
                remaining--;
            }

            if (remaining > 0)
            {
                _buffer.AddSamples(pcmAudio, offset, remaining);
            }
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _buffer?.ClearBuffer();
            _pendingSampleByte = null;
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
            _pendingSampleByte = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
