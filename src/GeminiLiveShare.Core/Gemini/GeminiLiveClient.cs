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
    private static readonly FunctionDeclaration[] s_desktopFunctionDeclarations =
    [
        new FunctionDeclaration
        {
            Name = "get_element_under_cursor",
            Description = "Return the Windows UI Automation element under the current mouse pointer, including name, control type, parent path, and screen bounds.",
            Parameters = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { },
                additionalProperties = false
            })
        },
        new FunctionDeclaration
        {
            Name = "list_taskbar_items",
            Description = "List visible taskbar items using UI Automation with exact names and bounds.",
            Parameters = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { },
                additionalProperties = false
            })
        },
        new FunctionDeclaration
        {
            Name = "list_desktop_icons",
            Description = "List visible desktop icons from the desktop FolderView with exact names and bounds.",
            Parameters = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { },
                additionalProperties = false
            })
        },
        new FunctionDeclaration
        {
            Name = "get_focused_window",
            Description = "Return the focused foreground window app name, title, and focused element details.",
            Parameters = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { },
                additionalProperties = false
            })
        },
        new FunctionDeclaration
        {
            Name = "zoom_region",
            Description = "Inspect a zoomed crop from the most recent full-resolution sanitized screenshot. Use either grid cells (A1-D4) or an explicit box with x,y,width,height plus a question.",
            Parameters = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new
                {
                    cells = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        minItems = 1,
                        maxItems = 6
                    },
                    box = new
                    {
                        type = "object",
                        properties = new
                        {
                            x = new { type = "integer" },
                            y = new { type = "integer" },
                            width = new { type = "integer" },
                            height = new { type = "integer" }
                        },
                        required = new[] { "x", "y", "width", "height" },
                        additionalProperties = false
                    },
                    question = new { type = "string" }
                },
                required = new[] { "question" },
                additionalProperties = false
            })
        }
    ];

    internal const string NoScreenReply =
        "I can't see your screen right now. Turn on screen sharing with the screen button on the overlay if you'd like me to look.";

    // Screen sharing is OFF when every session starts. Earlier wording described a constant screenshot stream plus
    // icon-counting advice, and with zero frames sent the model claimed to see the desktop in 3 of 4 test sessions
    // (inventing windows, apps and icon counts). Visual access is therefore stated as conditional, and the app sends
    // an explicit notice once the first screenshot has actually been sent (SessionOrchestrator.ScreenShareOnNotice).

    internal static string BuildInstruction(bool webSearchAvailable, DateTimeOffset? now = null)
    {
        DateTimeOffset instructionTime = now ?? DateTimeOffset.Now;
        string dateText = instructionTime.ToString("yyyy-MM-dd");
        string offsetText = instructionTime.ToString("zzz");
        string sessionContext =
            "SESSION CONTEXT:\n" +
            $"- Model: {Model}\n" +
            $"- User-local date when this instruction was built: {dateText} (UTC{offsetText})\n" +
            "- Use that date to reason about words like today, tomorrow and yesterday.\n\n";

        return sessionContext + DesktopVisionInstruction + "\n\n" +
            (webSearchAvailable
                ? "WEB SEARCH:\n- You can use Google Search. Use it when the user asks you to look something up or when a question " +
                  "needs current or factual information about products, companies or websites. Base your answer on the results."
                : "WEB SEARCH:\n- You cannot search the internet in this session. Never say you searched or looked something up. " +
                  "If asked to search, say you can't search the internet right now and answer from your own knowledge, saying it may be out of date.") +
            "\n\nNAMES YOU MAY HEAR:\n- Speech recognition often mishears product names. \"Cloud\" or \"Cloud Code\" said about an AI app " +
            "usually means Claude or Claude Code, made by Anthropic. If the user corrects a name, use their correction from then on.";
    }

    internal const string DesktopVisionInstruction =
        "You are GeminiLiveShare, a voice assistant running on the user's Windows PC.\n\n" +
        "SCREEN ACCESS RULES (highest priority):\n" +
        "- Screen sharing is OFF when the conversation starts. While it is off you cannot see anything on the user's computer.\n" +
        "- You can see the screen only after the app tells you screen sharing is on AND you have actually received a screenshot image in this conversation.\n" +
        "- Never pretend or assume you can see the screen. Never describe, guess or invent windows, apps, websites, icons, text or counts you have not received in an image.\n" +
        "- If the user asks about their screen and you have no screenshot, reply: '" + NoScreenReply + "'\n" +
        "- A message saying screen sharing is disabled is authoritative: from then on you have no visuals, even if you saw screenshots earlier.\n\n" +
        "ACCURACY AND RELIABILITY RULES:\n" +
        "- Never guess. If evidence is weak, partial or blurry, say you cannot see clearly.\n" +
        "- For pointer location, counting items, reading small text, or identifying icons/controls, use an available tool first.\n" +
        "- Available desktop tools include get_element_under_cursor, list_taskbar_items, list_desktop_icons, get_focused_window, and zoom_region.\n" +
        "- For details UI Automation cannot access (tiny text in images, scanned PDFs), call zoom_region and specify either grid cells A1-D4 or a box.\n" +
        "- If no tool is available for that request, say you cannot see it clearly from the screenshot instead of inventing an answer.\n" +
        "- If user intent is unclear, ask a brief clarifying question; if the target on screen is unclear, ask the user to point at it with the mouse.\n\n" +
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
    public event EventHandler<ToolCallsEventArgs>? ToolCallsReceived;
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

    public Task SendToolResponseAsync(IReadOnlyList<ToolResponsePayload> responses, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(responses);
        if (!IsConnected || responses.Count == 0)
        {
            return Task.CompletedTask;
        }

        return SendJsonAsync(new ToolResponseMessage
        {
            ToolResponse = new ToolResponsePayloadContent
            {
                FunctionResponses = responses.Select(response => new FunctionResponse
                {
                    Id = response.Id,
                    Name = response.Name,
                    Response = response.Response
                }).ToArray()
            }
        }, cancellationToken);
    }

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
                    $"{connectedLabel}: session {(resumedSession ? "resumed" : "fresh")}, web search {(setup.WebSearchEnabled ? "ON" : "OFF")}, desktop tools {(setup.DesktopToolsEnabled ? "ON" : "OFF")}");
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
        List<(bool WebSearch, bool DesktopTools)> attempts = [];
        if (!s_webSearchUnavailable)
        {
            attempts.Add((WebSearch: true, DesktopTools: true));
        }

        attempts.Add((WebSearch: false, DesktopTools: true));
        attempts.Add((WebSearch: false, DesktopTools: false));

        Exception? lastError = null;
        foreach ((bool webSearch, bool desktopTools) in attempts.Distinct())
        {
            try
            {
                return await ConnectSocketAsync(apiKey, resumptionHandle, webSearch, desktopTools, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                lastError = ex;
                if (webSearch && IsQuotaRejection(ex))
                {
                    s_webSearchUnavailable = true;
                    StatusChanged?.Invoke(this, "Web search is not available for this API key (no quota); continuing without it");
                    continue;
                }

                if (desktopTools)
                {
                    StatusChanged?.Invoke(this, $"Live desktop tools setup failed ({GetSafeConnectionError(ex)}); retrying with reduced tool set");
                    continue;
                }

                throw;
            }
        }

        throw lastError ?? new InvalidOperationException("The Gemini Live API connection failed.");
    }

    internal static bool IsQuotaRejection(Exception exception) =>
        exception.Message.Contains("exceeded your current quota", StringComparison.OrdinalIgnoreCase) ||
        exception.Message.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase);

    private static ToolConfiguration[]? BuildTools(bool webSearchEnabled, bool desktopToolsEnabled)
    {
        List<ToolConfiguration> tools = [];

        if (webSearchEnabled)
        {
            tools.Add(new ToolConfiguration { GoogleSearch = new GoogleSearchTool() });
        }

        if (desktopToolsEnabled)
        {
            tools.Add(new ToolConfiguration { FunctionDeclarations = s_desktopFunctionDeclarations });
        }

        return tools.Count == 0 ? null : tools.ToArray();
    }

    private async Task<SocketSetupResult> ConnectSocketAsync(
        string apiKey,
        string? resumptionHandle,
        bool webSearch,
        bool desktopTools,
        CancellationToken cancellationToken)
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
                    Tools = BuildTools(webSearch, desktopTools)
                }
            }, cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(this,
                $"Gemini Live setup sent (resumption handle {(string.IsNullOrWhiteSpace(resumptionHandle) ? "none" : "present")}, web search {(webSearch ? "ON" : "OFF")}, desktop tools {(desktopTools ? "ON" : "OFF")}); awaiting server confirmation");
            Task receiveSetup = ReceiveUntilSetupAsync(socket, setupCompleted, cancellationToken);
            // A server close during setup (e.g. a quota rejection) fails receiveSetup without completing setupCompleted;
            // surface that real error immediately instead of waiting for the 15 s timeout.
            await Task.WhenAny(setupCompleted.Task, receiveSetup).WaitAsync(SetupTimeout, cancellationToken).ConfigureAwait(false);
            await receiveSetup.ConfigureAwait(false);
            await setupCompleted.Task.ConfigureAwait(false);
            return new SocketSetupResult(socket, webSearch, desktopTools);
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
        if (message.ToolCalls.Count > 0)
        {
            ToolCallsReceived?.Invoke(this, new ToolCallsEventArgs(message.ToolCalls));
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

    private sealed record SocketSetupResult(ClientWebSocket Socket, bool WebSearchEnabled, bool DesktopToolsEnabled);

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

