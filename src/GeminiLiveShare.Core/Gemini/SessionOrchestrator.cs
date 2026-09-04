using GeminiLiveShare.Core.Audio;
using GeminiLiveShare.Core.BrowserAgent;
using GeminiLiveShare.Core.BrowserAgent.Models;
using GeminiLiveShare.Core.Storage;
using GeminiLiveShare.Core.Vision;
using System.Text.Json;
using Windows.Graphics.Imaging;
using System.Threading.Channels;

namespace GeminiLiveShare.Core.Gemini;

public sealed class SessionOrchestrator : IAsyncDisposable
{
    // Two 20 ms frames cap stale microphone audio at roughly 40 ms when a video
    // frame briefly occupies the WebSocket send lock. Fresh speech is preferable
    // to replaying old speech after a transient transport delay.
    private const int MicrophoneQueueCapacity = 2;
    private static readonly TimeSpan SpeakingSilenceThreshold = TimeSpan.FromMilliseconds(350);

    private readonly IAudioCaptureService _audioCapture;
    private readonly IAudioPlaybackService _audioPlayback;
    private readonly IGeminiLiveClient _liveClient;
    private readonly IScreenCaptureService _screenCapture;
    private readonly IImageProcessingService _imageProcessing;
    private readonly IChatHistoryRepository _chatHistory;
    private readonly BrowserAgentBridge? _browserAgentBridge;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private CancellationTokenSource? _sessionCancellation;
    private CancellationTokenSource? _mediaCancellation;
    private CancellationTokenSource? _videoCancellation;
    private Channel<byte[]>? _microphoneAudio;
    private Task? _microphoneSendTask;
    private Task? _videoTask;
    private CancellationTokenSource? _speakingCancellation;
    private string? _sessionId;
    private bool _microphoneDesired;
    private bool _screenShareDesired;
    private string? _apiKey;
    private bool _resettingVisualContext;

    public SessionOrchestrator(
        IAudioCaptureService audioCapture,
        IAudioPlaybackService audioPlayback,
        IGeminiLiveClient liveClient,
        IScreenCaptureService screenCapture,
        IImageProcessingService imageProcessing,
        IChatHistoryRepository chatHistory,
        BrowserAgentBridge? browserAgentBridge = null)
    {
        _audioCapture = audioCapture;
        _audioPlayback = audioPlayback;
        _liveClient = liveClient;
        _screenCapture = screenCapture;
        _imageProcessing = imageProcessing;
        _chatHistory = chatHistory;
        _browserAgentBridge = browserAgentBridge;
        _audioCapture.AudioCaptured += OnAudioCaptured;
        _liveClient.AudioReceived += OnAudioReceived;
        _liveClient.TurnCompleted += OnTurnCompleted;
        _liveClient.Interrupted += OnInterrupted;
        _liveClient.StatusChanged += OnClientStatusChanged;
        _liveClient.TranscriptionReceived += OnTranscriptionReceived;
        _liveClient.ConnectionAvailabilityChanged += OnConnectionAvailabilityChanged;
        if (_browserAgentBridge is not null)
        {
            _browserAgentBridge.EventReceived += OnBrowserAgentEventReceived;
        }
        _audioCapture.CaptureFailed += OnCaptureFailed;
    }

    public event EventHandler<string>? StatusChanged;

    public event EventHandler? MicrophoneStateChanged;

    public event EventHandler? ScreenShareStateChanged;

    public event EventHandler? SessionStateChanged;

    public event EventHandler? SpeakingStateChanged;

    public event EventHandler? ConnectionStateChanged;

    public bool IsRunning { get; private set; }

    public bool IsMicrophoneOn { get; private set; }

    public bool IsScreenShareOn { get; private set; }

    public bool IsSpeaking { get; private set; }

    public bool IsConnected => _liveClient.IsConnected;

