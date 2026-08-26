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
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _sessionCancellation;
    private Task? _receiveTask;
    private TaskCompletionSource _setupCompleted = NewCompletionSource();

    public event EventHandler<byte[]>? AudioReceived;
    public event EventHandler? Interrupted;
    public event EventHandler<string>? StatusChanged;

    public bool IsConnected => _socket?.State == WebSocketState.Open && _setupCompleted.Task.IsCompletedSuccessfully;

    public async Task ConnectAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if (_socket is not null)
        {
            throw new InvalidOperationException("The Live API client is already active.");
        }

        StatusChanged?.Invoke(this, "Connecting");
        ClientWebSocket socket = new();
        CancellationTokenSource sessionCancellation = new();
        _setupCompleted = NewCompletionSource();

        try
        {
            Uri uri = new($"{Endpoint}?key={Uri.EscapeDataString(apiKey)}");
            await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(this, "WebSocket connected; sending Gemini Live setup");
            _socket = socket;
            _sessionCancellation = sessionCancellation;
            _receiveTask = ReceiveLoopAsync(socket, sessionCancellation.Token);

            SetupMessage setup = new()
            {
                Setup = new SetupConfiguration
                {
                    Model = Model,
                    GenerationConfig = new AudioGenerationConfiguration()
                }
            };
            await SendJsonAsync(setup, cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(this, "Gemini Live setup sent; awaiting server confirmation");
            await _setupCompleted.Task.WaitAsync(SetupTimeout, cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(this, "Connected");
        }
        catch (Exception ex)
        {
            sessionCancellation.Cancel();
            socket.Abort();
            if (_receiveTask is not null)
            {
                try
                {
                    await _receiveTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
            }

            socket.Dispose();
            sessionCancellation.Dispose();
            _socket = null;
            _sessionCancellation = null;
            _receiveTask = null;
            StatusChanged?.Invoke(this, "Disconnected");
            throw new InvalidOperationException(GetSafeConnectionError(ex), ex);
        }
    }

    public async Task SendAudioAsync(byte[] pcmAudio, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pcmAudio);
        if (!IsConnected || pcmAudio.Length == 0)
        {
            return;
        }

        RealtimeInputMessage message = new()
        {
            RealtimeInput = new RealtimeInput
            {
                Audio = new AudioBlob { Data = Convert.ToBase64String(pcmAudio) }
            }
        };
        await SendJsonAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendVideoFrameAsync(string base64Jpeg, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64Jpeg);
        RealtimeInputMessage message = new()
        {
            RealtimeInput = new RealtimeInput
            {
                Video = new VideoBlob { Data = base64Jpeg }
            }
        };
        await SendJsonAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAudioStreamEndAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            return;
        }

        await SendJsonAsync(new AudioStreamEndMessage(), cancellationToken).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ClientWebSocket? socket = _socket;
        CancellationTokenSource? sessionCancellation = _sessionCancellation;
        Task? receiveTask = _receiveTask;
        _socket = null;
        _sessionCancellation = null;
        _receiveTask = null;

        if (socket is null)
        {
            return;
        }

        StatusChanged?.Invoke(this, "Disconnecting");
        sessionCancellation?.Cancel();
        try
        {
            if (socket.State == WebSocketState.Open || socket.State == WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Conversation stopped", cancellationToken).ConfigureAwait(false);
            }
        }
        catch (WebSocketException)
        {
            // The remote endpoint may already have closed the socket.
        }

        if (receiveTask is not null)
        {
            try
            {
                await receiveTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        socket.Dispose();
        sessionCancellation?.Dispose();
        StatusChanged?.Invoke(this, "Disconnected");
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    private async Task SendJsonAsync<T>(T message, CancellationToken cancellationToken)
    {
        ClientWebSocket socket = _socket ?? throw new InvalidOperationException("The Live API client is not connected.");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        byte[] rentedBuffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using MemoryStream message = new();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(rentedBuffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        string closeMessage = GetServerCloseMessage(result.CloseStatus, result.CloseStatusDescription);
                        _setupCompleted.TrySetException(new InvalidOperationException(closeMessage));
                        StatusChanged?.Invoke(this, closeMessage);
                        return;
                    }

                    message.Write(rentedBuffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType is WebSocketMessageType.Text or WebSocketMessageType.Binary)
                {
                    if (!_setupCompleted.Task.IsCompleted)
                    {
                        StatusChanged?.Invoke(this, $"Gemini Live setup response received ({result.MessageType})");
                    }

                    ProcessServerMessage(message.GetBuffer().AsMemory(0, checked((int)message.Length)));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _setupCompleted.TrySetException(ex);
            StatusChanged?.Invoke(this, GetSafeConnectionError(ex));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rentedBuffer);
        }
    }

    private void ProcessServerMessage(ReadOnlyMemory<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("error", out JsonElement error))
        {
            string errorMessage = GetServerErrorMessage(error);
            _setupCompleted.TrySetException(new InvalidOperationException(errorMessage));
            StatusChanged?.Invoke(this, errorMessage);
            return;
        }

        if (root.TryGetProperty("setupComplete", out _))
        {
            _setupCompleted.TrySetResult();
        }

        if (!root.TryGetProperty("serverContent", out JsonElement serverContent))
        {
            return;
        }

        if (serverContent.TryGetProperty("interrupted", out JsonElement interrupted) && interrupted.ValueKind == JsonValueKind.True)
        {
            Interrupted?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!serverContent.TryGetProperty("modelTurn", out JsonElement modelTurn) ||
            !modelTurn.TryGetProperty("parts", out JsonElement parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement part in parts.EnumerateArray())
        {
            if (!part.TryGetProperty("inlineData", out JsonElement inlineData) ||
                !inlineData.TryGetProperty("data", out JsonElement data) ||
                data.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (inlineData.TryGetProperty("mimeType", out JsonElement mimeType) &&
                mimeType.GetString()?.StartsWith("audio/pcm", StringComparison.OrdinalIgnoreCase) == true)
            {
                string? encodedAudio = data.GetString();
                if (!string.IsNullOrEmpty(encodedAudio))
                {
                    AudioReceived?.Invoke(this, Convert.FromBase64String(encodedAudio));
                }
            }
        }
    }

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string GetSafeConnectionError(Exception exception) => exception switch
    {
        TimeoutException => $"The Gemini Live API setup timed out after {SetupTimeout.TotalSeconds:0} seconds without a setup response.",
        OperationCanceledException => "The Gemini Live API connection was canceled.",
        WebSocketException => "The Gemini Live API WebSocket connection failed.",
        InvalidOperationException invalidOperationException
            when invalidOperationException.Message.StartsWith("Gemini Live API server closed", StringComparison.Ordinal) =>
            invalidOperationException.Message,
        _ => "The Gemini Live API connection failed."
    };

    private static string GetServerCloseMessage(WebSocketCloseStatus? status, string? description)
    {
        string statusText = status?.ToString() ?? "unknown status";
        string safeDescription = string.IsNullOrWhiteSpace(description)
            ? "No reason was provided."
            : SanitizeServerText(description);
        return $"Gemini Live API server closed the connection ({statusText}): {safeDescription}";
    }

    private static string GetServerErrorMessage(JsonElement error)
    {
        string code = error.TryGetProperty("code", out JsonElement codeElement)
            ? codeElement.ToString()
            : "unknown code";
        string status = error.TryGetProperty("status", out JsonElement statusElement)
            ? SanitizeServerText(statusElement.ToString())
            : "unknown status";
        string message = error.TryGetProperty("message", out JsonElement messageElement)
            ? SanitizeServerText(messageElement.ToString())
            : "No reason was provided.";
        return $"Gemini Live API error ({code}, {status}): {message}";
    }

    private static string SanitizeServerText(string value)
    {
        const int maximumLength = 300;
        string sanitized = new(value
            .Where(character => !char.IsControl(character))
            .Take(maximumLength)
            .ToArray());
        return sanitized.Length == 0 ? "No reason was provided." : sanitized;
    }
}