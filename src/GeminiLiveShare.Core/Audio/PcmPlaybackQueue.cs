using NAudio.Wave;

namespace GeminiLiveShare.Core.Audio;

/// <summary>
/// Unbounded, lossless PCM queue for speaker playback. Gemini Live delivers speech much faster than
/// real time (a measured 38.7 s reply arrived in about 10.6 s), so a fixed-size buffer that discards on
/// overflow cut roughly two thirds of long replies into audible garbage. Latency cannot build up
/// across turns because user barge-in clears the queue.
/// </summary>
internal sealed class PcmPlaybackQueue : IWaveProvider
{
    private readonly object _syncRoot = new();
    private readonly Queue<byte[]> _segments = new();
    private int _headOffset;
    private long _bufferedBytes;

    public PcmPlaybackQueue(WaveFormat waveFormat)
    {
        WaveFormat = waveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public long BufferedBytes
    {
        get
        {
            lock (_syncRoot)
            {
                return _bufferedBytes;
            }
        }
    }

    public void Enqueue(byte[] pcm, int count)
    {
        if (count <= 0)
        {
            return;
        }

        byte[] segment = count == pcm.Length ? pcm : pcm[..count];
        lock (_syncRoot)
        {
            _segments.Enqueue(segment);
            _bufferedBytes += segment.Length;
        }
    }

    public void Clear()
    {
        lock (_syncRoot)
        {
            _segments.Clear();
            _headOffset = 0;
            _bufferedBytes = 0;
        }
    }

    /// <summary>Always fills the whole request, padding with silence, so the output device never stops.</summary>
    public int Read(byte[] buffer, int offset, int count)
    {
        int written = 0;
        lock (_syncRoot)
        {
            while (written < count && _segments.Count > 0)
            {
                byte[] head = _segments.Peek();
                int toCopy = Math.Min(count - written, head.Length - _headOffset);
                Buffer.BlockCopy(head, _headOffset, buffer, offset + written, toCopy);
                written += toCopy;
                _headOffset += toCopy;
                _bufferedBytes -= toCopy;
                if (_headOffset == head.Length)
                {
                    _segments.Dequeue();
                    _headOffset = 0;
                }
            }
        }

        if (written < count)
        {
            Array.Clear(buffer, offset + written, count - written);
        }

        return count;
    }
}
