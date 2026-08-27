using System.Text.Json;
using GeminiLiveShare.Core.Audio;
using GeminiLiveShare.Core.Gemini;
using GeminiLiveShare.Core.Gemini.Models;
using GeminiLiveShare.Core.Security;
using GeminiLiveShare.Core.Storage;
using GeminiLiveShare.Core.Vision;
using SkiaSharp;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

ValidateMatcher();
await ValidateSanitizationBeforeEncodingAsync();
await ValidateDetectorFailureDropsFrameAsync();
ValidateLiveProtocol();
ValidateOutputTranscriptionAccumulation();
ValidateReconnectPolicy();
await ValidateChatHistoryAsync();
await ValidateMediaPauseAndRestoreAsync();
Console.WriteLine("Credential filtering, Live protocol, reconnect, and chat-history validation passed.");

static void ValidateMatcher()
{
    SensitiveLine[] sameLine =
    {
        new("password is FAKE-PASSWORD-12345", new Rect(10, 10, 260, 20), string.Empty)
    };
    Require(CredentialMatcher.Find(sameLine).Count == 1, "password-is syntax was not detected");

    SensitiveLine[] splitLines =
    {
        new("Password", new Rect(10, 10, 90, 20), string.Empty),
        new("FAKE-PASSWORD-12345", new Rect(10, 42, 220, 20), string.Empty)
    };
    IReadOnlyList<SensitiveLine> splitMatches = CredentialMatcher.Find(splitLines);
    Require(splitMatches.Count == 2, "split password label/value was not detected");
    Require(splitMatches.Any(match => match.Text == "FAKE-PASSWORD-12345"), "split password value was not covered");

    SensitiveLine[] sameRowSplit =
    {
        // Windows OCR can return these in non-visual order, so put the value first deliberately.
        new("MyFakePassword123!", new Rect(125, 80, 190, 24), string.Empty),
        new("password:", new Rect(10, 80, 100, 24), string.Empty)
    };
    IReadOnlyList<SensitiveLine> sameRowMatches = CredentialMatcher.Find(sameRowSplit);
    Require(sameRowMatches.Count == 2, "same-row split password label/value was not detected");
    Require(sameRowMatches.Any(match => match.Text == "MyFakePassword123!"),
        "same-row password value was not covered");
}

