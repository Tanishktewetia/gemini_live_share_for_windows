using GeminiLiveShare.Core.Audio;
using GeminiLiveShare.Core.BrowserAgent;
using GeminiLiveShare.Core.BrowserAgent.Models;
using GeminiLiveShare.Core.Diagnostics;
using GeminiLiveShare.Core.Interop;
using GeminiLiveShare.Core.Desktop;
using GeminiLiveShare.Core.Storage;
using GeminiLiveShare.Core.Vision;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using System.Threading.Channels;
using SkiaSharp;

namespace GeminiLiveShare.Core.Gemini;

public sealed class SessionOrchestrator : IAsyncDisposable
{
    // Microphone chunks are 40 ms. Screen frames share the WebSocket send lock, and a frame upload measured
    // 65-380 ms on a home connection; the previous 2-chunk (80 ms) queue discarded 11% of speech while frames
    // uploaded, which Gemini heard as broken words. ~520 ms rides out a normal frame upload with a short delay
    // instead of lost speech, while still bounding latency after a real network stall.
    private const int MicrophoneQueueCapacity = 13;
    private static readonly TimeSpan DiagnosticsSummaryInterval = TimeSpan.FromSeconds(30);
    // Adaptive frame quality: slow uploads delay microphone audio, so shrink frames until the link recovers.
    private static readonly TimeSpan SlowFrameUpload = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan FastFrameUpload = TimeSpan.FromMilliseconds(150);
    private const int MinimumJpegQuality = 60;
    private const int JpegQualityStep = 10;
    private const int FastUploadsBeforeRaisingQuality = 5;
    private static readonly TimeSpan SpeakingSilenceThreshold = TimeSpan.FromMilliseconds(350);