    public bool IsConnecting { get; private set; }

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
                SetConnectingState(true);
                _apiKey = apiKey;
                _sessionId = Guid.NewGuid().ToString("N");
                await _liveClient.ConnectAsync(apiKey, cancellationToken).ConfigureAwait(false);
                _audioPlayback.Start();
                _sessionCancellation = sessionCancellation;
                SetRunningState(true);
                _microphoneDesired = true;
                _screenShareDesired = false;
                SetMicrophoneState(true);
                StartMedia();
                StatusChanged?.Invoke(this, "Conversation started");
            }
            catch
            {
                SetRunningState(false);
                _microphoneDesired = false;
                _screenShareDesired = false;
                SetMicrophoneState(false);
                _audioCapture.Stop();
                sessionCancellation.Cancel();
                await StopMediaAsync().ConfigureAwait(false);
                _audioPlayback.Stop();
                await _liveClient.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
                _sessionCancellation = null;
                _sessionId = null;
                sessionCancellation.Dispose();
                throw;
            }
            finally
            {
                SetConnectingState(false);
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

            SetRunningState(false);
            StopSpeaking();
            _microphoneDesired = false;
            _screenShareDesired = false;
            SetMicrophoneState(false);
            _sessionCancellation?.Cancel();
            _audioCapture.Stop();
            await StopMediaAsync().ConfigureAwait(false);
            _audioPlayback.Clear();
            _audioPlayback.Stop();
            await _liveClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            _sessionCancellation?.Dispose();
            _sessionCancellation = null;
            _sessionId = null;
            _apiKey = null;
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
                _microphoneDesired = true;
                CancellationToken mediaToken = _mediaCancellation?.Token ?? CancellationToken.None;
                StartMicrophoneSender(mediaToken);
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
            _microphoneDesired = false;
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

    public async Task SetScreenShareEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning)
            {
                return;
            }

            _screenShareDesired = enabled;
            if (enabled)
            {
                if (!IsScreenShareOn && _liveClient.IsConnected && _mediaCancellation is not null)
                {
                    StartVideoSender(_mediaCancellation.Token);
                    StatusChanged?.Invoke(this, "Screen share ON");
                }

                return;
            }

            // Publish the off state immediately. StopVideoSenderAsync still waits for the
            // capture loop to unwind, but no new frame is allowed past this point.
            SetScreenShareState(false);
            await StopVideoSenderAsync().ConfigureAwait(false);
            if (_liveClient.IsConnected && !string.IsNullOrWhiteSpace(_apiKey))
            {
                await ResetVisualContextAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (_liveClient.IsConnected)
            {
                try
                {
                    await _liveClient.SendTextAsync(
                        "Screen sharing is now disabled. Treat all earlier screen frames as unavailable. " +
                        "If the user asks about anything visual, say exactly: I don't see your screen right now; " +
                        "I'm not receiving any visuals.", cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    StatusChanged?.Invoke(this, $"Screen sharing is off, but its state update could not be sent: {ex.Message}");
                }
            }
            StatusChanged?.Invoke(this, "Screen share OFF");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task ResetVisualContextAsync(CancellationToken cancellationToken)
    {
        // A Live session retains previously sent images. Reconnecting without a
        // resumption handle is the only reliable way to make screen-off private.
        _resettingVisualContext = true;
        try
        {
            await StopMediaAsync().ConfigureAwait(false);
            await _liveClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            await _liveClient.ConnectAsync(_apiKey!, cancellationToken).ConfigureAwait(false);
            StartMedia();
            await _liveClient.SendTextAsync(
                "Screen sharing is disabled. This session contains no visual input. " +
                "If the user asks about anything visual, say exactly: I don't see your screen right now; " +
                "I'm not receiving any visuals.", cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(this, "Screen share OFF; visual context cleared");
        }
        finally
        {
            _resettingVisualContext = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _audioCapture.AudioCaptured -= OnAudioCaptured;
        _audioCapture.CaptureFailed -= OnCaptureFailed;
        _liveClient.AudioReceived -= OnAudioReceived;
        _liveClient.TurnCompleted -= OnTurnCompleted;
        _liveClient.Interrupted -= OnInterrupted;
        _liveClient.StatusChanged -= OnClientStatusChanged;
        _liveClient.TranscriptionReceived -= OnTranscriptionReceived;
        _liveClient.ConnectionAvailabilityChanged -= OnConnectionAvailabilityChanged;
        if (_browserAgentBridge is not null)
        {
            _browserAgentBridge.EventReceived -= OnBrowserAgentEventReceived;
        }
        _audioCapture.Dispose();
        _audioPlayback.Dispose();
        await _screenCapture.DisposeAsync().ConfigureAwait(false);
        await _liveClient.DisposeAsync().ConfigureAwait(false);
        await _chatHistory.DisposeAsync().ConfigureAwait(false);
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

    private void OnAudioReceived(object? sender, byte[] audio)
    {
        if (!IsRunning || audio.Length == 0)
        {
            return;
        }

        _audioPlayback.Play(audio);
        SetSpeakingState(true);
        RestartSpeakingSilenceTimer();
    }

    private void OnTurnCompleted(object? sender, EventArgs e) => _audioPlayback.CompleteResponse();

    private void OnInterrupted(object? sender, EventArgs e)
    {
        StopSpeaking();
        _audioPlayback.Clear();
        StatusChanged?.Invoke(this, "Gemini response interrupted by user speech");
    }

    private void OnClientStatusChanged(object? sender, string status) => StatusChanged?.Invoke(this, status);

    private async void OnTranscriptionReceived(object? sender, TranscriptionEventArgs e)
    {
        string? sessionId = _sessionId;
        if (sessionId is null || string.IsNullOrWhiteSpace(e.Text) ||
            e.Text.Trim().Equals("hello", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await _chatHistory.AddAsync(new ChatMessage
            {
                SessionId = sessionId,
                Role = e.Role,
                Text = e.Text,
                CreatedAtUtc = DateTime.UtcNow
            }).ConfigureAwait(false);
            if (e.Role.Equals("user", StringComparison.OrdinalIgnoreCase) && IsPageContextRequest(e.Text))
            {
                await SendBrowserPageContextAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Unable to save chat transcript: {ex.Message}");
        }
    }

    private void OnBrowserAgentEventReceived(object? sender, BrowserAgentEventArgs e)
    {
        if (e.Payload.TryGetProperty("code", out JsonElement code) &&
            code.GetString() == "page_context_request")
        {
            _ = SendBrowserPageContextAsync();
        }
    }

    private async Task SendBrowserPageContextAsync()
    {
        if (!IsRunning || !IsConnected || _browserAgentBridge is null)
        {
            return;
        }

        try
        {
            using JsonDocument emptyArguments = JsonDocument.Parse("{}");
            ToolCallResult page = await _browserAgentBridge
                .SendToolCallAsync("get_active_page", emptyArguments.RootElement)
                .ConfigureAwait(false);
            ToolCallResult fields = await _browserAgentBridge
                .SendToolCallAsync("get_form_fields", emptyArguments.RootElement)
                .ConfigureAwait(false);
            string context = "Browser page context was explicitly requested by the user. " +
                "Use only the supplied URL, title, and field metadata. Do not invent fields or values. " +
                "Password fields are intentionally omitted. Button fields are controls, not fillable text fields.\n" +
                $"Active page: {page.Payload.GetRawText()}\nForm fields: {fields.Payload.GetRawText()}";
            await _liveClient.SendTextAsync(context).ConfigureAwait(false);
            StatusChanged?.Invoke(this, "Browser page context sent to Gemini");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Unable to fetch browser page context: {ex.Message}");
        }
    }

    private static bool IsPageContextRequest(string text)
    {
        string normalized = text.Trim().ToLowerInvariant();
        return normalized.Contains("look at this page", StringComparison.Ordinal) ||
            normalized.Contains("what fields are on this form", StringComparison.Ordinal) ||
            normalized.Contains("what fields are on the form", StringComparison.Ordinal) ||
            normalized.Contains("tell me what fields are on it", StringComparison.Ordinal);
    }

    private async void OnConnectionAvailabilityChanged(object? sender, ConnectionAvailabilityChangedEventArgs e)
    {
        ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
        if (_resettingVisualContext)
        {
            return;
        }
        // The initial connection becomes available while StartAsync still owns the lifecycle lock.
        // StartAsync starts media itself, so do not queue a duplicate start behind that lock.
        if (!IsRunning)
        {
            return;
        }

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRunning)
            {
                return;
            }

            if (!e.IsAvailable)
            {
                StopSpeaking();
                _audioCapture.Stop();
                SetMicrophoneState(false);
                await StopMediaAsync().ConfigureAwait(false);
                _audioPlayback.Clear();
                return;
            }

            StartMedia();
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Unable to restore media after reconnect: {ex.Message}");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private void SetConnectingState(bool isConnecting)
    {
        if (IsConnecting == isConnecting)
        {
            return;
        }

        IsConnecting = isConnecting;
        ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

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
            FullMode = BoundedChannelFullMode.DropOldest
        };
        Channel<byte[]> microphoneAudio = Channel.CreateBounded<byte[]>(options);
        _microphoneAudio = microphoneAudio;
        _microphoneSendTask = SendMicrophoneAudioAsync(microphoneAudio.Reader, cancellationToken);
    }

    private void StartMedia()
    {
        _mediaCancellation?.Dispose();
        _mediaCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _sessionCancellation?.Token ?? CancellationToken.None);
        CancellationToken mediaToken = _mediaCancellation.Token;
        if (_microphoneDesired)
        {
            StartMicrophoneSender(mediaToken);
            SetMicrophoneState(true);
            _audioCapture.Start();
        }
        if (_screenShareDesired)
        {
            StartVideoSender(mediaToken);
        }
    }

    private async Task StopMediaAsync()
    {
        _mediaCancellation?.Cancel();
        await StopMicrophoneSenderAsync().ConfigureAwait(false);
        await StopVideoSenderAsync().ConfigureAwait(false);
        _mediaCancellation?.Dispose();
        _mediaCancellation = null;
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
        _videoCancellation?.Dispose();
        _videoCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken videoToken = _videoCancellation.Token;
        // This task is intentionally independent from capture/audio sending. JPEG work never runs on
        // the audio callback and can neither await nor apply backpressure to the microphone channel.
        _videoTask = Task.Run(() => RunVideoSenderAsync(videoToken), videoToken);
        SetScreenShareState(true);
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
        finally
        {
            SetScreenShareState(false);
        }
    }

    private async Task ProcessAndSendVideoFrameAsync(SoftwareBitmap frame, CancellationToken cancellationToken)
    {
        if (!_screenShareDesired)
        {
            return;
        }

        string? base64Jpeg = await _imageProcessing.EncodeForGeminiAsync(frame, cancellationToken).ConfigureAwait(false);
        if (base64Jpeg is null || !_screenShareDesired)
        {
            return;
        }

        if (!_screenShareDesired)
        {
            return;
        }

        await _liveClient.SendVideoFrameAsync(base64Jpeg, cancellationToken).ConfigureAwait(false);
    }

    private async Task StopVideoSenderAsync()
    {
        _videoCancellation?.Cancel();
        Task? videoTask = _videoTask;
        _videoTask = null;
        if (videoTask is null)
        {
            _videoCancellation?.Dispose();
            _videoCancellation = null;
            SetScreenShareState(false);
            return;
        }

        try
        {
            await videoTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _videoCancellation?.Dispose();
        _videoCancellation = null;
        SetScreenShareState(false);
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

    private void SetScreenShareState(bool isOn)
    {
        if (IsScreenShareOn == isOn)
        {
            return;
        }

        IsScreenShareOn = isOn;
        ScreenShareStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetRunningState(bool isRunning)
    {
        if (IsRunning == isRunning)
        {
            return;
        }

        IsRunning = isRunning;
        SessionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RestartSpeakingSilenceTimer()
    {
        CancellationTokenSource cancellation = new();
        CancellationTokenSource? previous = Interlocked.Exchange(ref _speakingCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        _ = ClearSpeakingAfterSilenceAsync(cancellation);
    }

    private async Task ClearSpeakingAfterSilenceAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(SpeakingSilenceThreshold, cancellation.Token).ConfigureAwait(false);
            if (Interlocked.CompareExchange(ref _speakingCancellation, null, cancellation) == cancellation)
            {
                SetSpeakingState(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private void StopSpeaking()
    {
        CancellationTokenSource? cancellation = Interlocked.Exchange(ref _speakingCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        SetSpeakingState(false);
    }

    private void SetSpeakingState(bool isSpeaking)
    {
        if (IsSpeaking == isSpeaking)
        {
            return;
        }

        IsSpeaking = isSpeaking;
        SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
