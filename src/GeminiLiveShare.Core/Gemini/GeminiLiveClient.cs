using System.Buffers;
using System.Net.WebSockets;
using System.Text.Json;
using GeminiLiveShare.Core.Gemini.Models;

namespace GeminiLiveShare.Core.Gemini;

public sealed class GeminiLiveClient : IGeminiLiveClient
{
    private const string Endpoint = "wss://generativelanguage.googleapis.com/ws/google.ai.generativelanguage.v1beta.GenerativeService.BidiGenerateContent";
    private const string Model = "models/gemini-3.1-flash-live-preview";
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(15);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateLock = new();
    private readonly OutputTranscriptionAccumulator _outputTranscription = new();
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _supervisorTask;
    private string? _resumptionHandle;
    private volatile bool _isConnected;
    // Google Search grounding is attempted first. Some API keys/plans have no quota for it and the server closes the
    // setup with "You exceeded your current quota" even though plain Live sessions work (measured 2026-09-17), so the
    // client falls back to a session without search for the rest of the app run.
    private static volatile bool s_webSearchUnavailable;

    internal const string NoScreenReply =
        "I can't see your screen right now. Turn on screen sharing with the screen button on the overlay if you'd like me to look.";

    // Screen sharing is OFF when every session starts. Earlier wording described a constant screenshot stream plus
    // icon-counting advice, and with zero frames sent the model claimed to see the desktop in 3 of 4 test sessions
    // (inventing windows, apps and icon counts). Visual access is therefore stated as conditional, and the app sends
    // an explicit notice once the first screenshot has actually been sent (SessionOrchestrator.ScreenShareOnNotice).
    internal static string BuildInstruction(bool webSearchAvailable) => DesktopVisionInstruction + "\n\n" +
        (webSearchAvailable
            ? "WEB SEARCH:\n- You can use Google Search. Use it when the user asks you to look something up or when a question " +
              "needs current or factual information about products, companies or websites. Base your answer on the results."
            : "WEB SEARCH:\n- You cannot search the internet in this session. Never say you searched or looked something up. " +
              "If asked to search, say you can't search the internet right now and answer from your own knowledge, saying it may be out of date.") +
        "\n\nNAMES YOU MAY HEAR:\n- Speech recognition often mishears product names. \"Cloud\" or \"Cloud Code\" said about an AI app " +
        "usually means Claude or Claude Code, made by Anthropic. If the user corrects a name, use their correction from then on.";

    internal const string DesktopVisionInstruction =
        "You are GeminiLiveShare, a voice assistant running on the user's Windows PC.\n\n" +
        "SCREEN ACCESS RULES (highest priority):\n" +
        "- Screen sharing is OFF when the conversation starts. While it is off you cannot see anything on the user's computer.\n" +
        "- You can see the screen only after the app tells you screen sharing is on AND you have actually received a screenshot image in this conversation.\n" +
        "- Never pretend or assume you can see the screen. Never describe, guess or invent windows, apps, websites, icons, text or counts you have not received in an image.\n" +
        "- If the user asks about their screen and you have no screenshot, reply: '" + NoScreenReply + "'\n" +
        "- A message saying screen sharing is disabled is authoritative: from then on you have no visuals, even if you saw screenshots earlier.\n\n" +
        "WHEN SCREENSHOTS ARE PRESENT (the images show the user's primary monitor):\n" +
        "- Answer from the newest screenshot. Read text carefully and keep exact spelling, capitalization and numbers; say so if something is not legible instead of guessing.\n" +
        "- Identify an application from visible text (window title, tab title, taskbar or menu labels), not from its layout or colours; " +
        "many apps look alike (for example Claude and VS Code). If no visible text names it, say you are not sure which app it is.\n" +
        "- For icons, use their visible labels and positions. To count desktop icons, scan top to bottom and left to right, count each icon once, " +
        "exclude the taskbar, this app's overlay, window chrome and wallpaper, and say the count is uncertain if the image is not clear enough.\n\n" +
        "BROWSER PAGE CONTEXT:\n" +
        "- Browser page details are available only after an explicit user request such as 'look at this page' or 'what fields are on this form'.\n" +
        "- When supplied, use only the given URL, title and fields; never invent missing values. Password fields are omitted, " +
        "and button fields are controls rather than fillable text fields.";

    public event EventHandler<byte[]>? AudioReceived;
    public event EventHandler? TurnCompleted;
    public event EventHandler? Interrupted;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<TranscriptionEventArgs>? TranscriptionReceived;
    public event EventHandler<ConnectionAvailabilityChangedEventArgs>? ConnectionAvailabilityChanged;
    public event EventHandler<SessionReadyEventArgs>? SessionReady;
    public bool IsConnected => _isConnected;