    private readonly IAudioCaptureService _audioCapture;
    private readonly IAudioPlaybackService _audioPlayback;
    private readonly IGeminiLiveClient _liveClient;
    private readonly IScreenCaptureService _screenCapture;
    private readonly IImageProcessingService _imageProcessing;
    private readonly IChatHistoryRepository _chatHistory;
    private readonly BrowserAgentBridge? _browserAgentBridge;
    private readonly ISessionDiagnostics _diagnostics;
    private readonly IDiagnosticsDebugSettings _diagnosticsSettings;
    private readonly ConversationStateRebuilder _conversationStateRebuilder;
    private readonly IDesktopAutomationService _desktopAutomation;
    private readonly IZoomVisionService _zoomVisionService;
    private readonly IHighlightOverlayService _highlightOverlay;
    private readonly IWebSearchService _webSearchService;
    private readonly HighlightSettings _highlightSettings;
    private readonly object _latestFrameLock = new();
    private TaskCompletionSource<bool>? _freshFrameAfterSpeech;
    private byte[]? _latestFullResolutionJpeg;
    private int _latestFrameWidth;
    private int _latestFrameHeight;
    private long _micChunksCaptured;
    private long _micChunksDropped;
    private long _framesSent;
    private long _framesUnchanged;
    private long _framesDropped;
    private long _frameBytesSent;
    private long _frameUploadTicksTotal;
    private long _frameUploadTicksMax;
    private int _fastUploadStreak;
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
    private int _screenShareNoticePending;
    private int _restoreConversationStatePending;
    private bool _isWebSearchAvailable = true;
    private string _webSearchMode = "Unknown";
    private bool _hasReconnected;
    private bool _browserPageContextAttached;
    private readonly object _desktopExpectationLock = new();
    private readonly object _recentCountLock = new();
    private PendingDesktopExpectation? _pendingDesktopExpectation;
    private RecentCountContext? _recentCountContext;
    private readonly object _assistantWatchdogLock = new();
    private CancellationTokenSource? _assistantWatchdogCancellation;
    private string? _pendingAudibleReplyUserPrompt;
    private bool _assistantAudioReceivedForPendingPrompt;
    private bool _assistantSpeaking;
    private bool _userSpeaking;
    private bool _assistantTurnAwaitingToolResponse;
    private int _toolCallsInFlight;
    private DateTimeOffset _lastSilentRecoveryUtc = DateTimeOffset.MinValue;
    private static readonly Regex NumberRegex = new(@"\b(\d+)\b", RegexOptions.Compiled);
    private static readonly Regex ZoomCellRegex = new(@"^[A-D](?:[1-4])$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly TimeSpan RecentCountIntentLifetime = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan AssistantAudioWatchdogDelay = TimeSpan.FromSeconds(4);
    // UI Automation, zoom and regular-model search can each take longer than the
    // normal audio start window. Tool calls are not silent turns, so only start this
    // longer watchdog after the tool response has been sent.
    private static readonly TimeSpan AssistantAudioAfterToolWatchdogDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan AssistantSilentRecoveryCooldown = TimeSpan.FromSeconds(12);

    internal const string ScreenShareOnNotice =
        "App notice (not spoken by the user): screen sharing is now ON. You are receiving screenshots of the user's " +
        "primary monitor about once per second and may describe what they show. Briefly confirm that you can now see the screen.";

    public SessionOrchestrator(
        IAudioCaptureService audioCapture,
        IAudioPlaybackService audioPlayback,
        IGeminiLiveClient liveClient,
        IScreenCaptureService screenCapture,
        IImageProcessingService imageProcessing,
        IChatHistoryRepository chatHistory,
        BrowserAgentBridge? browserAgentBridge = null,
        ISessionDiagnostics? diagnostics = null,
        IDiagnosticsDebugSettings? diagnosticsSettings = null,
        IDesktopAutomationService? desktopAutomation = null,
        IZoomVisionService? zoomVisionService = null,
        IHighlightOverlayService? highlightOverlay = null,
        IWebSearchService? webSearchService = null,
        HighlightSettings? highlightSettings = null)
    {
        _diagnostics = diagnostics ?? NullSessionDiagnostics.Instance;
        _diagnosticsSettings = diagnosticsSettings ?? new DiagnosticsDebugSettings();
        _conversationStateRebuilder = new ConversationStateRebuilder(chatHistory, browserAgentBridge);
        _desktopAutomation = desktopAutomation ?? new DesktopAutomationService();
        _zoomVisionService = zoomVisionService ?? new GeminiZoomVisionService();
        _highlightOverlay = highlightOverlay ?? NullHighlightOverlayService.Instance;
        _webSearchService = webSearchService ?? new GeminiWebSearchService();
        _highlightSettings = highlightSettings ?? new HighlightSettings();
        StatusChanged += (_, status) => _diagnostics.Log($"status: {status}");
        _audioCapture = audioCapture;
        _audioPlayback = audioPlayback;
        _liveClient = liveClient;
        _screenCapture = screenCapture;
        _imageProcessing = imageProcessing;
        _chatHistory = chatHistory;
        _browserAgentBridge = browserAgentBridge;
        _audioCapture.AudioCaptured += OnAudioCaptured;
        _audioCapture.UserSpeakingChanged += OnUserSpeakingChanged;
        _liveClient.AudioReceived += OnAudioReceived;
        _liveClient.TurnCompleted += OnTurnCompleted;
        _liveClient.Interrupted += OnInterrupted;
        _liveClient.StatusChanged += OnClientStatusChanged;
        _liveClient.TranscriptionReceived += OnTranscriptionReceived;
        _liveClient.ConnectionAvailabilityChanged += OnConnectionAvailabilityChanged;
        _liveClient.SessionReady += OnSessionReady;
        _liveClient.ToolCallsReceived += OnToolCallsReceived;
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

    public bool IsWebSearchAvailable => _isWebSearchAvailable;

    public string WebSearchMode => _webSearchMode;

    public bool HasReconnected => _hasReconnected;

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
                ResetDiagnosticsCounters();
                _diagnostics.Log("session: starting");
                _diagnostics.Log($"diagnostics: save-sent-frames {(_diagnosticsSettings.SaveSentFrames ? "on" : "off")}; path {_diagnosticsSettings.SentFramesDirectory}");
                _apiKey = apiKey;
                _sessionId = Guid.NewGuid().ToString("N");
                _isWebSearchAvailable = true;
                _webSearchMode = "Unknown";
                _hasReconnected = false;
                _browserPageContextAttached = false;
                ClearPendingDesktopExpectation();
                ClearRecentCountIntent();
                ClearLatestZoomFrame();
                Interlocked.Exchange(ref _restoreConversationStatePending, 0);
                await _liveClient.ConnectAsync(apiKey, cancellationToken).ConfigureAwait(false);
                _audioPlayback.Start();
                _sessionCancellation = sessionCancellation;
                SetRunningState(true);
                _microphoneDesired = true;
                _screenShareDesired = false;
                SetMicrophoneState(true);
                StartMedia();
                _ = LogDiagnosticsPeriodicallyAsync(sessionCancellation.Token);
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
            CancelAssistantAudioWatchdog();
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
            _isWebSearchAvailable = true;
            _webSearchMode = "Unknown";
            _hasReconnected = false;
            _browserPageContextAttached = false;
            ClearPendingDesktopExpectation();
            ClearRecentCountIntent();
            ClearLatestZoomFrame();
            await ClearHighlightAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _restoreConversationStatePending, 0);
            LogDiagnosticsSummary("session end");
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

                StatusChanged?.Invoke(this, $"Microphone ON ({DescribeEchoCancellation()})");
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
            ClearLatestZoomFrame();
            await ClearHighlightAsync().ConfigureAwait(false);
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
                        "If the user asks about anything visual, reply: " + GeminiLiveClient.NoScreenReply, cancellationToken).ConfigureAwait(false);
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
                "If the user asks about anything visual, reply: " + GeminiLiveClient.NoScreenReply, cancellationToken).ConfigureAwait(false);
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
        _audioCapture.UserSpeakingChanged -= OnUserSpeakingChanged;
        _audioCapture.CaptureFailed -= OnCaptureFailed;
        _liveClient.AudioReceived -= OnAudioReceived;
        _liveClient.TurnCompleted -= OnTurnCompleted;
        _liveClient.Interrupted -= OnInterrupted;
        _liveClient.StatusChanged -= OnClientStatusChanged;
        _liveClient.TranscriptionReceived -= OnTranscriptionReceived;
        _liveClient.ConnectionAvailabilityChanged -= OnConnectionAvailabilityChanged;
        _liveClient.SessionReady -= OnSessionReady;
        _liveClient.ToolCallsReceived -= OnToolCallsReceived;
        if (_browserAgentBridge is not null)
        {
            _browserAgentBridge.EventReceived -= OnBrowserAgentEventReceived;
        }
        _audioCapture.Dispose();
        _audioPlayback.Dispose();
        await _screenCapture.DisposeAsync().ConfigureAwait(false);
        await _liveClient.DisposeAsync().ConfigureAwait(false);
        await _highlightOverlay.DisposeAsync().ConfigureAwait(false);
        await _chatHistory.DisposeAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }

    private void OnAudioCaptured(object? sender, byte[] audio)
    {
        if (!IsRunning || !IsMicrophoneOn)
        {
            return;
        }

        Interlocked.Increment(ref _micChunksCaptured);
        _microphoneAudio?.Writer.TryWrite(audio);
    }

    private void OnUserSpeakingChanged(object? sender, bool speaking)
    {
        _userSpeaking = speaking;
        SetSpeakingState(_assistantSpeaking || _userSpeaking);
    }

    private void OnAudioReceived(object? sender, byte[] audio)
    {
        if (!IsRunning || audio.Length == 0)
        {
            return;
        }

        MarkAssistantAudioArrived();
        _audioPlayback.Play(audio);
        SetSpeakingState(true);
        RestartSpeakingSilenceTimer();
    }

    private void OnTurnCompleted(object? sender, EventArgs e)
    {
        _audioPlayback.CompleteResponse();
        _ = RecoverSilentAssistantTurnIfNeededAsync("turn-complete-without-audio");
    }

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
            if (e.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                ClearPendingDesktopExpectation();
                ForceFreshFrameAfterUserSpeech();
                BeginAssistantAudioWatchdog(e.Text);
                await _chatHistory.AddAsync(new ChatMessage
                {
                    SessionId = sessionId,
                    Role = e.Role,
                    Text = e.Text,
                    CreatedAtUtc = DateTime.UtcNow
                }).ConfigureAwait(false);

                if (await TryHandleDeterministicCountIntentAsync(sessionId, e.Text).ConfigureAwait(false))
                {
                    return;
                }

                await SendDesktopIntentContextIfNeededAsync(e.Text).ConfigureAwait(false);
                if (IsPageContextRequest(e.Text))
                {
                    await SendBrowserPageContextAsync().ConfigureAwait(false);
                }

                return;
            }

            if (e.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                await ShouldRequestAssistantCorrectionAsync(e.Text).ConfigureAwait(false))
            {
                // Ignore this low-confidence or duplicate assistant text and force/suppress correction.
                return;
            }

            await _chatHistory.AddAsync(new ChatMessage
            {
                SessionId = sessionId,
                Role = e.Role,
                Text = e.Text,
                CreatedAtUtc = DateTime.UtcNow
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Unable to save chat transcript: {ex.Message}");
        }
    }
    private void ForceFreshFrameAfterUserSpeech()
    {
        if (!_screenShareDesired || !IsScreenShareOn)
        {
            return;
        }

        lock (_latestFrameLock)
        {
            _freshFrameAfterSpeech?.TrySetCanceled();
            _freshFrameAfterSpeech = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _imageProcessing.ForceSendNextFrame();
    }
    private async Task SendDesktopIntentContextIfNeededAsync(string userText)
    {
        if (!IsConnected || !TryBuildDesktopIntentContext(userText, out string context))
        {
            return;
        }

        await _liveClient.SendTextAsync(context, _sessionCancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);
        StatusChanged?.Invoke(this, "Desktop automation context sent (authoritative)");
    }

    private async Task<bool> TryHandleDeterministicCountIntentAsync(string sessionId, string userText)
    {
        if (!TryResolveCountIntentTarget(userText, out CountIntentTarget target))
        {
            return false;
        }

        if (!IsVisualContextReadyForDeterministicDesktopAnswer())
        {
            await _liveClient.SendTextAsync(
                "Screen visual context is not active yet. Reply exactly with: " + GeminiLiveClient.NoScreenReply,
                _sessionCancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);
            _diagnostics.Log("count intent blocked: no active visual context");
            StatusChanged?.Invoke(this, "Count request deferred: screen sharing not active yet");
            return true;
        }

        CountReplyOutcome outcome = target switch
        {
            CountIntentTarget.DesktopIcons => BuildDesktopCountReply(),
            CountIntentTarget.TaskbarAppIcons => BuildTaskbarCountReply(),
            _ => throw new InvalidOperationException("Unsupported count intent target.")
        };

        // Speak the deterministic answer through Gemini audio (overlay-first UX) instead of only writing chat text.
        string speakPrompt =
            "Authoritative app result (highest priority). Reply in exactly one short sentence and speak it aloud. " +
            "Do not add extra wording. Say exactly: \"" + outcome.Reply.Replace("\"", "'") + "\"";
        await _liveClient.SendTextAsync(speakPrompt, _sessionCancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);

        if (outcome.Expectation is not null)
        {
            SetPendingDesktopExpectation(outcome.Expectation);
        }

        SetRecentCountIntent(target);
        _diagnostics.Log(outcome.Diagnostics);
        StatusChanged?.Invoke(this, "Count sent to Gemini for spoken deterministic reply");
        return true;
    }

    private bool IsVisualContextReadyForDeterministicDesktopAnswer() =>
        IsScreenShareOn && Volatile.Read(ref _screenShareNoticePending) == 0;
    private CountReplyOutcome BuildDesktopCountReply()
    {
        DesktopIconCountSnapshot count = _desktopAutomation.GetDesktopIconCount();
        if (!count.IsReliable)
        {
            return new CountReplyOutcome(
                Reply:
                    "I can't verify an exact desktop icon count right now because desktop automation could not read FolderView reliably. " +
                    "Please keep the desktop unobstructed and ask again.",
                Diagnostics:
                    $"count desktop: unreliable strategy={count.SourceStrategy}, source={count.SourceItemCount}, visible-ui={count.VisibleUiItemCount}, hidden-or-filtered={count.HiddenOrFilteredCount}, reason={count.ReliabilityNote}",
                Expectation: new PendingDesktopExpectation(
                    DesktopExpectationType.DesktopIconCount,
                    count.Count,
                    null,
                    DateTimeOffset.UtcNow,
                    -1));
        }

        return new CountReplyOutcome(
            Reply:
                $"There are exactly {count.Count} desktop icons. " +
                "Policy: count all items in desktop FolderView (shell item count when available, visible UIA list items otherwise).",
            Diagnostics:
                $"count desktop: reliable=yes, strategy={count.SourceStrategy}, source={count.SourceItemCount}, visible-ui={count.VisibleUiItemCount}, hidden-or-filtered={count.HiddenOrFilteredCount}, final={count.Count}",
            Expectation: new PendingDesktopExpectation(
                DesktopExpectationType.DesktopIconCount,
                count.Count,
                null,
                DateTimeOffset.UtcNow,
                0));
    }

    private CountReplyOutcome BuildTaskbarCountReply()
    {
        TaskbarItemCountSnapshot count = _desktopAutomation.GetTaskbarItemCount();
        if (!count.IsReliable)
        {
            return new CountReplyOutcome(
                Reply:
                    "I can't verify an exact taskbar app-icon count right now because taskbar app buttons were not detected reliably. " +
                    "Please ask again in a moment.",
                Diagnostics:
                    $"count taskbar: unreliable strategy={count.SourceStrategy}, app-buttons={count.AppButtons}, tray-buttons={count.TrayButtons}, system-buttons={count.SystemButtons}, reason={count.ReliabilityNote}",
                Expectation: new PendingDesktopExpectation(
                    DesktopExpectationType.TaskbarItemCount,
                    count.Count,
                    null,
                    DateTimeOffset.UtcNow,
                    -1));
        }

        return new CountReplyOutcome(
            Reply:
                $"There are exactly {count.Count} taskbar app icons. " +
                "Policy: count app buttons only; Start, Search, Widgets, system tray, clock, and overflow are excluded.",
            Diagnostics:
                $"count taskbar: reliable=yes, strategy={count.SourceStrategy}, app-buttons={count.AppButtons}, tray-buttons={count.TrayButtons}, system-buttons={count.SystemButtons}, final={count.Count}",
            Expectation: new PendingDesktopExpectation(
                DesktopExpectationType.TaskbarItemCount,
                count.Count,
                null,
                DateTimeOffset.UtcNow,
                0));
    }
    private bool TryResolveCountIntentTarget(string userText, out CountIntentTarget target)
    {
        string normalized = userText.Trim().ToLowerInvariant();
        bool containsDesktop = normalized.Contains("desktop", StringComparison.Ordinal);
        bool containsTaskbar = normalized.Contains("taskbar", StringComparison.Ordinal);
        bool containsIconCue = normalized.Contains("icon", StringComparison.Ordinal) ||
            normalized.Contains("icons", StringComparison.Ordinal) ||
            normalized.Contains("app", StringComparison.Ordinal) ||
            normalized.Contains("item", StringComparison.Ordinal);
        bool countCue = normalized.Contains("how many", StringComparison.Ordinal) ||
            normalized.Contains("count", StringComparison.Ordinal) ||
            normalized.Contains("number", StringComparison.Ordinal) ||
            normalized.Contains("total", StringComparison.Ordinal);
        bool followUpCue = normalized.Contains("are you sure", StringComparison.Ordinal) ||
            normalized.Contains("just tell", StringComparison.Ordinal) ||
            normalized.Contains("again", StringComparison.Ordinal) ||
            normalized.Contains("still", StringComparison.Ordinal) ||
            normalized.Contains("only", StringComparison.Ordinal);

        if (!countCue && !followUpCue)
        {
            target = default;
            return false;
        }

        if (containsTaskbar && normalized.Contains("only", StringComparison.Ordinal))
        {
            target = CountIntentTarget.TaskbarAppIcons;
            return true;
        }

        if (containsDesktop && normalized.Contains("excluding taskbar", StringComparison.Ordinal))
        {
            target = CountIntentTarget.DesktopIcons;
            return true;
        }

        if (containsDesktop && !containsTaskbar)
        {
            target = CountIntentTarget.DesktopIcons;
            return true;
        }

        if (containsTaskbar && !containsDesktop)
        {
            target = CountIntentTarget.TaskbarAppIcons;
            return true;
        }

        if ((containsDesktop || containsTaskbar || containsIconCue || followUpCue) &&
            TryGetRecentCountIntent(out CountIntentTarget recent))
        {
            target = recent;
            return true;
        }

        target = default;
        return false;
    }
    private bool TryBuildDesktopIntentContext(string userText, out string context)
    {
        string normalized = userText.Trim().ToLowerInvariant();

        if (normalized.Contains("where") && (normalized.Contains("pointer") || normalized.Contains("cursor")))
        {
            DesktopElementSnapshot? element = _desktopAutomation.GetElementUnderCursor();
            context = element is null
                ? "Authoritative desktop automation result: pointer element could not be resolved. Say you cannot verify pointer location exactly right now."
                : "Authoritative desktop automation result (high priority): " +
                  $"pointer is over '{element.Name}' ({element.ControlType}) at bounds " +
                  $"x={element.Bounds.X}, y={element.Bounds.Y}, width={element.Bounds.Width}, height={element.Bounds.Height}. " +
                  "Answer from this exact data and do not guess.";
            return true;
        }

        context = string.Empty;
        return false;
    }

    private async Task<bool> ShouldRequestAssistantCorrectionAsync(string assistantText)
    {
        PendingDesktopExpectation? expectation = GetPendingDesktopExpectation();
        if (expectation is null)
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - expectation.CreatedAtUtc > TimeSpan.FromSeconds(20))
        {
            ClearPendingDesktopExpectation();
            return false;
        }

        if (expectation.Type is DesktopExpectationType.DesktopIconCount or DesktopExpectationType.TaskbarItemCount)
        {
            // correctionsIssued < 0 means the app already answered deterministically from local automation;
            // suppress the next model count reply to avoid contradictory duplicates in history.
            if (expectation.CorrectionsIssued < 0)
            {
                if (NumberRegex.IsMatch(assistantText))
                {
                    ClearPendingDesktopExpectation();
                    return true;
                }

                ClearPendingDesktopExpectation();
                return false;
            }

            Match match = NumberRegex.Match(assistantText);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int actual) || actual != expectation.ExpectedCount)
            {
                if (expectation.CorrectionsIssued >= 2)
                {
                    ClearPendingDesktopExpectation();
                    return false;
                }

                PendingDesktopExpectation updated = expectation with { CorrectionsIssued = expectation.CorrectionsIssued + 1 };
                SetPendingDesktopExpectation(updated);
                await _liveClient.SendTextAsync(
                    "Correction (authoritative desktop data): your previous answer was not exact. " +
                    $"The exact count is {expectation.ExpectedCount}. " +
                    "Reply again using that exact count only, with no estimate words.",
                    _sessionCancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);
                StatusChanged?.Invoke(this, "Assistant answer corrected using desktop automation");
                return true;
            }

            ClearPendingDesktopExpectation();
            return false;
        }

        ClearPendingDesktopExpectation();
        return false;
    }
    private void SetPendingDesktopExpectation(PendingDesktopExpectation expectation)
    {
        lock (_desktopExpectationLock)
        {
            _pendingDesktopExpectation = expectation;
        }
    }

    private PendingDesktopExpectation? GetPendingDesktopExpectation()
    {
        lock (_desktopExpectationLock)
        {
            return _pendingDesktopExpectation;
        }
    }

    private void ClearPendingDesktopExpectation()
    {
        lock (_desktopExpectationLock)
        {
            _pendingDesktopExpectation = null;
        }
    }

    private void SetRecentCountIntent(CountIntentTarget target)
    {
        lock (_recentCountLock)
        {
            _recentCountContext = new RecentCountContext(target, DateTimeOffset.UtcNow);
        }
    }

    private bool TryGetRecentCountIntent(out CountIntentTarget target)
    {
        lock (_recentCountLock)
        {
            if (_recentCountContext is null ||
                DateTimeOffset.UtcNow - _recentCountContext.TimestampUtc > RecentCountIntentLifetime)
            {
                _recentCountContext = null;
                target = default;
                return false;
            }

            target = _recentCountContext.Target;
            return true;
        }
    }

    private void ClearRecentCountIntent()
    {
        lock (_recentCountLock)
        {
            _recentCountContext = null;
        }
    }

    private void BeginAssistantAudioWatchdog(string userPrompt)
    {
        CancellationTokenSource watchdog = new();
        lock (_assistantWatchdogLock)
        {
            _assistantWatchdogCancellation?.Cancel();
            _assistantWatchdogCancellation?.Dispose();
            _assistantWatchdogCancellation = watchdog;
            _pendingAudibleReplyUserPrompt = userPrompt;
            _assistantAudioReceivedForPendingPrompt = false;
        }

        _ = WatchForMissingAssistantAudioAsync(watchdog);
    }

    private void MarkAssistantAudioArrived()
    {
        lock (_assistantWatchdogLock)
        {
            _assistantAudioReceivedForPendingPrompt = true;
            _assistantWatchdogCancellation?.Cancel();
            _assistantWatchdogCancellation?.Dispose();
            _assistantWatchdogCancellation = null;
            _pendingAudibleReplyUserPrompt = null;
        }
    }

    private void CancelAssistantAudioWatchdog()
    {
        lock (_assistantWatchdogLock)
        {
            _assistantWatchdogCancellation?.Cancel();
            _assistantWatchdogCancellation?.Dispose();
            _assistantWatchdogCancellation = null;
            _pendingAudibleReplyUserPrompt = null;
            _assistantAudioReceivedForPendingPrompt = false;
        }
    }

    private async Task WatchForMissingAssistantAudioAsync(CancellationTokenSource watchdog, TimeSpan? delay = null)
    {
        try
        {
            await Task.Delay(delay ?? AssistantAudioWatchdogDelay, watchdog.Token).ConfigureAwait(false);
            await RecoverSilentAssistantTurnIfNeededAsync("no-audio-watchdog").ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RecoverSilentAssistantTurnIfNeededAsync(string reason)
    {
        string? pendingPrompt;
        lock (_assistantWatchdogLock)
        {
            if (_assistantAudioReceivedForPendingPrompt || _assistantTurnAwaitingToolResponse || _toolCallsInFlight > 0 || !IsRunning || !IsConnected)
            {
                return;
            }

            if (DateTimeOffset.UtcNow - _lastSilentRecoveryUtc < AssistantSilentRecoveryCooldown)
            {
                return;
            }

            pendingPrompt = _pendingAudibleReplyUserPrompt;
            _lastSilentRecoveryUtc = DateTimeOffset.UtcNow;
        }

        if (string.IsNullOrWhiteSpace(pendingPrompt) || string.IsNullOrWhiteSpace(_apiKey))
        {
            return;
        }

        if (_screenShareDesired || IsScreenShareOn)
        {
            _diagnostics.Log($"audio watchdog: suppressed recovery during screen share ({reason})");
            StatusChanged?.Invoke(this, "Assistant audio was quiet; keeping screen share connected.");
            return;
        }

        StatusChanged?.Invoke(this, "No assistant audio detected; recovering session...");
        _diagnostics.Log($"audio watchdog: recovering from silent assistant turn ({reason})");

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsRunning || string.IsNullOrWhiteSpace(_apiKey))
            {
                return;
            }

            await StopMediaAsync().ConfigureAwait(false);
            await _liveClient.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            await _liveClient.ConnectAsync(_apiKey!, CancellationToken.None).ConfigureAwait(false);
            StartMedia();
            await _liveClient.SendTextAsync(
                "The previous response was not audible to the user. " +
                "Please answer now in one short sentence and speak it clearly. User request: " + pendingPrompt,
                _sessionCancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);
            StatusChanged?.Invoke(this, "Recovered: asked Gemini to repeat audibly");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Audio recovery failed: {ex.Message}");
            _diagnostics.Log($"audio watchdog: recovery failed: {ex.Message}");
        }
        finally
        {
            _lifecycleLock.Release();
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

    private async void OnToolCallsReceived(object? sender, ToolCallsEventArgs e)
    {
        if (!IsRunning || !IsConnected || e.Calls.Count == 0)
        {
            return;
        }

        try
        {
            PauseAssistantAudioWatchdogForToolCalls(e.Calls.Count);
            _diagnostics.Log($"tool calls received: {string.Join(", ", e.Calls.Select(call => call.Name))}");
            List<ToolResponsePayload> responses = new(e.Calls.Count);
            foreach (ToolCallRequest call in e.Calls)
            {
                long toolStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                JsonElement response = await ExecuteToolCallAsync(call, _sessionCancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);
                double toolMs = System.Diagnostics.Stopwatch.GetElapsedTime(toolStarted).TotalMilliseconds;
                _diagnostics.Log($"tool execution completed: name={call.Name}, elapsed={toolMs:0}ms");
                responses.Add(new ToolResponsePayload(call.Id, call.Name, response));
                LogToolResponse(call.Name, response);
            }

            await _liveClient.SendToolResponseAsync(responses, _sessionCancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);
            StatusChanged?.Invoke(this, $"Tool response sent ({responses.Count} call(s))");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Unable to execute tool call: {ex.Message}");
        }
        finally
        {
            ResumeAssistantAudioWatchdogAfterToolCalls(e.Calls.Count);
        }
    }

    private async Task<JsonElement> ExecuteToolCallAsync(ToolCallRequest call, CancellationToken cancellationToken)
    {
        try
        {
            if (call.Name.Equals("zoom_region", StringComparison.Ordinal))
            {
                return await ExecuteZoomRegionToolCallAsync(call, cancellationToken).ConfigureAwait(false);
            }

            if (call.Name.Equals("highlight_element", StringComparison.Ordinal))
            {
                return await ExecuteHighlightElementToolCallAsync(call, cancellationToken).ConfigureAwait(false);
            }

            if (call.Name.Equals("web_search", StringComparison.Ordinal))
            {
                return await ExecuteWebSearchToolCallAsync(call, cancellationToken).ConfigureAwait(false);
            }

            return ExecuteDesktopToolCall(call);
        }
        catch (Exception ex)
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = ex.Message });
        }
    }

    private async Task<JsonElement> ExecuteHighlightElementToolCallAsync(ToolCallRequest call, CancellationToken cancellationToken)
    {
        if (!_highlightSettings.IsEnabled)
        {
            return JsonSerializer.SerializeToElement(new
            {
                ok = false, found = false, disabled = true,
                error = "Visual highlighting is disabled in Settings. Do not claim that a highlight was shown."
            });
        }

        if (!TryParseHighlightRequest(call.Args, out string name, out string? role, out string? location, out string parseError))
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = parseError });
        }

        long lookupStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        IReadOnlyList<DesktopItemSnapshot> matches = _desktopAutomation.FindElementsByNameRole(name, role, location);
        double lookupMs = System.Diagnostics.Stopwatch.GetElapsedTime(lookupStarted).TotalMilliseconds;
        _diagnostics.Log($"highlight lookup completed: elapsed={lookupMs:0}ms, matches={matches.Count}, location={(string.IsNullOrWhiteSpace(location) ? "foreground" : location)}");
        string source = "ui_automation";

        if (matches.Count == 0)
        {
            return JsonSerializer.SerializeToElement(new
            {
                ok = false,
                found = false,
                error = string.IsNullOrWhiteSpace(location) || (!location.Equals("desktop", StringComparison.OrdinalIgnoreCase) && !location.Equals("taskbar", StringComparison.OrdinalIgnoreCase))
                    ? $"No visible enabled element named '{name}' was found in the focused foreground window. Ask the user to click/focus the intended application window, then try again. Do not search or highlight another window."
                    : $"No visible enabled element named '{name}' was found. Ask the user to move the pointer over the target."
            });
        }

        DesktopItemSnapshot selected = matches[0];
        await _highlightOverlay.ShowAsync(
            selected.Bounds,
            $"Click: {selected.Name}",
            TimeSpan.FromSeconds(8),
            cancellationToken).ConfigureAwait(false);

        if (!_highlightOverlay.IsVisible)
        {
            return JsonSerializer.SerializeToElement(new
            {
                ok = false,
                found = true,
                error = "The target was found, but the highlight overlay did not become visible. Do not claim that it was highlighted."
            });
        }

        return JsonSerializer.SerializeToElement(new
        {
            ok = true,
            found = true,
            source,
            ambiguous = matches.Count > 1,
            location = string.IsNullOrWhiteSpace(location) ? null : location,
            matchCount = matches.Count,
            overlayVisible = _highlightOverlay.IsVisible,
            selected = new
            {
                name = selected.Name,
                role = selected.ControlType,
                bounds = new
                {
                    x = selected.Bounds.X,
                    y = selected.Bounds.Y,
                    width = selected.Bounds.Width,
                    height = selected.Bounds.Height
                }
            },
            instruction = matches.Count > 1
                ? "Several matches were found. The likeliest match is highlighted; ask the user whether this is the one before they click."
                : "The control is highlighted. Tell the user to click it; do not click it yourself."
        });
    }

    private async Task<JsonElement> ExecuteWebSearchToolCallAsync(ToolCallRequest call, CancellationToken cancellationToken)
    {
        string query = call.Args.TryGetProperty("query", out JsonElement queryElement)
            ? queryElement.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = "web_search requires a non-empty query." });
        }

        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = "web_search is unavailable before session startup." });
        }

        try
        {
            WebSearchResult result = await _webSearchService
                .SearchAsync(_apiKey, query, cancellationToken)
                .ConfigureAwait(false);
            _diagnostics.Log($"web search tool completed: provider={result.Provider}, reliable={(result.IsReliable ? "yes" : "no")}, sources={result.Sources.Count}");
            return JsonSerializer.SerializeToElement(new
            {
                ok = true,
                reliable = result.IsReliable,
                query,
                summary = result.Summary,
                sources = result.Sources,
                provider = result.Provider
            });
        }
        catch (Exception ex)
        {
            _diagnostics.Log($"web search tool failed: {ex.Message}");
            return JsonSerializer.SerializeToElement(new
            {
                ok = false,
                error = "The web search request failed. Say that current web search is temporarily unavailable and do not guess.",
                detail = ex.Message.Length > 240 ? ex.Message[..240] : ex.Message
            });
        }
    }

    private static bool TryParseHighlightRequest(JsonElement args, out string name, out string? role, out string? location, out string error)
    {
        name = args.TryGetProperty("name", out JsonElement nameElement)
            ? nameElement.GetString()?.Trim() ?? string.Empty
            : string.Empty;
        role = args.TryGetProperty("role", out JsonElement roleElement)
            ? roleElement.GetString()?.Trim()
            : null;
        location = args.TryGetProperty("location", out JsonElement locationElement)
            ? locationElement.GetString()?.Trim()
            : null;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "highlight_element requires a non-empty name.";
            return false;
        }

        return true;
    }

    private void PauseAssistantAudioWatchdogForToolCalls(int callCount)
    {
        lock (_assistantWatchdogLock)
        {
            _toolCallsInFlight += callCount;
            _assistantTurnAwaitingToolResponse = true;
            _assistantWatchdogCancellation?.Cancel();
            _assistantWatchdogCancellation?.Dispose();
            _assistantWatchdogCancellation = null;
        }

        _diagnostics.Log($"audio watchdog: paused for {callCount} tool call(s)");
    }

    private void ResumeAssistantAudioWatchdogAfterToolCalls(int callCount)
    {
        CancellationTokenSource? watchdog = null;
        lock (_assistantWatchdogLock)
        {
            _toolCallsInFlight = Math.Max(0, _toolCallsInFlight - callCount);
            if (_toolCallsInFlight != 0)
            {
                return;
            }

            _assistantTurnAwaitingToolResponse = false;
            if (IsRunning && IsConnected &&
                !_assistantAudioReceivedForPendingPrompt &&
                !string.IsNullOrWhiteSpace(_pendingAudibleReplyUserPrompt))
            {
                watchdog = new CancellationTokenSource();
                _assistantWatchdogCancellation?.Cancel();
                _assistantWatchdogCancellation?.Dispose();
                _assistantWatchdogCancellation = watchdog;
            }
        }

        if (watchdog is not null)
        {
            _diagnostics.Log($"audio watchdog: waiting {AssistantAudioAfterToolWatchdogDelay.TotalSeconds:0}s after tool response");
            _ = WatchForMissingAssistantAudioAsync(watchdog, AssistantAudioAfterToolWatchdogDelay);
        }
    }

    private void LogToolResponse(string toolName, JsonElement response)
    {
        bool ok = response.TryGetProperty("ok", out JsonElement okElement) && okElement.ValueKind == JsonValueKind.True;
        string detail = response.TryGetProperty("error", out JsonElement error)
            ? TrimDiagnosticValue(error.GetString())
            : toolName switch
            {
                "highlight_element" => BuildHighlightDiagnostic(response),
                "web_search" => BuildSearchDiagnostic(response),
                "zoom_region" => BuildZoomDiagnostic(response),
                _ => string.Empty
            };
        _diagnostics.Log($"tool response: name={toolName}, ok={(ok ? "yes" : "no")}{(string.IsNullOrWhiteSpace(detail) ? string.Empty : ", " + detail)}");
    }

    private static string BuildHighlightDiagnostic(JsonElement response)
    {
        int matchCount = response.TryGetProperty("matchCount", out JsonElement count) && count.TryGetInt32(out int value) ? value : 0;
        if (!response.TryGetProperty("selected", out JsonElement selected) ||
            !selected.TryGetProperty("bounds", out JsonElement bounds))
        {
            return $"matches={matchCount}";
        }

        return $"matches={matchCount}, bounds={FormatBounds(bounds)}";
    }

    private static string BuildSearchDiagnostic(JsonElement response) =>
        $"provider={(response.TryGetProperty("provider", out JsonElement provider) ? provider.GetString() : "unknown")}, reliable={(response.TryGetProperty("reliable", out JsonElement reliable) && reliable.ValueKind == JsonValueKind.True ? "yes" : "no")}, sources={(response.TryGetProperty("sources", out JsonElement sources) && sources.ValueKind == JsonValueKind.Array ? sources.GetArrayLength() : 0)}";

    private static string BuildZoomDiagnostic(JsonElement response) =>
        response.TryGetProperty("bounds", out JsonElement bounds)
            ? $"bounds={FormatBounds(bounds)}"
            : string.Empty;

    private static string FormatBounds(JsonElement bounds)
    {
        if (bounds.TryGetProperty("x", out JsonElement x) && bounds.TryGetProperty("y", out JsonElement y) &&
            bounds.TryGetProperty("width", out JsonElement width) && bounds.TryGetProperty("height", out JsonElement height))
        {
            return $"{x.GetInt32()}:{y.GetInt32()}:{width.GetInt32()}x{height.GetInt32()}";
        }

        return "unknown";
    }

    private static string TrimDiagnosticValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "error=unknown" : $"error={(value.Length > 160 ? value[..160] : value)}";

    private async Task ClearHighlightAsync()
    {
        if (!_highlightOverlay.IsVisible)
        {
            return;
        }

        try
        {
            await _highlightOverlay.ClearAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics.Log($"highlight: unable to clear overlay: {ex.Message}");
        }
    }

    private JsonElement ExecuteDesktopToolCall(ToolCallRequest call) => call.Name switch
    {
        "get_element_under_cursor" => JsonSerializer.SerializeToElement(BuildElementUnderCursorPayload()),
        "list_taskbar_items" => JsonSerializer.SerializeToElement(BuildTaskbarItemsPayload()),
        "list_desktop_icons" => JsonSerializer.SerializeToElement(BuildDesktopIconsPayload()),
        "get_focused_window" => JsonSerializer.SerializeToElement(BuildFocusedWindowPayload()),
        _ => JsonSerializer.SerializeToElement(new { ok = false, error = $"Unknown tool: {call.Name}" })
    };

    private async Task<JsonElement> ExecuteZoomRegionToolCallAsync(ToolCallRequest call, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = "zoom_region is unavailable before session startup." });
        }

        if (!TryParseZoomRequest(call.Args, out ZoomRequest request, out string parseError))
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = parseError });
        }

        if (!await WaitForFreshFrameAfterSpeechAsync(cancellationToken).ConfigureAwait(false))
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = "A fresh screenshot was not ready. Ask the user to keep screen share on and try again." });
        }

        if (!TryGetLatestZoomFrame(out byte[] fullResolutionJpeg, out int frameWidth, out int frameHeight))
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = "No screenshot is available yet. Ask the user to keep screen share on and try again." });
        }

        if (!TryResolveZoomBounds(request, frameWidth, frameHeight, out SKRectI bounds, out string boundsError))
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = boundsError });
        }

        byte[] croppedJpeg;
        try
        {
            croppedJpeg = CropJpeg(fullResolutionJpeg, bounds);
        }
        catch (Exception ex)
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = $"Unable to crop zoom region: {ex.Message}" });
        }

        string answer = await _zoomVisionService.AnalyzeAsync(_apiKey!, croppedJpeg, request.Question, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.SerializeToElement(new
        {
            ok = true,
            source = "zoom_region",
            answer,
            bounds = new { x = bounds.Left, y = bounds.Top, width = bounds.Width, height = bounds.Height },
            frame = new { width = frameWidth, height = frameHeight }
        });
    }

    private static byte[] CropJpeg(byte[] fullResolutionJpeg, SKRectI bounds)
    {
        using SKBitmap source = SKBitmap.Decode(fullResolutionJpeg)
            ?? throw new InvalidOperationException("The latest screenshot could not be decoded.");
        using SKBitmap cropped = new(bounds.Width, bounds.Height, source.ColorType, source.AlphaType);
        using (SKCanvas canvas = new(cropped))
        {
            canvas.DrawBitmap(source, bounds, new SKRect(0, 0, bounds.Width, bounds.Height));
        }

        using SKImage image = SKImage.FromBitmap(cropped);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return encoded.ToArray();
    }

    private bool TryGetLatestZoomFrame(out byte[] fullResolutionJpeg, out int frameWidth, out int frameHeight)
    {
        lock (_latestFrameLock)
        {
            if (_latestFullResolutionJpeg is null || _latestFrameWidth <= 0 || _latestFrameHeight <= 0)
            {
                fullResolutionJpeg = [];
                frameWidth = 0;
                frameHeight = 0;
                return false;
            }

            fullResolutionJpeg = _latestFullResolutionJpeg.ToArray();
            frameWidth = _latestFrameWidth;
            frameHeight = _latestFrameHeight;
            return true;
        }
    }

    private void StoreLatestZoomFrame(FrameEncodeResult encoded)
    {
        if (encoded.FullResolutionJpeg is null || encoded.FullResolutionWidth <= 0 || encoded.FullResolutionHeight <= 0)
        {
            return;
        }

        lock (_latestFrameLock)
        {
            _latestFullResolutionJpeg = encoded.FullResolutionJpeg.ToArray();
            _latestFrameWidth = encoded.FullResolutionWidth;
            _latestFrameHeight = encoded.FullResolutionHeight;
        }
    }

    private void ClearLatestZoomFrame()
    {
        lock (_latestFrameLock)
        {
            _freshFrameAfterSpeech?.TrySetCanceled();
            _freshFrameAfterSpeech = null;
            _latestFullResolutionJpeg = null;
            _latestFrameWidth = 0;
            _latestFrameHeight = 0;
        }
    }

    private void SignalFreshFrameAfterSpeech()
    {
        lock (_latestFrameLock)
        {
            _freshFrameAfterSpeech?.TrySetResult(true);
            _freshFrameAfterSpeech = null;
        }
    }

    private async Task<bool> WaitForFreshFrameAfterSpeechAsync(CancellationToken cancellationToken)
    {
        Task? pending;
        lock (_latestFrameLock)
        {
            pending = _freshFrameAfterSpeech?.Task;
        }

        if (pending is null)
        {
            return true;
        }

        try
        {
            await pending.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static bool TryParseZoomRequest(JsonElement args, out ZoomRequest request, out string error)
    {
        request = new ZoomRequest(string.Empty, null, null);
        error = string.Empty;

        string question = args.TryGetProperty("question", out JsonElement questionElement)
            ? questionElement.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(question))
        {
            error = "zoom_region requires a non-empty question.";
            return false;
        }

        if (args.TryGetProperty("box", out JsonElement boxElement) && boxElement.ValueKind == JsonValueKind.Object)
        {
            if (!TryGetInt(boxElement, "x", out int x) || !TryGetInt(boxElement, "y", out int y) ||
                !TryGetInt(boxElement, "width", out int width) || !TryGetInt(boxElement, "height", out int height))
            {
                error = "zoom_region box must contain integer x, y, width and height.";
                return false;
            }

            request = new ZoomRequest(question.Trim(), null, new SKRectI(x, y, x + width, y + height));
            return true;
        }

        if (args.TryGetProperty("cells", out JsonElement cellsElement) && cellsElement.ValueKind == JsonValueKind.Array)
        {
            string[] cells = cellsElement.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => (element.GetString() ?? string.Empty).Trim().ToUpperInvariant())
                .Where(cell => cell.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (cells.Length == 0)
            {
                error = "zoom_region cells must contain at least one value like A1 or C3.";
                return false;
            }

            if (cells.Any(cell => !ZoomCellRegex.IsMatch(cell)))
            {
                error = "zoom_region cells must be in the A1-D4 grid.";
                return false;
            }

            request = new ZoomRequest(question.Trim(), cells, null);
            return true;
        }

        error = "zoom_region requires either cells (A1-D4) or box (x,y,width,height).";
        return false;
    }

    private static bool TryResolveZoomBounds(ZoomRequest request, int frameWidth, int frameHeight, out SKRectI bounds, out string error)
    {
        bounds = default;
        error = string.Empty;

        if (request.Box is { } box)
        {
            if (box.Width <= 0 || box.Height <= 0)
            {
                error = "zoom_region box width and height must be positive.";
                return false;
            }

            SKRectI clamped = SKRectI.Intersect(box, new SKRectI(0, 0, frameWidth, frameHeight));
            if (clamped.Width <= 0 || clamped.Height <= 0)
            {
                error = "zoom_region box is outside the captured screen.";
                return false;
            }

            bounds = clamped;
            return true;
        }

        if (request.Cells is not { Length: > 0 })
        {
            error = "zoom_region cells are missing.";
            return false;
        }

        int minColumn = int.MaxValue;
        int minRow = int.MaxValue;
        int maxColumn = int.MinValue;
        int maxRow = int.MinValue;
        foreach (string cell in request.Cells)
        {
            int column = char.ToUpperInvariant(cell[0]) - 'A';
            int row = cell[1] - '1';
            minColumn = Math.Min(minColumn, column);
            minRow = Math.Min(minRow, row);
            maxColumn = Math.Max(maxColumn, column);
            maxRow = Math.Max(maxRow, row);
        }

        int cellWidth = Math.Max(1, frameWidth / 4);
        int cellHeight = Math.Max(1, frameHeight / 4);
        int left = minColumn * cellWidth;
        int top = minRow * cellHeight;
        int right = maxColumn == 3 ? frameWidth : (maxColumn + 1) * cellWidth;
        int bottom = maxRow == 3 ? frameHeight : (maxRow + 1) * cellHeight;
        bounds = SKRectI.Intersect(new SKRectI(left, top, right, bottom), new SKRectI(0, 0, frameWidth, frameHeight));
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            error = "zoom_region cells resolved to an empty area.";
            return false;
        }

        return true;
    }

    private static bool TryGetInt(JsonElement element, string property, out int value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out JsonElement propertyElement))
        {
            return false;
        }

        return propertyElement.ValueKind == JsonValueKind.Number && propertyElement.TryGetInt32(out value);
    }

    private sealed record ZoomRequest(string Question, string[]? Cells, SKRectI? Box);
    private object BuildElementUnderCursorPayload()
    {
        DesktopElementSnapshot? element = _desktopAutomation.GetElementUnderCursor();
        return element is null
            ? new { ok = true, found = false }
            : new
            {
                ok = true,
                found = true,
                element = new
                {
                    name = element.Name,
                    controlType = element.ControlType,
                    parentPath = element.ParentPath,
                    bounds = new
                    {
                        x = element.Bounds.X,
                        y = element.Bounds.Y,
                        width = element.Bounds.Width,
                        height = element.Bounds.Height
                    }
                }
            };
    }

    private object BuildDesktopIconsPayload()
    {
        DesktopIconCountSnapshot snapshot = _desktopAutomation.GetDesktopIconCount();
        return new
        {
            ok = true,
            count = snapshot.Count,
            sourceItemCount = snapshot.SourceItemCount,
            visibleUiItemCount = snapshot.VisibleUiItemCount,
            hiddenOrFilteredCount = snapshot.HiddenOrFilteredCount,
            reliable = snapshot.IsReliable,
            reliabilityNote = snapshot.ReliabilityNote,
            strategy = snapshot.SourceStrategy,
            policy = snapshot.SourcePolicy,
            items = snapshot.VisibleItems.Select(item => new
            {
                name = item.Name,
                controlType = item.ControlType,
                bounds = new
                {
                    x = item.Bounds.X,
                    y = item.Bounds.Y,
                    width = item.Bounds.Width,
                    height = item.Bounds.Height
                }
            }).ToArray()
        };
    }

    private object BuildTaskbarItemsPayload()
    {
        TaskbarItemCountSnapshot snapshot = _desktopAutomation.GetTaskbarItemCount();
        return new
        {
            ok = true,
            count = snapshot.Count,
            appButtons = snapshot.AppButtons,
            trayButtons = snapshot.TrayButtons,
            systemButtons = snapshot.SystemButtons,
            reliable = snapshot.IsReliable,
            reliabilityNote = snapshot.ReliabilityNote,
            strategy = snapshot.SourceStrategy,
            policy = snapshot.SourcePolicy,
            items = snapshot.AppItems.Select(item => new
            {
                name = item.Name,
                controlType = item.ControlType,
                bounds = new
                {
                    x = item.Bounds.X,
                    y = item.Bounds.Y,
                    width = item.Bounds.Width,
                    height = item.Bounds.Height
                }
            }).ToArray()
        };
    }
    private object BuildFocusedWindowPayload()
    {
        FocusedWindowSnapshot? window = _desktopAutomation.GetFocusedWindow();
        return window is null
            ? new { ok = true, found = false }
            : new
            {
                ok = true,
                found = true,
                appName = window.AppName,
                title = window.Title,
                focusedElement = window.FocusedElement is null
                    ? null
                    : new
                    {
                        name = window.FocusedElement.Name,
                        controlType = window.FocusedElement.ControlType,
                        parentPath = window.FocusedElement.ParentPath,
                        bounds = new
                        {
                            x = window.FocusedElement.Bounds.X,
                            y = window.FocusedElement.Bounds.Y,
                            width = window.FocusedElement.Bounds.Width,
                            height = window.FocusedElement.Bounds.Height
                        }
                    }
            };
    }

    private void OnSessionReady(object? sender, SessionReadyEventArgs e)
    {
        _isWebSearchAvailable = e.IsWebSearchAvailable;
        _webSearchMode = e.WebSearchMode;
        if (e.IsReconnect)
        {
            _hasReconnected = true;
            if (!e.WasSessionResumed)
            {
                Interlocked.Exchange(ref _restoreConversationStatePending, 1);
                _diagnostics.Log("reconnect: resumed=no; scheduling conversation context restore");
            }
            else
            {
                _diagnostics.Log("reconnect: resumed=yes; context restore not needed");
            }
        }

        _diagnostics.Log($"session setup: reconnect={(e.IsReconnect ? "yes" : "no")}, resumptionAttempt={(e.AttemptedResumption ? "yes" : "no")}, resumed={(e.WasSessionResumed ? "yes" : "no")}, web-search={e.WebSearchMode}");
        ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
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
                CancelAssistantAudioWatchdog();
                _audioCapture.Stop();
                SetMicrophoneState(false);
                await StopMediaAsync().ConfigureAwait(false);
                _audioPlayback.Clear();
                return;
            }

            StartMedia();
            if (Interlocked.Exchange(ref _restoreConversationStatePending, 0) == 1)
            {
                await RestoreConversationStateAfterReconnectAsync().ConfigureAwait(false);
            }
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
        Channel<byte[]> microphoneAudio = Channel.CreateBounded<byte[]>(
            options, _ => Interlocked.Increment(ref _micChunksDropped));
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
            StatusChanged?.Invoke(this, $"Microphone ON ({DescribeEchoCancellation()})");
        }
        if (_screenShareDesired)
        {
            StartVideoSender(mediaToken);
        }
    }

    private string DescribeEchoCancellation() => _audioCapture.IsEchoCancellationActive
        ? "echo cancellation active"
        : "echo cancellation unavailable - use headphones";

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
        // Tell Gemini sharing is on only after a real screenshot has been sent, so it never "sees" before an image exists.
        Interlocked.Exchange(ref _screenShareNoticePending, 1);
        _imageProcessing.ResetChangeDetection();
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

        FrameEncodeResult encoded = await _imageProcessing.EncodeForGeminiAsync(frame, cancellationToken).ConfigureAwait(false);
        if (encoded.Status == FrameEncodeStatus.Unchanged)
        {
            Interlocked.Increment(ref _framesUnchanged);
            return;
        }

        if (encoded.Status == FrameEncodeStatus.Dropped || encoded.Base64Jpeg is null)
        {
            Interlocked.Increment(ref _framesDropped);
            return;
        }

        if (!_screenShareDesired)
        {
            return;
        }

        // Normal screen-share frames must not shorten a highlight lifetime.

        StoreLatestZoomFrame(encoded);
        SignalFreshFrameAfterSpeech();

        long uploadStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        await _liveClient.SendVideoFrameAsync(encoded.Base64Jpeg, cancellationToken).ConfigureAwait(false);
        RecordFrameUpload(System.Diagnostics.Stopwatch.GetElapsedTime(uploadStarted), encoded.JpegBytes);
        SaveSentFrameForDiagnostics(encoded);
        if (Interlocked.Exchange(ref _screenShareNoticePending, 0) == 1 && _screenShareDesired)
        {
            await _liveClient.SendTextAsync(ScreenShareOnNotice, cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(this, "Screen share ON: first screenshot sent to Gemini");
        }
    }

    private void SaveSentFrameForDiagnostics(FrameEncodeResult encoded)
    {
        if (!_diagnosticsSettings.SaveSentFrames || encoded.Base64Jpeg is null)
        {
            return;
        }

        string? sessionId = _sessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        try
        {
            byte[] jpeg = Convert.FromBase64String(encoded.Base64Jpeg);
            long frameNumber = Interlocked.Read(ref _framesSent);
            _diagnostics.SaveSentFrame(sessionId, frameNumber, jpeg);
        }
        catch (Exception ex)
        {
            _diagnostics.Log($"diagnostics: unable to save sent frame: {ex.Message}");
        }
    }

    private async Task RestoreConversationStateAfterReconnectAsync()
    {
        string? sessionId = _sessionId;
        if (string.IsNullOrWhiteSpace(sessionId) || !_liveClient.IsConnected)
        {
            return;
        }

        try
        {
            string? context = await _conversationStateRebuilder
                .BuildReconnectContextAsync(
                    sessionId,
                    _screenShareDesired,
                    _browserPageContextAttached,
                    _sessionCancellation?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(context))
            {
                _diagnostics.Log("reconnect: no prior context to restore");
                return;
            }

            await _liveClient.SendTextAsync(context, _sessionCancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);
            StatusChanged?.Invoke(this, "Reconnected: restored recent conversation context");
            _diagnostics.Log("reconnect: restored recent conversation context");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Reconnected but could not restore context: {ex.Message}");
            _diagnostics.Log($"reconnect: context restore failed: {ex.Message}");
        }
    }
    private enum DesktopExpectationType
    {
        DesktopIconCount,
        TaskbarItemCount
    }

    private enum CountIntentTarget
    {
        DesktopIcons,
        TaskbarAppIcons
    }

    private sealed record RecentCountContext(CountIntentTarget Target, DateTimeOffset TimestampUtc);

    private sealed record CountReplyOutcome(
        string Reply,
        string Diagnostics,
        PendingDesktopExpectation? Expectation);

    private sealed record PendingDesktopExpectation(
        DesktopExpectationType Type,
        int ExpectedCount,
        string? ExpectedName,
        DateTimeOffset CreatedAtUtc,
        int CorrectionsIssued);

    private void RecordFrameUpload(TimeSpan upload, int jpegBytes)
    {
        Interlocked.Increment(ref _framesSent);
        Interlocked.Add(ref _frameBytesSent, jpegBytes);
        Interlocked.Add(ref _frameUploadTicksTotal, upload.Ticks);
        long max;
        while (upload.Ticks > (max = Interlocked.Read(ref _frameUploadTicksMax)) &&
               Interlocked.CompareExchange(ref _frameUploadTicksMax, upload.Ticks, max) != max)
        {
        }

        int quality = _imageProcessing.JpegQuality;
        if (upload >= SlowFrameUpload)
        {
            _fastUploadStreak = 0;
            if (quality > MinimumJpegQuality)
            {
                _imageProcessing.JpegQuality = Math.Max(MinimumJpegQuality, quality - JpegQualityStep);
                _diagnostics.Log($"video: slow frame upload {upload.TotalMilliseconds:0} ms ({jpegBytes / 1024} KB); JPEG quality {quality} -> {_imageProcessing.JpegQuality}");
            }
        }
        else if (upload <= FastFrameUpload)
        {
            if (quality < ImageProcessingService.DefaultJpegQuality && ++_fastUploadStreak >= FastUploadsBeforeRaisingQuality)
            {
                _fastUploadStreak = 0;
                _imageProcessing.JpegQuality = Math.Min(ImageProcessingService.DefaultJpegQuality, quality + JpegQualityStep);
                _diagnostics.Log($"video: uploads fast again; JPEG quality {quality} -> {_imageProcessing.JpegQuality}");
            }
        }
        else
        {
            _fastUploadStreak = 0;
        }
    }

    private void ResetDiagnosticsCounters()
    {
        Interlocked.Exchange(ref _micChunksCaptured, 0);
        Interlocked.Exchange(ref _micChunksDropped, 0);
        Interlocked.Exchange(ref _framesSent, 0);
        Interlocked.Exchange(ref _framesUnchanged, 0);
        Interlocked.Exchange(ref _framesDropped, 0);
        Interlocked.Exchange(ref _frameBytesSent, 0);
        Interlocked.Exchange(ref _frameUploadTicksTotal, 0);
        Interlocked.Exchange(ref _frameUploadTicksMax, 0);
        _fastUploadStreak = 0;
        _imageProcessing.JpegQuality = ImageProcessingService.DefaultJpegQuality;
    }

    private async Task LogDiagnosticsPeriodicallyAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(DiagnosticsSummaryInterval, cancellationToken).ConfigureAwait(false);
                LogDiagnosticsSummary("session so far");
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void LogDiagnosticsSummary(string label)
    {
        long captured = Interlocked.Read(ref _micChunksCaptured);
        long dropped = Interlocked.Read(ref _micChunksDropped);
        long sent = Interlocked.Read(ref _framesSent);
        double averageUpload = sent == 0 ? 0 : TimeSpan.FromTicks(Interlocked.Read(ref _frameUploadTicksTotal) / sent).TotalMilliseconds;
        double maxUpload = TimeSpan.FromTicks(Interlocked.Read(ref _frameUploadTicksMax)).TotalMilliseconds;
        long averageKb = sent == 0 ? 0 : Interlocked.Read(ref _frameBytesSent) / sent / 1024;
        double lostPercent = captured == 0 ? 0 : 100.0 * dropped / captured;
        _diagnostics.Log(
            $"{label}: mic chunks {captured}, lost {dropped} ({lostPercent:0.0}%), " +
            $"echo cancellation {(_audioCapture.IsEchoCancellationActive ? "on" : "off")}; " +
            $"frames sent {sent}, unchanged-skipped {Interlocked.Read(ref _framesUnchanged)}, dropped {Interlocked.Read(ref _framesDropped)}, " +
            $"avg {averageKb} KB, upload avg {averageUpload:0} ms max {maxUpload:0} ms, JPEG quality {_imageProcessing.JpegQuality}");
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
            // Gemini sends speech faster than real time, so the last chunk can arrive long before it is heard.
            // Keep the speaking state (overlay animation) on until the queued speech has actually played.
            while (_audioPlayback.HasQueuedAudio)
            {
                await Task.Delay(100, cancellation.Token).ConfigureAwait(false);
            }

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
        _assistantSpeaking = isSpeaking;
        bool combined = _assistantSpeaking || _userSpeaking;
        if (IsSpeaking == combined)
        {
            return;
        }

        IsSpeaking = combined;
        SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
