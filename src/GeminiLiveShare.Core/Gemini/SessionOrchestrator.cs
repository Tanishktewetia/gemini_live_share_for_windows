using GeminiLiveShare.Core.Audio;
using GeminiLiveShare.Core.Vision;
using Windows.Graphics.Imaging;
using System.Threading.Channels;

namespace GeminiLiveShare.Core.Gemini;

public sealed class SessionOrchestrator : IAsyncDisposable
{
    private const int MicrophoneQueueCapacity = 4;
#if DEBUG
    // TEMP DEBUG - remove after Phase 3a verification
    private const bool SaveFramesForDebug = true;
#endif

    private readonly IAudioCaptureService _audioCapture;
    private readonly IAudioPlaybackService _audioPlayback;
    private readonly IGeminiLiveClient _liveClient;
    private readonly IScreenCaptureService _screenCapture;
    private readonly IImageProcessingService _imageProcessing;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private CancellationTokenSource? _sessionCancellation;
    private Channel<byte[]>? _microphoneAudio;
    private Task? _microphoneSendTask;
    private Task? _videoTask;

    public SessionOrchestrator(
        IAudioCaptureService audioCapture,
        IAudioPlaybackService audioPlayback,
        IGeminiLiveClient liveClient,
        IScreenCaptureService screenCapture,
        IImageProcessingService imageProcessing)
    {
        _audioCapture = audioCapture;
        _audioPlayback = audioPlayback;
        _liveClient = liveClient;
        _screenCapture = screenCapture;
        _imageProcessing = imageProcessing;
        _audioCapture.AudioCaptured += OnAudioCaptured;
        _liveClient.AudioReceived += OnAudioReceived;
        _liveClient.Interrupted += OnInterrupted;
        _liveClient.StatusChanged += OnClientStatusChanged;
        _audioCapture.CaptureFailed += OnCaptureFailed;
    }

    public event EventHandler<string>? StatusChanged;

    public event EventHandler? MicrophoneStateChanged;

    public bool IsRunning { get; private set; }

    public bool IsMicrophoneOn { get; private set; }