    public async Task ConnectAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        lock (_stateLock)
        {
            if (_supervisorTask is not null)
            {
                throw new InvalidOperationException("The Live API client is already active.");
            }
        }

        StatusChanged?.Invoke(this, "Connecting");
        _outputTranscription.Clear();
        CancellationTokenSource sessionCancellation = new();
        TaskCompletionSource initialConnection = NewCompletionSource();
        lock (_stateLock)
        {
            _sessionCancellation = sessionCancellation;
            _resumptionHandle = null;
            _supervisorTask = RunConnectionSupervisorAsync(apiKey, initialConnection, sessionCancellation.Token);
        }

        try
        {
            await initialConnection.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            sessionCancellation.Cancel();
            Task? supervisor;
            lock (_stateLock)
            {
                supervisor = _supervisorTask;
            }
            if (supervisor is not null)
            {
                await IgnoreCancellationAsync(supervisor).ConfigureAwait(false);
            }
            ClearSessionState(sessionCancellation);
            StatusChanged?.Invoke(this, "Disconnected");
            throw new InvalidOperationException(GetSafeConnectionError(ex), ex);
        }
    }

    public Task SendAudioAsync(byte[] pcmAudio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pcmAudio);
        if (!IsConnected || pcmAudio.Length == 0)
        {
            return Task.CompletedTask;
        }
        return SendJsonAsync(new RealtimeInputMessage
        {
            RealtimeInput = new RealtimeInput { Audio = new AudioBlob { Data = Convert.ToBase64String(pcmAudio) } }
        }, cancellationToken);
    }

    public Task SendVideoFrameAsync(string base64Jpeg, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Jpeg);
        if (!IsConnected)
        {
            return Task.CompletedTask;
        }
        return SendJsonAsync(new RealtimeInputMessage
        {
            RealtimeInput = new RealtimeInput { Video = new VideoBlob { Data = base64Jpeg } }
        }, cancellationToken);
    }

    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        if (!IsConnected)
        {
            return Task.CompletedTask;
        }

        return SendJsonAsync(new RealtimeInputMessage
        {
            RealtimeInput = new RealtimeInput { Text = text }
        }, cancellationToken);
    }

    public Task SendAudioStreamEndAsync(CancellationToken cancellationToken = default) =>
        IsConnected ? SendJsonAsync(new AudioStreamEndMessage(), cancellationToken) : Task.CompletedTask;

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? sessionCancellation;
        Task? supervisor;
        ClientWebSocket? socket;
        lock (_stateLock)
        {
            sessionCancellation = _sessionCancellation;
            supervisor = _supervisorTask;
            socket = _socket;
        }
        if (sessionCancellation is null)
        {
            return;
        }

        StatusChanged?.Invoke(this, "Disconnecting");
        sessionCancellation.Cancel();
        if (socket is not null && socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Conversation stopped", cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
            }
        }
        if (supervisor is not null)
        {
            await IgnoreCancellationAsync(supervisor).ConfigureAwait(false);
        }
        ClearSessionState(sessionCancellation);
        SetConnectionAvailability(false);
        StatusChanged?.Invoke(this, "Disconnected");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    private async Task RunConnectionSupervisorAsync(string apiKey, TaskCompletionSource initialConnection, CancellationToken cancellationToken)
    {
        bool connectedOnce = false;
        int retryIndex = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (connectedOnce)
            {
                if (retryIndex >= ReconnectPolicy.Delays.Count)
                {
                    StatusChanged?.Invoke(this, "Unable to reconnect after 5 attempts; conversation disconnected");
                    return;
                }
                TimeSpan delay = ReconnectPolicy.Delays[retryIndex++];
                StatusChanged?.Invoke(this, $"Reconnecting in {delay.TotalSeconds:0} second(s) (attempt {retryIndex}/5)");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            string? attemptedHandle = _resumptionHandle;
            ClientWebSocket? socket = null;
            bool setupSucceeded = false;
            bool isReconnect = connectedOnce;
            try
            {
                SocketSetupResult setup = await ConnectSocketAsync(apiKey, attemptedHandle, cancellationToken).ConfigureAwait(false);
                socket = setup.Socket;
                setupSucceeded = true;
                connectedOnce = true;
                retryIndex = 0;
                bool attemptedResumption = !string.IsNullOrWhiteSpace(attemptedHandle);
                bool resumedSession = attemptedResumption;
                string connectedLabel = isReconnect ? "Reconnected" : "Connected";
                StatusChanged?.Invoke(this, connectedLabel);
                StatusChanged?.Invoke(this,
                    $"{connectedLabel}: session {(resumedSession ? "resumed" : "fresh")}, web search {(setup.WebSearchEnabled ? "ON" : "OFF")}");
                SetConnectionAvailability(true);
                SessionReady?.Invoke(this, new SessionReadyEventArgs(
                    isReconnect,
                    attemptedResumption,
                    resumedSession,
                    setup.WebSearchEnabled));
                initialConnection.TrySetResult();
                await ReceiveUntilDisconnectedAsync(socket, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                if (!connectedOnce)
                {
                    initialConnection.TrySetException(ex);
                    return;
                }
                if (!setupSucceeded && !string.IsNullOrWhiteSpace(attemptedHandle))
                {
                    _resumptionHandle = ReconnectPolicy.HandleAfterSetupFailure(attemptedHandle);
                    StatusChanged?.Invoke(this, "Session resumption was rejected; retrying with a fresh Gemini session");
                }
                else
                {
                    StatusChanged?.Invoke(this, $"Connection lost: {GetSafeConnectionError(ex)}");
                }
            }
            finally
            {
                SetConnectionAvailability(false);
                lock (_stateLock)
                {
                    if (ReferenceEquals(_socket, socket))
                    {
                        _socket = null;
                    }
                }
                socket?.Abort();
                socket?.Dispose();
            }
        }
    }

    private async Task<SocketSetupResult> ConnectSocketAsync(string apiKey, string? resumptionHandle, CancellationToken cancellationToken)
    {
        bool webSearch = !s_webSearchUnavailable;
        try
        {
            return await ConnectSocketAsync(apiKey, resumptionHandle, webSearch, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (webSearch && IsQuotaRejection(ex) && !cancellationToken.IsCancellationRequested)
        {
            s_webSearchUnavailable = true;
            StatusChanged?.Invoke(this, "Web search is not available for this API key (no quota); continuing without it");
            return await ConnectSocketAsync(apiKey, resumptionHandle, false, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool IsQuotaRejection(Exception exception) =>
        exception.Message.Contains("exceeded your current quota", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase);

    private async Task<SocketSetupResult> ConnectSocketAsync(string apiKey, string? resumptionHandle, bool webSearch, CancellationToken cancellationToken)
    {
        ClientWebSocket socket = new();
        try
        {
            await socket.ConnectAsync(new Uri($"{Endpoint}?key={Uri.EscapeDataString(apiKey)}"), cancellationToken).ConfigureAwait(false);
            TaskCompletionSource setupCompleted = NewCompletionSource();
            lock (_stateLock)
            {
                _socket = socket;
            }
            await SendJsonAsync(new SetupMessage
            {
                Setup = new SetupConfiguration
                {
                    Model = Model,
                    GenerationConfig = new AudioGenerationConfiguration(),
                    SystemInstruction = new InstructionContent
                    {
                        Parts = [new InstructionPart { Text = BuildInstruction(webSearch) }]
                    },
                    SessionResumption = new SessionResumptionConfiguration { Handle = resumptionHandle },
                    Tools = webSearch ? [new ToolConfiguration()] : null
                }
            }, cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(this,
                $"Gemini Live setup sent (resumption handle {(string.IsNullOrWhiteSpace(resumptionHandle) ? "none" : "present")}, web search {(webSearch ? "ON" : "OFF")}); awaiting server confirmation");
            Task receiveSetup = ReceiveUntilSetupAsync(socket, setupCompleted, cancellationToken);
            // A server close during setup (e.g. a quota rejection) fails receiveSetup without completing setupCompleted;
            // surface that real error immediately instead of waiting for the 15 s timeout.
            await Task.WhenAny(setupCompleted.Task, receiveSetup).WaitAsync(SetupTimeout, cancellationToken).ConfigureAwait(false);
            await receiveSetup.ConfigureAwait(false);
            await setupCompleted.Task.ConfigureAwait(false);
            return new SocketSetupResult(socket, webSearch);
        }
        catch
        {
            socket.Abort();
            socket.Dispose();
            lock (_stateLock)
            {
                if (ReferenceEquals(_socket, socket))
                {
                    _socket = null;
                }
            }
            throw;
        }
    }

    private async Task ReceiveUntilSetupAsync(ClientWebSocket socket, TaskCompletionSource setupCompleted, CancellationToken cancellationToken)
    {
        while (!setupCompleted.Task.IsCompleted)
        {
            ParsedServerMessage message = await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            ProcessServerMessage(message);
            if (message.Error is not null)
            {
                setupCompleted.TrySetException(new InvalidOperationException(message.Error));
            }
            else if (message.SetupComplete)
            {
                setupCompleted.TrySetResult();
            }
        }
    }

    private async Task ReceiveUntilDisconnectedAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            ParsedServerMessage message = await ReceiveMessageAsync(socket, cancellationToken).ConfigureAwait(false);
            ProcessServerMessage(message);
            if (message.Error is not null)
            {
                throw new InvalidOperationException(message.Error);
            }
            if (message.GoAway)
            {
                string suffix = string.IsNullOrWhiteSpace(message.GoAwayTimeLeft) ? string.Empty : $" ({message.GoAwayTimeLeft} remaining)";
                StatusChanged?.Invoke(this, $"Gemini requested connection migration{suffix}");
                return;
            }
        }
        if (!cancellationToken.IsCancellationRequested)
        {
            throw new WebSocketException("The Gemini Live API WebSocket closed unexpectedly.");
        }
    }

    private static async Task<ParsedServerMessage> ReceiveMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using MemoryStream message = new();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(rentedBuffer, cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new InvalidOperationException(GetServerCloseMessage(result.CloseStatus, result.CloseStatusDescription));
                }
                message.Write(rentedBuffer, 0, result.Count);
            }
            while (!result.EndOfMessage);
            return ServerMessageParser.Parse(message.GetBuffer().AsMemory(0, checked((int)message.Length)));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private void ProcessServerMessage(ParsedServerMessage message)
    {
        if (message.Resumable == true && !string.IsNullOrWhiteSpace(message.NewHandle))
        {
            _resumptionHandle = message.NewHandle;
        }
        else if (message.Resumable == false)
        {
            _resumptionHandle = null;
        }
        if (message.Interrupted)
        {
            Interrupted?.Invoke(this, EventArgs.Empty);
        }
        EmitTranscription("user", message.InputTranscription);
        EmitTranscription(
            "assistant",
            _outputTranscription.Process(message.OutputTranscription, message.TurnComplete, message.Interrupted));
        foreach (byte[] audio in message.AudioChunks)
        {
            AudioReceived?.Invoke(this, audio);
        }
        if (message.TurnComplete && !message.Interrupted)
        {
            TurnCompleted?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task SendJsonAsync<T>(T message, CancellationToken cancellationToken)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClientWebSocket? socket;
            lock (_stateLock)
            {
                socket = _socket;
            }
            if (socket?.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("The Live API client is not connected.");
            }
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void EmitTranscription(string role, string? text)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            TranscriptionReceived?.Invoke(this, new TranscriptionEventArgs(role, text));
        }
    }

    private void SetConnectionAvailability(bool isAvailable)
    {
        if (_isConnected == isAvailable)
        {
            return;
        }
        _isConnected = isAvailable;
        ConnectionAvailabilityChanged?.Invoke(this, new ConnectionAvailabilityChangedEventArgs(isAvailable));
    }

    private void ClearSessionState(CancellationTokenSource sessionCancellation)
    {
        lock (_stateLock)
        {
            if (ReferenceEquals(_sessionCancellation, sessionCancellation))
            {
                _sessionCancellation = null;
                _supervisorTask = null;
                _socket = null;
                _resumptionHandle = null;
            }
        }
        sessionCancellation.Dispose();
    }

    private static async Task IgnoreCancellationAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private sealed record SocketSetupResult(ClientWebSocket Socket, bool WebSearchEnabled);

    private static TaskCompletionSource NewCompletionSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string GetSafeConnectionError(Exception exception) => exception switch
    {
        TimeoutException => $"The Gemini Live API setup timed out after {SetupTimeout.TotalSeconds:0} seconds without a setup response.",
        OperationCanceledException => "The Gemini Live API connection was canceled.",
        WebSocketException => "The Gemini Live API WebSocket connection failed.",
        InvalidOperationException invalidOperationException
            when invalidOperationException.Message.StartsWith("Gemini Live API", StringComparison.Ordinal) => invalidOperationException.Message,
        _ => "The Gemini Live API connection failed."
    };

    private static string GetServerCloseMessage(WebSocketCloseStatus? status, string? description)
    {
        string statusText = status?.ToString() ?? "unknown status";
        string safeDescription = string.IsNullOrWhiteSpace(description)
            ? "No reason was provided."
            : new string(description.Where(character => !char.IsControl(character)).Take(300).ToArray());
        return $"Gemini Live API server closed the connection ({statusText}): {safeDescription}";
    }
}