static async Task ValidateSanitizationBeforeEncodingAsync()
{
    const int width = 100;
    const int height = 60;
    byte[] whitePixels = Enumerable.Repeat((byte)255, width * height * 4).ToArray();
    using SoftwareBitmap frame = new(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
    IBuffer buffer;
    using (DataWriter writer = new())
    {
        writer.WriteBytes(whitePixels);
        buffer = writer.DetachBuffer();
    }
    frame.CopyFromBuffer(buffer);

    ImageProcessingService service = new(
        new SuccessfulUiAutomationStub(),
        new FixedOcrStub(new[] { new SKRect(20, 15, 80, 45) }),
        new EnabledFilterSettings());
    string? encoded = await service.EncodeForGeminiAsync(frame, CancellationToken.None);
    Require(encoded is not null, "sanitized frame was unexpectedly dropped");

    using SKBitmap decoded = SKBitmap.Decode(Convert.FromBase64String(encoded!));
    SKColor protectedPixel = decoded.GetPixel(50, 30);
    SKColor unprotectedPixel = decoded.GetPixel(5, 5);
    Require(protectedPixel.Red < 20 && protectedPixel.Green < 20 && protectedPixel.Blue < 20,
        "OCR rectangle was not black before JPEG encoding");
    Require(unprotectedPixel.Red > 235 && unprotectedPixel.Green > 235 && unprotectedPixel.Blue > 235,
        "pixels outside the OCR rectangle were unexpectedly changed");
}

static async Task ValidateDetectorFailureDropsFrameAsync()
{
    using SoftwareBitmap frame = new(BitmapPixelFormat.Bgra8, 10, 10, BitmapAlphaMode.Premultiplied);
    ImageProcessingService service = new(
        new SuccessfulUiAutomationStub(),
        new FailingOcrStub(),
        new EnabledFilterSettings());

    string? encoded = await service.EncodeForGeminiAsync(frame, CancellationToken.None);
    Require(encoded is null, "frame was encoded after a credential detector failed");
}

static void ValidateLiveProtocol()
{
    SetupMessage freshSetup = new()
    {
        Setup = new SetupConfiguration
        {
            Model = "models/test",
            GenerationConfig = new AudioGenerationConfiguration()
        }
    };
    using JsonDocument freshJson = JsonDocument.Parse(JsonSerializer.Serialize(freshSetup));
    JsonElement setup = freshJson.RootElement.GetProperty("setup");
    Require(setup.GetProperty("inputAudioTranscription").ValueKind == JsonValueKind.Object,
        "input transcription was not enabled in setup");
    Require(setup.GetProperty("outputAudioTranscription").ValueKind == JsonValueKind.Object,
        "output transcription was not enabled in setup");
    Require(!setup.GetProperty("sessionResumption").TryGetProperty("handle", out _),
        "a fresh setup serialized a null resumption handle");

    SetupMessage resumedSetup = new()
    {
        Setup = new SetupConfiguration
        {
            Model = "models/test",
            GenerationConfig = new AudioGenerationConfiguration(),
            SessionResumption = new SessionResumptionConfiguration { Handle = "resume-123" }
        }
    };
    using JsonDocument resumedJson = JsonDocument.Parse(JsonSerializer.Serialize(resumedSetup));
    Require(resumedJson.RootElement.GetProperty("setup").GetProperty("sessionResumption")
        .GetProperty("handle").GetString() == "resume-123", "resumption handle was not serialized");

    const string serverJson = """
        {
          "sessionResumptionUpdate": { "resumable": true, "newHandle": "handle-2" },
          "goAway": { "timeLeft": "10s" },
          "serverContent": {
            "interrupted": true,
            "turnComplete": true,
            "inputTranscription": { "text": "hello" },
            "outputTranscription": { "text": "hi" },
            "modelTurn": { "parts": [
              { "inlineData": { "mimeType": "audio/pcm;rate=24000", "data": "AQID" } }
            ] }
          }
        }
        """;
    ParsedServerMessage parsed = ServerMessageParser.Parse(System.Text.Encoding.UTF8.GetBytes(serverJson));
    Require(parsed.Resumable == true && parsed.NewHandle == "handle-2", "session resumption update was not parsed");
    Require(parsed.GoAway && parsed.GoAwayTimeLeft == "10s", "goAway was not parsed");
    Require(parsed.Interrupted, "interruption was not parsed");
    Require(parsed.TurnComplete, "turnComplete was not parsed");
    Require(parsed.InputTranscription == "hello" && parsed.OutputTranscription == "hi",
        "transcription text was not parsed");
    Require(parsed.AudioChunks.Count == 1 && parsed.AudioChunks[0].SequenceEqual(new byte[] { 1, 2, 3 }),
        "audio payload was not parsed");
}

static void ValidateOutputTranscriptionAccumulation()
{
    OutputTranscriptionAccumulator accumulator = new();
    Require(accumulator.Process("Hallo! Wie", false, false) is null,
        "assistant transcription was emitted before turn completion");
    Require(accumulator.Process(" kann", false, false) is null,
        "assistant transcription was emitted before turn completion");
    Require(accumulator.Process(" ich dir", false, false) is null,
        "assistant transcription was emitted before turn completion");
    Require(accumulator.Process(" heute helfen?", false, false) is null,
        "assistant transcription was emitted before turn completion");
    Require(accumulator.Process(null, true, false) == "Hallo! Wie kann ich dir heute helfen?",
        "assistant transcription chunks were not emitted as one complete turn");

    Require(accumulator.Process("This response was", false, false) is null,
        "partial assistant transcription was emitted before interruption");
    Require(accumulator.Process(" interrupted", false, true) == "This response was interrupted",
        "partial assistant transcription was not emitted on interruption");
    Require(accumulator.Process(null, false, true) is null,
        "an empty assistant transcription was emitted on interruption");
}

static void ValidateReconnectPolicy()
{
    Require(ReconnectPolicy.Delays.SequenceEqual(new[]
    {
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(16)
    }), "reconnect schedule is not 1/2/4/8/16 seconds");
    Require(ReconnectPolicy.HandleAfterSetupFailure("rejected") is null,
        "a rejected resumption handle was not cleared");
}

static async Task ValidateChatHistoryAsync()
{
    string directory = Path.Combine(Path.GetTempPath(), $"GeminiLiveShare.Tests.{Guid.NewGuid():N}");
    string databasePath = Path.Combine(directory, "history.db3");
    try
    {
        await using ChatHistoryRepository repository = new(databasePath);
        await repository.AddAsync(new ChatMessage
        {
            SessionId = "session-a", Role = "user", Text = "first", CreatedAtUtc = DateTime.UtcNow
        });
        await repository.AddAsync(new ChatMessage
        {
            SessionId = "session-b", Role = "assistant", Text = "other", CreatedAtUtc = DateTime.UtcNow
        });
        await repository.AddAsync(new ChatMessage
        {
            SessionId = "session-a", Role = "assistant", Text = "second", CreatedAtUtc = DateTime.UtcNow
        });

        IReadOnlyList<ChatMessage> messages = await repository.GetBySessionAsync("session-a");
        Require(messages.Count == 2, "chat history was not filtered by session");
        Require(messages[0].Text == "first" && messages[1].Text == "second",
            "chat history was not returned in insertion order");
        Require(messages.All(message => message.Id > 0), "chat history IDs were not generated");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

static async Task ValidateMediaPauseAndRestoreAsync()
{
    FakeAudioCapture capture = new();
    FakeLiveClient client = new();
    FakeScreenCapture screen = new();
    await using SessionOrchestrator orchestrator = new(
        capture, new FakeAudioPlayback(), client, screen, new FakeImageProcessing(), new FakeChatHistory());

    await orchestrator.StartAsync("test-key");
    Require(capture.IsCapturing && orchestrator.IsMicrophoneOn, "media did not start with the conversation");
    await WaitUntilAsync(() => screen.RunCount == 1, "screen capture did not start");
    Require(orchestrator.IsScreenShareOn, "screen-share state did not turn on with the conversation");

    await orchestrator.SetScreenShareEnabledAsync(false);
    Require(!orchestrator.IsScreenShareOn && capture.IsCapturing,
        "turning off screen share also stopped the microphone");
    await orchestrator.SetScreenShareEnabledAsync(true);
    await WaitUntilAsync(() => orchestrator.IsScreenShareOn && screen.RunCount == 2,
        "screen capture did not restart after being toggled on");

    await orchestrator.SetMicrophoneEnabledAsync(false);
    Require(!capture.IsCapturing && !orchestrator.IsMicrophoneOn && orchestrator.IsScreenShareOn,
        "turning off the microphone also stopped screen share");
    await orchestrator.SetMicrophoneEnabledAsync(true);
    Require(capture.IsCapturing && orchestrator.IsMicrophoneOn,
        "microphone capture did not restart after being toggled on");

    client.SetAvailable(false);
    await WaitUntilAsync(() => !capture.IsCapturing && !orchestrator.IsMicrophoneOn,
        "media did not pause when the connection was lost");

    client.SetAvailable(true);
    await WaitUntilAsync(() => capture.IsCapturing && orchestrator.IsMicrophoneOn &&
        orchestrator.IsScreenShareOn && screen.RunCount == 3,
        "media did not resume after reconnection");
    await orchestrator.StopAsync();
}

static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
{
    for (int attempt = 0; attempt < 100; attempt++)
    {
        if (condition())
        {
            return;
        }
        await Task.Delay(10);
    }
    throw new InvalidOperationException(failureMessage);
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

file sealed class SuccessfulUiAutomationStub : ICredentialBlurService
{
    public Task<bool> BlurPasswordFieldsAsync(SKBitmap fullResolutionFrame, CancellationToken cancellationToken) =>
        Task.FromResult(true);
}

file sealed class FixedOcrStub(IReadOnlyList<SKRect> rectangles) : IOcrCredentialDetector
{
    public Task<IReadOnlyList<SKRect>> DetectAsync(SoftwareBitmap frame, CancellationToken cancellationToken) =>
        Task.FromResult(rectangles);
}

file sealed class FailingOcrStub : IOcrCredentialDetector
{
    public Task<IReadOnlyList<SKRect>> DetectAsync(SoftwareBitmap frame, CancellationToken cancellationToken) =>
        Task.FromException<IReadOnlyList<SKRect>>(new InvalidOperationException("simulated OCR failure"));
}

file sealed class EnabledFilterSettings : ISensitiveContentFilterSettings
{
    public bool IsEnabled { get; set; } = true;
}

file sealed class FakeAudioCapture : IAudioCaptureService
{
    public event EventHandler<byte[]>? AudioCaptured { add { } remove { } }
    public event EventHandler<AudioCaptureFailedEventArgs>? CaptureFailed { add { } remove { } }
    public bool IsCapturing { get; private set; }
    public void Start() => IsCapturing = true;
    public void Stop() => IsCapturing = false;
    public void Dispose() { }
}

file sealed class FakeAudioPlayback : IAudioPlaybackService
{
    public void Start() { }
    public void Play(byte[] pcmAudio) { }
    public void Clear() { }
    public void Stop() { }
    public void Dispose() { }
}

file sealed class FakeLiveClient : IGeminiLiveClient
{
    public event EventHandler<byte[]>? AudioReceived { add { } remove { } }
    public event EventHandler? Interrupted { add { } remove { } }
    public event EventHandler<string>? StatusChanged { add { } remove { } }
    public event EventHandler<TranscriptionEventArgs>? TranscriptionReceived { add { } remove { } }
    public event EventHandler<ConnectionAvailabilityChangedEventArgs>? ConnectionAvailabilityChanged;
    public bool IsConnected { get; private set; }

    public Task ConnectAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        IsConnected = true;
        ConnectionAvailabilityChanged?.Invoke(this, new ConnectionAvailabilityChangedEventArgs(true));
        return Task.CompletedTask;
    }

    public Task SendAudioAsync(byte[] pcmAudio, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendVideoFrameAsync(string base64Jpeg, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendAudioStreamEndAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void SetAvailable(bool available)
    {
        IsConnected = available;
        ConnectionAvailabilityChanged?.Invoke(this, new ConnectionAvailabilityChangedEventArgs(available));
    }
}

file sealed class FakeScreenCapture : IScreenCaptureService
{
    public int RunCount { get; private set; }

    public async Task RunAsync(
        Func<SoftwareBitmap, CancellationToken, Task> frameHandler,
        CancellationToken cancellationToken)
    {
        RunCount++;
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class FakeImageProcessing : IImageProcessingService
{
    public Task<string?> EncodeForGeminiAsync(SoftwareBitmap frame, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);
}

file sealed class FakeChatHistory : IChatHistoryRepository
{
    public Task AddAsync(ChatMessage message) => Task.CompletedTask;
    public Task<IReadOnlyList<ChatMessage>> GetBySessionAsync(string sessionId) =>
        Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}