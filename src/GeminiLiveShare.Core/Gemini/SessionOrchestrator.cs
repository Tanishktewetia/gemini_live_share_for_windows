using GeminiLiveShare.Core.Audio;
using GeminiLiveShare.Core.BrowserAgent;
using GeminiLiveShare.Core.BrowserAgent.Models;
using GeminiLiveShare.Core.Diagnostics;
using GeminiLiveShare.Core.Desktop;
using GeminiLiveShare.Core.Storage;
using GeminiLiveShare.Core.Vision;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using System.Threading.Channels;

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
    private bool _hasReconnected;
    private bool _browserPageContextAttached;
    private readonly object _desktopExpectationLock = new();
    private PendingDesktopExpectation? _pendingDesktopExpectation;
    private static readonly Regex NumberRegex = new(@"\b(\d+)\b", RegexOptions.Compiled);

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
        IDesktopAutomationService? desktopAutomation = null)
    {
        _diagnostics = diagnostics ?? NullSessionDiagnostics.Instance;
        _diagnosticsSettings = diagnosticsSettings ?? new DiagnosticsDebugSettings();
        _conversationStateRebuilder = new ConversationStateRebuilder(chatHistory, browserAgentBridge);
        _desktopAutomation = desktopAutomation ?? new DesktopAutomationService();
        StatusChanged += (_, status) => _diagnostics.Log($"status: {status}");
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
                _hasReconnected = false;
                _browserPageContextAttached = false;
                ClearPendingDesktopExpectation();
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
            _hasReconnected = false;
            _browserPageContextAttached = false;
            ClearPendingDesktopExpectation();
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
            if (e.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                await SendDesktopIntentContextIfNeededAsync(e.Text).ConfigureAwait(false);
            }
            else if (e.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) &&
                     await ShouldRequestAssistantCorrectionAsync(e.Text).ConfigureAwait(false))
            {
                // Ignore this low-confidence assistant text in history and force a corrected reply.
                return;
            }

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

    private async Task SendDesktopIntentContextIfNeededAsync(string userText)
    {
        if (!IsConnected || !TryBuildDesktopIntentContext(userText, out string context, out PendingDesktopExpectation? expectation))
        {
            return;
        }

        if (expectation is not null)
        {
            SetPendingDesktopExpectation(expectation);
        }

        await _liveClient.SendTextAsync(context, _sessionCancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);
        StatusChanged?.Invoke(this, "Desktop automation context sent (authoritative)");
    }

    private bool TryBuildDesktopIntentContext(string userText, out string context, out PendingDesktopExpectation? expectation)
    {
        string normalized = userText.Trim().ToLowerInvariant();
        expectation = null;

        if (normalized.Contains("how many") && normalized.Contains("desktop") && normalized.Contains("icon"))
        {
            IReadOnlyList<DesktopItemSnapshot> icons = _desktopAutomation.ListDesktopIcons();
            int count = icons.Count;
            expectation = new PendingDesktopExpectation(DesktopExpectationType.DesktopIconCount, count, null, DateTimeOffset.UtcNow, 0);
            context =
                "Authoritative desktop automation result (high priority): " +
                $"The exact number of visible desktop icons is {count}. " +
                "For this question, answer with that exact number only. Do not estimate or round.";
            return true;
        }

        if (normalized.Contains("how many") && normalized.Contains("taskbar") && normalized.Contains("icon"))
        {
            IReadOnlyList<DesktopItemSnapshot> items = _desktopAutomation.ListTaskbarItems();
            int count = items.Count;
            expectation = new PendingDesktopExpectation(DesktopExpectationType.TaskbarItemCount, count, null, DateTimeOffset.UtcNow, 0);
            context =
                "Authoritative desktop automation result (high priority): " +
                $"The exact number of visible taskbar items is {count}. " +
                "For this question, answer with that exact number only. Do not estimate or round.";
            return true;
        }

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
            List<ToolResponsePayload> responses = new(e.Calls.Count);
            foreach (ToolCallRequest call in e.Calls)
            {
                responses.Add(new ToolResponsePayload(call.Id, call.Name, ExecuteDesktopToolCall(call)));
            }

            await _liveClient.SendToolResponseAsync(responses, _sessionCancellation?.Token ?? CancellationToken.None).ConfigureAwait(false);
            StatusChanged?.Invoke(this, $"Desktop tool response sent ({responses.Count} call(s))");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(this, $"Unable to execute desktop tool call: {ex.Message}");
        }
    }

    private JsonElement ExecuteDesktopToolCall(ToolCallRequest call)
    {
        try
        {
            return call.Name switch
            {
                "get_element_under_cursor" => JsonSerializer.SerializeToElement(BuildElementUnderCursorPayload()),
                "list_taskbar_items" => JsonSerializer.SerializeToElement(BuildItemsPayload(_desktopAutomation.ListTaskbarItems())),
                "list_desktop_icons" => JsonSerializer.SerializeToElement(BuildItemsPayload(_desktopAutomation.ListDesktopIcons())),
                "get_focused_window" => JsonSerializer.SerializeToElement(BuildFocusedWindowPayload()),
                _ => JsonSerializer.SerializeToElement(new { ok = false, error = $"Unknown desktop tool: {call.Name}" })
            };
        }
        catch (Exception ex)
        {
            return JsonSerializer.SerializeToElement(new { ok = false, error = ex.Message });
        }
    }

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

    private static object BuildItemsPayload(IReadOnlyList<DesktopItemSnapshot> items) => new
    {
        ok = true,
        count = items.Count,
        items = items.Select(item => new
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

        _diagnostics.Log($"session setup: reconnect={(e.IsReconnect ? "yes" : "no")}, resumptionAttempt={(e.AttemptedResumption ? "yes" : "no")}, resumed={(e.WasSessionResumed ? "yes" : "no")}, web-search={(e.IsWebSearchAvailable ? "on" : "off")}");
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
        if (IsSpeaking == isSpeaking)
        {
            return;
        }

        IsSpeaking = isSpeaking;
        SpeakingStateChanged?.Invoke(this, EventArgs.Empty);
    }
}



