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

    private const string DesktopVisionInstruction =
        "You are GeminiLiveShare, a real-time desktop vision assistant. " +
        "The realtime video stream contains screenshots from the user's primary monitor only. " +
        "When the user asks about the screen, inspect the newest available screenshot before answering. " +
        "If no screenshot has been sent in the current session, you have no visual access and must not claim to see the desktop. " +
        "For text, read the relevant area carefully and preserve exact spelling, capitalization, and numbers; " +
        "do not guess text that is not legible. For icons, identify the visible icon and its label or location. " +
        "When asked to count desktop icons, scan the complete desktop systematically from top to bottom and left to right, " +
        "use the icon tile labels and glyphs rather than guessing from wallpaper, count each visible icon tile once, " +
        "and exclude the taskbar, this overlay, window chrome, and wallpaper. If the screenshot resolution is insufficient, " +
        "say that the count is uncertain and ask for a closer screenshot rather than inventing a number. " +
        "Report the total and mention any item that is ambiguous instead of inventing a count. " +
        "A text message saying screen sharing is disabled is authoritative: there are no current visuals, " +
        "so say exactly, 'I don't see your screen right now; I'm not receiving any visuals.'";

    public event EventHandler<byte[]>? AudioReceived;
    public event EventHandler? Interrupted;
    public event EventHandler<string>? StatusChanged;
    public event EventHandler<TranscriptionEventArgs>? TranscriptionReceived;
    public event EventHandler<ConnectionAvailabilityChangedEventArgs>? ConnectionAvailabilityChanged;
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
            try
            {
                socket = await ConnectSocketAsync(apiKey, attemptedHandle, cancellationToken).ConfigureAwait(false);
                setupSucceeded = true;
                connectedOnce = true;
                retryIndex = 0;
                SetConnectionAvailability(true);
                StatusChanged?.Invoke(this, initialConnection.Task.IsCompleted ? "Reconnected" : "Connected");
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

    private async Task<ClientWebSocket> ConnectSocketAsync(string apiKey, string? resumptionHandle, CancellationToken cancellationToken)
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
                        Parts = [new InstructionPart { Text = DesktopVisionInstruction }]
                    },
                    SessionResumption = new SessionResumptionConfiguration { Handle = resumptionHandle }
                }
            }, cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(this, "Gemini Live setup sent; awaiting server confirmation");
            Task receiveSetup = ReceiveUntilSetupAsync(socket, setupCompleted, cancellationToken);
            await setupCompleted.Task.WaitAsync(SetupTimeout, cancellationToken).ConfigureAwait(false);
            await receiveSetup.ConfigureAwait(false);
            return socket;
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
