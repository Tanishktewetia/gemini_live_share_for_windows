using System.Diagnostics;
using NAudio.Wave;

namespace GeminiLiveShare.Core.Audio;

public sealed class AudioCaptureService : IAudioCaptureService
{
    private const int InputSampleRate = 16_000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;

    private readonly object _syncRoot = new();
    private EchoCancellingMicrophone? _echoCancellingMicrophone;
    private WaveInEvent? _waveIn;

    public event EventHandler<byte[]>? AudioCaptured;

    public event EventHandler<AudioCaptureFailedEventArgs>? CaptureFailed;

    public bool IsCapturing => _echoCancellingMicrophone is not null || _waveIn is not null;

    public bool IsEchoCancellationActive => _echoCancellingMicrophone is not null;

    public void Start()
    {
        lock (_syncRoot)
        {
            if (IsCapturing)
            {
                return;
            }

            // Prefer Windows acoustic echo cancellation so Gemini's speaker output is not streamed back
            // to Gemini as user speech (which makes it interrupt itself when headphones are not used).
            EchoCancellingMicrophone microphone = new(OnEchoCancelledAudio, OnEchoCancellationFailed);
            try
            {
                _echoCancellingMicrophone = microphone;
                microphone.Start();
                return;
            }
            catch (Exception ex)
            {
                _echoCancellingMicrophone = null;
                microphone.Dispose();
                Trace.WriteLine($"Echo cancellation unavailable; using raw microphone capture: {ex.Message}");
            }

            StartRawCapture();
        }
    }

    public void Stop()
    {
        lock (_syncRoot)
        {
            EchoCancellingMicrophone? microphone = _echoCancellingMicrophone;
            _echoCancellingMicrophone = null;
            microphone?.Dispose();

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
    }

    public void Dispose()
    {
        Stop();
    }

    private void StartRawCapture()
    {
        WaveInEvent waveIn = new()
        {
            WaveFormat = new WaveFormat(InputSampleRate, BitsPerSample, Channels),
            // Keep capture frames short so speech reaches Gemini without waiting for
            // a large driver buffer to fill. Two buffers provide enough headroom for
            // normal scheduling without adding a long queue in front of the network.
            BufferMilliseconds = 40,
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

    private void OnEchoCancelledAudio(byte[] audio) => AudioCaptured?.Invoke(this, audio);

    private void OnEchoCancellationFailed(Exception exception)
    {
        // Runs on the DSP thread after its loop has exited; release it without joining that thread.
        lock (_syncRoot)
        {
            _echoCancellingMicrophone = null;
            if (exception is EchoCancellationStalledException)
            {
                // Keep the microphone working rather than silently sending nothing.
                Trace.WriteLine($"{exception.Message} Falling back to raw microphone capture.");
                try
                {
                    StartRawCapture();
                    return;
                }
                catch (Exception fallbackException)
                {
                    exception = fallbackException;
                }
            }
        }

        CaptureFailed?.Invoke(this, new AudioCaptureFailedEventArgs(exception));
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
