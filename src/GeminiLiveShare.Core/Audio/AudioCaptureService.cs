using NAudio.Wave;

namespace GeminiLiveShare.Core.Audio;

public sealed class AudioCaptureService : IAudioCaptureService
{
    private const int InputSampleRate = 16_000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    private WaveInEvent? _waveIn;

    public event EventHandler<byte[]>? AudioCaptured;

    public event EventHandler<AudioCaptureFailedEventArgs>? CaptureFailed;

    public bool IsCapturing => _waveIn is not null;

    public void Start()
    {
        if (_waveIn is not null)
        {
            return;
        }

        WaveInEvent waveIn = new()
        {
            WaveFormat = new WaveFormat(InputSampleRate, BitsPerSample, Channels),
            BufferMilliseconds = 50,
            NumberOfBuffers = 3
        };

        waveIn.DataAvailable += OnDataAvailable;
        waveIn.RecordingStopped += OnRecordingStopped;

        try
        {
            waveIn.StartRecording();
            _waveIn = waveIn;
        }
        catch
        {
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.RecordingStopped -= OnRecordingStopped;
            waveIn.Dispose();
            throw;
        }
    }

    public void Stop()
    {
        WaveInEvent? waveIn = _waveIn;
        _waveIn = null;
        if (waveIn is null)
        {
            return;
        }

        waveIn.StopRecording();
        waveIn.DataAvailable -= OnDataAvailable;
        waveIn.RecordingStopped -= OnRecordingStopped;
        waveIn.Dispose();
    }

    public void Dispose()
    {
        Stop();
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0)
        {
            return;
        }

        byte[] audio = new byte[e.BytesRecorded];
        Buffer.BlockCopy(e.Buffer, 0, audio, 0, e.BytesRecorded);
        AudioCaptured?.Invoke(this, audio);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (sender is WaveInEvent waveIn && ReferenceEquals(_waveIn, waveIn))
        {
            _waveIn = null;
            waveIn.DataAvailable -= OnDataAvailable;
            waveIn.RecordingStopped -= OnRecordingStopped;
            waveIn.Dispose();
        }

        if (e.Exception is not null)
        {
            CaptureFailed?.Invoke(this, new AudioCaptureFailedEventArgs(e.Exception));
        }
    }
}