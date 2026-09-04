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
    private byte[] _heldTail = [];
    private const int FadeTailBytes = 960;

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

            byte[] incoming = pcmAudio;
            if (_pendingSampleByte.HasValue)
            {
                incoming = new byte[pcmAudio.Length + 1];
                incoming[0] = _pendingSampleByte.Value;
                Buffer.BlockCopy(pcmAudio, 0, incoming, 1, pcmAudio.Length);
                _pendingSampleByte = null;
            }

            int incomingLength = incoming.Length;
            if ((incomingLength & 1) != 0)
            {
                _pendingSampleByte = incoming[^1];
                incomingLength--;
            }

            if (incomingLength > 0)
            {
                byte[] combined = new byte[_heldTail.Length + incomingLength];
                Buffer.BlockCopy(_heldTail, 0, combined, 0, _heldTail.Length);
                Buffer.BlockCopy(incoming, 0, combined, _heldTail.Length, incomingLength);
                int bytesToWrite = Math.Max(0, combined.Length - FadeTailBytes);
                if (bytesToWrite > 0)
                {
                    _buffer.AddSamples(combined, 0, bytesToWrite);
                }

                _heldTail = combined[bytesToWrite..];
            }
        }
    }

    public void CompleteResponse()
    {
        lock (_syncRoot)
        {
            if (_buffer is null || _heldTail.Length == 0)
            {
                return;
            }

            for (int offset = 0; offset + 1 < _heldTail.Length; offset += 2)
            {
                double gain = 1.0 - (offset / (double)Math.Max(2, _heldTail.Length - 2));
                short sample = BitConverter.ToInt16(_heldTail, offset);
                short fadedSample = (short)Math.Clamp((int)Math.Round(sample * gain), short.MinValue, short.MaxValue);
                byte[] bytes = BitConverter.GetBytes(fadedSample);
                _heldTail[offset] = bytes[0];
                _heldTail[offset + 1] = bytes[1];
            }

            _buffer.AddSamples(_heldTail, 0, _heldTail.Length);
            _heldTail = [];
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _buffer?.ClearBuffer();
            _pendingSampleByte = null;
            _heldTail = [];
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
            _heldTail = [];
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