    public async Task StartAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsRunning)
            {
                return;
            }

            CancellationTokenSource sessionCancellation = new();
            try
            {
                await _liveClient.ConnectAsync(apiKey, cancellationToken).ConfigureAwait(false);
                _audioPlayback.Start();
                _sessionCancellation = sessionCancellation;
                IsRunning = true;
                SetMicrophoneState(true);
                StartMicrophoneSender(sessionCancellation.Token);
                _audioCapture.Start();
                StartVideoSender(sessionCancellation.Token);
                StatusChanged?.Invoke(this, "Conversation started");
            }
            catch
            {
                IsRunning = false;
                SetMicrophoneState(false);
                _audioCapture.Stop();
                sessionCancellation.Cancel();
                await StopMicrophoneSenderAsync().ConfigureAwait(false);
                await StopVideoSenderAsync().ConfigureAwait(false);
                _audioPlayback.Stop();
                await _liveClient.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                _sessionCancellation = null;
                sessionCancellation.Dispose();
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning && !_liveClient.IsConnected)
            {
                return;
            }

            IsRunning = false;
            SetMicrophoneState(false);
            _sessionCancellation?.Cancel();
            _audioCapture.Stop();
            await StopMicrophoneSenderAsync().ConfigureAwait(false);
            await StopVideoSenderAsync().ConfigureAwait(false);
            _audioPlayback.Clear();
            _audioPlayback.Stop();
            await _liveClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
            StatusChanged?.Invoke(this, "Conversation stopped");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task SetMicrophoneEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning || enabled == IsMicrophoneOn)
            {
                return;
            }

            if (enabled)
            {
                CancellationToken sessionToken = _sessionCancellation?.Token ?? CancellationToken.None;
                StartMicrophoneSender(sessionToken);
                SetMicrophoneState(true);
                try
                {
                    _audioCapture.Start();
                }
                catch
                {
                    SetMicrophoneState(false);
                    await StopMicrophoneSenderAsync().ConfigureAwait(false);
                    throw;
                }

                StatusChanged?.Invoke(this, "Microphone ON");
                return;
            }

            SetMicrophoneState(false);
            _audioCapture.Stop();
            await StopMicrophoneSenderAsync().ConfigureAwait(false);
            await _liveClient.SendAudioStreamEndAsync(cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(this, "Microphone OFF");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _audioCapture.AudioCaptured -= OnAudioCaptured;
        _audioCapture.CaptureFailed -= OnCaptureFailed;
        _liveClient.AudioReceived -= OnAudioReceived;
        _liveClient.Interrupted -= OnInterrupted;
        _liveClient.StatusChanged -= OnClientStatusChanged;
        _audioCapture.Dispose();
        _audioPlayback.Dispose();
        await _screenCapture.DisposeAsync().ConfigureAwait(false);
        await _liveClient.DisposeAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }

    private void OnAudioCaptured(object? sender, byte[] audio)
    {
        if (!IsRunning || !IsMicrophoneOn)
        {
            return;
        }

        _microphoneAudio?.Writer.TryWrite(audio);
    }

    private void OnAudioReceived(object? sender, byte[] audio) => _audioPlayback.Play(audio);

    private void OnInterrupted(object? sender, EventArgs e)
    {
        _audioPlayback.Clear();
        StatusChanged?.Invoke(this, "Gemini response interrupted by user speech");
    }

    private void OnClientStatusChanged(object? sender, string status) => StatusChanged?.Invoke(this, status);

    private void OnCaptureFailed(object? sender, AudioCaptureFailedEventArgs e)
    {
        SetMicrophoneState(false);
        _microphoneAudio?.Writer.TryComplete();
        StatusChanged?.Invoke(this, $"Microphone capture stopped: {e.Exception.Message}");
    }

    private void StartMicrophoneSender(CancellationToken cancellationToken)
    {
        BoundedChannelOptions options = new(MicrophoneQueueCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        };
        Channel<byte[]> microphoneAudio = Channel.CreateBounded<byte[]>(options);
        _microphoneAudio = microphoneAudio;
        _microphoneSendTask = SendMicrophoneAudioAsync(microphoneAudio.Reader, cancellationToken);
    }

    private async Task StopMicrophoneSenderAsync()
    {
        Channel<byte[]>? microphoneAudio = _microphoneAudio;
        Task? microphoneSendTask = _microphoneSendTask;
        _microphoneAudio = null;
        _microphoneSendTask = null;
        microphoneAudio?.Writer.TryComplete();

        if (microphoneSendTask is not null)
        {
            try
            {
                await microphoneSendTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task SendMicrophoneAudioAsync(ChannelReader<byte[]> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (byte[] audio in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (IsMicrophoneOn)
                {
                    await _liveClient.SendAudioAsync(audio, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            SetMicrophoneState(false);
            StatusChanged?.Invoke(this, "Unable to send microphone audio.");
        }
    }

    private void StartVideoSender(CancellationToken cancellationToken)
    {
        // This task is intentionally independent from capture/audio sending. JPEG work never runs on
        // the audio callback and can neither await nor apply backpressure to the microphone channel.
        _videoTask = Task.Run(() => RunVideoSenderAsync(cancellationToken), cancellationToken);
    }

    private async Task RunVideoSenderAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _screenCapture.RunAsync(ProcessAndSendVideoFrameAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Screen capture stopped: {ex.Message}");
        }
    }

    private async Task ProcessAndSendVideoFrameAsync(SoftwareBitmap frame, CancellationToken cancellationToken)
    {
        string? base64Jpeg = await _imageProcessing.EncodeForGeminiAsync(frame, cancellationToken).ConfigureAwait(false);
        if (base64Jpeg is null)
        {
            return;
        }

#if DEBUG
        // TEMP DEBUG - remove after Phase 3a verification
        if (SaveFramesForDebug)
        {
            _ = Task.Run(() => SaveFrameForDebug(base64Jpeg));
        }
#endif
        await _liveClient.SendVideoFrameAsync(base64Jpeg, cancellationToken).ConfigureAwait(false);
    }

#if DEBUG
    // TEMP DEBUG - remove after Phase 3a verification
    private static void SaveFrameForDebug(string base64Jpeg)
    {
        try
        {
            const string outputDirectory = @"C:\Temp\gemini-frames";
            Directory.CreateDirectory(outputDirectory);
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string outputPath = Path.Combine(outputDirectory, $"frame_{timestamp}.jpg");
            File.WriteAllBytes(outputPath, Convert.FromBase64String(base64Jpeg));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"Unable to save temporary debug frame: {ex.Message}");
        }
    }
#endif

    private async Task StopVideoSenderAsync()
    {
        Task? videoTask = _videoTask;
        _videoTask = null;
        if (videoTask is null)
        {
            return;
        }

        try
        {
            await videoTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void SetMicrophoneState(bool isOn)
    {
        if (IsMicrophoneOn == isOn)
        {
            return;
        }

        IsMicrophoneOn = isOn;
        MicrophoneStateChanged?.Invoke(this, EventArgs.Empty);
    }
}