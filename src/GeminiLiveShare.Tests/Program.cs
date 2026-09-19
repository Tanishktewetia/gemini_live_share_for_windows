using System.Text.Json;
using GeminiLiveShare.Core.Audio;
using GeminiLiveShare.Core.Gemini;
using GeminiLiveShare.Core.Diagnostics;
using GeminiLiveShare.Core.Desktop;
using GeminiLiveShare.Core.Gemini.Models;
using GeminiLiveShare.Core.Interop;
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
await ValidateFrameResolutionAndChangeDetectionAsync();
ValidateLiveProtocol();
ValidateWebSearchSetup();
ValidateOutputTranscriptionAccumulation();
ValidateReconnectPolicy();
await ValidateChatHistoryAsync();
await ValidateMediaPauseAndRestoreAsync();
await ValidateSpeakingStateAsync();
await ValidateScreenShareNoticeFollowsRealFrameAsync();
await ValidateReconnectContextRestoreAsync();
await ValidateSentFrameDiagnosticsAsync();
await ValidateDesktopIntentGroundingAsync();
await ValidateCountIntentRequiresVisualContextAsync();
await ValidateCountFollowUpUsesRecentTargetAsync();
await ValidateUnreliableCountGuardAsync();
await ValidateZoomRegionToolAsync();
await ValidateHighlightElementToolAsync();
await ValidateWebSearchToolAsync();
await ValidateToolCallDoesNotTriggerSilentRecoveryAsync();
await ValidateFreshFrameOnUserSpeechAsync();
ValidateGlobalHotkeySettings();
ValidatePlaybackQueueIsLossless();
ValidateTitleFormatting();
Console.WriteLine("Credential filtering, Live protocol, reconnect, chat-history, and playback-queue validation passed.");

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

static void ValidateGlobalHotkeySettings()
{
    string directory = Path.Combine(Path.GetTempPath(), $"gemini-hotkey-{Guid.NewGuid():N}");
    string settingsPath = Path.Combine(directory, "hotkey-settings.json");
    try
    {
        GlobalHotkeySettings settings = new(settingsPath);
        GlobalHotkeyConfiguration defaults = settings.Load();
        Require(defaults == GlobalHotkeyConfiguration.Default, "Ctrl+Y was not used as the default hotkey");
        Require(File.Exists(settingsPath), "default hotkey settings were not persisted");

        GlobalHotkeyConfiguration configured = new(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x48);
        settings.Save(configured);
        Require(settings.Load() == configured, "configured hotkey did not round-trip through JSON settings");
    }
    finally
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
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
    string? encoded = (await service.EncodeForGeminiAsync(frame, CancellationToken.None)).Base64Jpeg;
    Require(encoded is not null, "sanitized frame was unexpectedly dropped");

    byte[] encodedBytes = Convert.FromBase64String(encoded!);
    Require(encodedBytes.Length >= 3 && encodedBytes[..3].SequenceEqual(
        new byte[] { 0xFF, 0xD8, 0xFF }),
        "screen frame was not encoded as JPEG");
    using SKBitmap decoded = SKBitmap.Decode(encodedBytes);
    float scale = decoded.Width / (float)width;
    SKColor protectedPixel = decoded.GetPixel((int)(50 * scale), (int)(30 * scale));
    SKColor unprotectedPixel = decoded.GetPixel((int)(5 * scale), (int)(5 * scale));
    Require(protectedPixel.Red < 20 && protectedPixel.Green < 20 && protectedPixel.Blue < 20,
        "OCR rectangle was not black before JPEG encoding");
    Require(unprotectedPixel.Red > 235 && unprotectedPixel.Green > 235 && unprotectedPixel.Blue > 235,
        "pixels outside the OCR rectangle were unexpectedly changed");
}

static async Task ValidateFrameResolutionAndChangeDetectionAsync()
{
    ImageProcessingService service = new(new SuccessfulUiAutomationStub(), new FixedOcrStub([]), new EnabledFilterSettings());

    // 1080p is sent at native size: never upscaled (the old 2048 px minimum stretched it) and never shrunk.
    using SoftwareBitmap desktop = CreateSolidFrame(1920, 1080, 255);
    FrameEncodeResult first = await service.EncodeForGeminiAsync(desktop, CancellationToken.None);
    Require(first.Status == FrameEncodeStatus.Encoded, "the first shared frame was not sent");
    using (SKBitmap decoded = SKBitmap.Decode(Convert.FromBase64String(first.Base64Jpeg!)))
    {
        Require(decoded.Width == 1920 && decoded.Height == 1080, $"1080p frame was resized to {decoded.Width}x{decoded.Height}");
    }

    // An identical screen is skipped until the periodic refresh...
    Require((await service.EncodeForGeminiAsync(desktop, CancellationToken.None)).Status == FrameEncodeStatus.Unchanged,
        "an unchanged screen was sent again immediately");

    // ...but one typed character (an 8x14 px glyph-sized change) is a real change and is sent at once.
    byte[] thumbnailBefore = ImageProcessingService.CreateChangeThumbnail(ToSkBitmap(desktop));
    using SoftwareBitmap typed = CreateSolidFrame(1920, 1080, 255, darkRect: (900, 500, 8, 14));
    byte[] thumbnailAfter = ImageProcessingService.CreateChangeThumbnail(ToSkBitmap(typed));
    Require(ImageProcessingService.HasVisibleChange(thumbnailBefore, thumbnailAfter),
        "a single typed character was not detected as a screen change");
    Require((await service.EncodeForGeminiAsync(typed, CancellationToken.None)).Status == FrameEncodeStatus.Encoded,
        "a frame with a newly typed character was not sent");

    // Sharing restarted: the next frame must be sent even if nothing changed.
    service.ResetChangeDetection();
    Require((await service.EncodeForGeminiAsync(typed, CancellationToken.None)).Status == FrameEncodeStatus.Encoded,
        "the first frame after screen sharing restarted was not sent");

    // 4K captures are reduced to 2560 px wide to bound upload size.
    using SoftwareBitmap uhd = CreateSolidFrame(3840, 2160, 200);
    FrameEncodeResult large = await service.EncodeForGeminiAsync(uhd, CancellationToken.None);
    using (SKBitmap decoded = SKBitmap.Decode(Convert.FromBase64String(large.Base64Jpeg!)))
    {
        Require(decoded.Width == 2560 && decoded.Height == 1440, $"4K frame was sent at {decoded.Width}x{decoded.Height}");
    }

    // Lower quality (used while uploads are slow) produces a smaller frame.
    using SoftwareBitmap busy = CreateNoiseFrame(1920, 1080);
    service.ResetChangeDetection();
    int highQualityBytes = (await service.EncodeForGeminiAsync(busy, CancellationToken.None)).JpegBytes;
    service.JpegQuality = 60;
    service.ResetChangeDetection();
    int lowQualityBytes = (await service.EncodeForGeminiAsync(busy, CancellationToken.None)).JpegBytes;
    Require(lowQualityBytes < highQualityBytes, "lowering JPEG quality did not reduce the frame size");
}

static SoftwareBitmap CreateSolidFrame(int width, int height, byte value, (int X, int Y, int W, int H)? darkRect = null)
{
    byte[] pixels = Enumerable.Repeat(value, width * height * 4).ToArray();
    if (darkRect is { } rect)
    {
        for (int y = rect.Y; y < rect.Y + rect.H; y++)
            for (int x = rect.X; x < rect.X + rect.W; x++)
            {
                int offset = (y * width + x) * 4;
                pixels[offset] = pixels[offset + 1] = pixels[offset + 2] = 20;
            }
    }

    return CreateFrameFromPixels(width, height, pixels);
}

static SoftwareBitmap CreateNoiseFrame(int width, int height)
{
    byte[] pixels = new byte[width * height * 4];
    new Random(11).NextBytes(pixels);
    for (int offset = 3; offset < pixels.Length; offset += 4)
    {
        pixels[offset] = 255;
    }

    return CreateFrameFromPixels(width, height, pixels);
}

static SoftwareBitmap CreateFrameFromPixels(int width, int height, byte[] pixels)
{
    SoftwareBitmap frame = new(BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
    using DataWriter writer = new();
    writer.WriteBytes(pixels);
    frame.CopyFromBuffer(writer.DetachBuffer());
    return frame;
}

static SKBitmap ToSkBitmap(SoftwareBitmap frame)
{
    byte[] pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
    Windows.Storage.Streams.Buffer buffer = new((uint)pixels.Length);
    frame.CopyToBuffer(buffer);
    using (DataReader reader = DataReader.FromBuffer(buffer))
    {
        reader.ReadBytes(pixels);
    }

    SKBitmap bitmap = new(frame.PixelWidth, frame.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Premul);
    System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
    return bitmap;
}

static async Task ValidateDetectorFailureDropsFrameAsync()
{
    using SoftwareBitmap frame = new(BitmapPixelFormat.Bgra8, 10, 10, BitmapAlphaMode.Premultiplied);
    ImageProcessingService service = new(
        new SuccessfulUiAutomationStub(),
        new FailingOcrStub(),
        new EnabledFilterSettings());

    string? encoded = (await service.EncodeForGeminiAsync(frame, CancellationToken.None)).Base64Jpeg;
    Require(encoded is null, "frame was encoded after a credential detector failed");
}

static void ValidateTitleFormatting()
{
    Require(GeminiTitleGenerationService.CleanTitle("**Title: \"Fixing WPF Microphone Echo.\"**\nextra") == "Fixing WPF Microphone Echo",
        "generated title was not cleaned of markdown, prefix, quotes and trailing punctuation");
    Require(GeminiTitleGenerationService.CleanTitle("NONE") is null, "a no-topic response was accepted as a title");
    Require(GeminiTitleGenerationService.CleanTitle("   ") is null, "an empty response was accepted as a title");
    Require(GeminiTitleGenerationService.CleanTitle(new string('a', 90))!.Length == 60, "title was not limited to 60 characters");

    string transcript = GeminiTitleGenerationService.BuildTranscript([
        new ChatMessage { Role = "user", Text = "can you" },
        new ChatMessage { Role = "user", Text = "hear me" },
        new ChatMessage { Role = "assistant", Text = "Yes." },
        new ChatMessage { Role = "user", Text = "  " }]);
    Require(transcript == "User: can you hear me\nAssistant: Yes.",
        "voice transcription fragments were not merged into speaker turns");
}

static void ValidatePlaybackQueueIsLossless()
{
    // Gemini delivered a 38.7 s reply in ~10.6 s; the old 2 s discard-on-overflow buffer lost 67% of it.
    PcmPlaybackQueue queue = new(new NAudio.Wave.WaveFormat(24_000, 16, 1));
    const int replyBytes = 48_000 * 39;
    byte[] expected = new byte[replyBytes];
    new Random(7).NextBytes(expected);
    for (int offset = 0; offset < replyBytes; offset += 3_840)
    {
        int count = Math.Min(3_840, replyBytes - offset);
        queue.Enqueue(expected[offset..(offset + count)], count);
    }

    Require(queue.BufferedBytes == replyBytes, "playback queue discarded audio that arrived faster than real time");
    byte[] actual = new byte[replyBytes];
    for (int offset = 0; offset < replyBytes; offset += 4_800)
    {
        queue.Read(actual, offset, Math.Min(4_800, replyBytes - offset));
    }

    Require(actual.AsSpan().SequenceEqual(expected), "playback queue changed or reordered audio samples");
    byte[] silence = Enumerable.Repeat((byte)0xFF, 1_000).ToArray();
    Require(queue.Read(silence, 0, silence.Length) == silence.Length && silence.All(value => value == 0),
        "an empty playback queue did not pad the output with silence");
    queue.Enqueue(new byte[4_800], 4_800);
    queue.Clear();
    Require(queue.BufferedBytes == 0, "clearing the playback queue (barge-in) left audio queued");
}

static void ValidateWebSearchSetup()
{
    SetupMessage withSearch = new()
    {
        Setup = new SetupConfiguration
        {
            Model = "models/test",
            GenerationConfig = new AudioGenerationConfiguration(),
            Tools =
            [
                new ToolConfiguration { GoogleSearch = new GoogleSearchTool() },
                new ToolConfiguration
                {
                    FunctionDeclarations =
                    [
                        new FunctionDeclaration
                        {
                            Name = "get_element_under_cursor",
                            Description = "returns element under cursor",
                            Parameters = JsonSerializer.SerializeToElement(new { type = "object" })
                        },
                        new FunctionDeclaration
                        {
                            Name = "zoom_region",
                            Description = "zoom",
                            Parameters = JsonSerializer.SerializeToElement(new { type = "object" })
                        }
                    ]
                }
            ]
        }
    };
    using (JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(withSearch)))
    {
        JsonElement tools = json.RootElement.GetProperty("setup").GetProperty("tools");
        Require(tools.EnumerateArray().Any(tool => tool.TryGetProperty("googleSearch", out _)),
            "Google Search was not serialized as a Live API tool");
        Require(tools.EnumerateArray().Any(tool =>
                tool.TryGetProperty("functionDeclarations", out JsonElement declarations) &&
                declarations.EnumerateArray().Any(declaration => declaration.GetProperty("name").GetString() == "get_element_under_cursor")),
            "desktop function declarations were not serialized");
        Require(tools.EnumerateArray().Any(tool =>
                tool.TryGetProperty("functionDeclarations", out JsonElement declarations) &&
                declarations.EnumerateArray().Any(declaration => declaration.GetProperty("name").GetString() == "zoom_region")),
            "zoom_region function declaration was not serialized");
    }

    SetupMessage withoutSearch = new()
    {
        Setup = new SetupConfiguration
        {
            Model = "models/test",
            GenerationConfig = new AudioGenerationConfiguration(),
            Tools =
            [
                new ToolConfiguration
                {
                    FunctionDeclarations =
                    [
                        new FunctionDeclaration
                        {
                            Name = "list_desktop_icons",
                            Description = "lists desktop icons",
                            Parameters = JsonSerializer.SerializeToElement(new { type = "object" })
                        },
                        new FunctionDeclaration
                        {
                            Name = "zoom_region",
                            Description = "zoom",
                            Parameters = JsonSerializer.SerializeToElement(new { type = "object" })
                        }
                    ]
                }
            ]
        }
    };
    using (JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(withoutSearch)))
    {
        JsonElement tools = json.RootElement.GetProperty("setup").GetProperty("tools");
        Require(!tools.EnumerateArray().Any(tool => tool.TryGetProperty("googleSearch", out _)),
            "googleSearch tool was sent when search should be unavailable");
        Require(tools.EnumerateArray().Any(tool => tool.TryGetProperty("functionDeclarations", out _)),
            "desktop function tools were not serialized without web search");
    }

    ToolConfiguration[] fallbackTools = GeminiLiveClient.BuildTools(webSearchEnabled: false, desktopToolsEnabled: false)!;
    using (JsonDocument fallbackJson = JsonDocument.Parse(JsonSerializer.Serialize(fallbackTools)))
    {
        string raw = fallbackJson.RootElement.GetRawText();
        Require(!raw.Contains("additionalProperties", StringComparison.Ordinal),
            "Live function schemas still contain unsupported additionalProperties");
        Require(fallbackJson.RootElement.EnumerateArray().Any(tool =>
                tool.TryGetProperty("functionDeclarations", out JsonElement declarations) &&
                declarations.EnumerateArray().Any(declaration => declaration.GetProperty("name").GetString() == "web_search")),
            "the no-desktop fallback did not expose the app web_search tool");
    }

    // Measured close reason for a key without Google Search quota in Live sessions.
    Require(GeminiLiveClient.IsQuotaRejection(new InvalidOperationException(
        "Gemini Live API server closed the connection (InternalServerError): You exceeded your current quota, please check your plan")),
        "the search quota rejection was not recognised");
    Require(!GeminiLiveClient.IsQuotaRejection(new InvalidOperationException("The Gemini Live API WebSocket connection failed.")),
        "an ordinary connection failure was treated as a quota rejection");

    string searchInstruction = GeminiLiveClient.BuildInstruction(webSearchAvailable: true);
    string noSearchInstruction = GeminiLiveClient.BuildInstruction(webSearchAvailable: false);
    Require(searchInstruction.Contains("You can use Google Search", StringComparison.Ordinal),
        "the instruction did not tell Gemini it can search");
    Require(noSearchInstruction.Contains("Never say you searched", StringComparison.Ordinal),
        "without search, the instruction did not forbid claiming to have searched");
    Require(noSearchInstruction.Contains("Screen sharing is OFF when the conversation starts", StringComparison.Ordinal),
        "the screen access rules were lost from the instruction");
    Require(noSearchInstruction.Contains("Never guess", StringComparison.Ordinal),
        "the instruction did not explicitly ban guessing");
    Require(noSearchInstruction.Contains("use an available tool first", StringComparison.Ordinal),
        "the instruction did not require tool-first answers for fine-detail tasks");
    Require(noSearchInstruction.Contains("point at it with the mouse", StringComparison.Ordinal),
        "the instruction did not ask for pointer-based clarification when intent is unclear");
    Require(noSearchInstruction.Contains("zoom_region", StringComparison.Ordinal),
        "the instruction did not tell Gemini to use zoom_region for tiny non-UI text");

    string instructionWithFixedDate = GeminiLiveClient.BuildInstruction(
        webSearchAvailable: false,
        now: new DateTimeOffset(2026, 9, 19, 14, 35, 0, TimeSpan.FromHours(5.5)));
    Require(instructionWithFixedDate.Contains("Model: models/gemini-3.1-flash-live-preview", StringComparison.Ordinal),
        "the instruction did not include the active model name");
    Require(instructionWithFixedDate.Contains("User-local date when this instruction was built: 2026-09-19 (UTC+05:30)", StringComparison.Ordinal),
        "the instruction did not include the user-local date context");
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
    Require(setup.GetProperty("generationConfig").GetProperty("mediaResolution").GetString() ==
        "MEDIA_RESOLUTION_HIGH", "high media resolution was not enabled");
    Require(!setup.GetProperty("sessionResumption").TryGetProperty("handle", out _),
        "a fresh setup serialized a null resumption handle");

    SetupMessage visionSetup = new()
    {
        Setup = new SetupConfiguration
        {
            Model = "models/test",
            GenerationConfig = new AudioGenerationConfiguration(),
            SystemInstruction = new InstructionContent
            {
                Parts = [new InstructionPart { Text = "inspect the newest desktop screenshot" }]
            }
        }
    };
    using JsonDocument visionJson = JsonDocument.Parse(JsonSerializer.Serialize(visionSetup));
    Require(visionJson.RootElement.GetProperty("setup").GetProperty("systemInstruction")
        .GetProperty("parts")[0].GetProperty("text").GetString() ==
        "inspect the newest desktop screenshot", "desktop vision instruction was not serialized");

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
          "toolCall": {
            "functionCalls": [
              { "id": "tool-1", "name": "list_desktop_icons", "args": { "scope": "visible" } }
            ]
          },
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
    Require(parsed.ToolCalls.Count == 1 &&
            parsed.ToolCalls[0].Id == "tool-1" &&
            parsed.ToolCalls[0].Name == "list_desktop_icons" &&
            parsed.ToolCalls[0].Args.GetProperty("scope").GetString() == "visible",
        "tool call payload was not parsed");
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
        IReadOnlyList<ChatMessage> allMessages = await repository.GetAllAsync();
        Require(allMessages.Count == 3 && allMessages[0].CreatedAtUtc >= allMessages[^1].CreatedAtUtc,
            "chat history sessions were not returned newest first");

        await repository.SetSessionTitleAsync("session-a", "Visual debugging", true);
        ChatSessionMetadata sessionMetadata = (await repository.GetSessionMetadataAsync())
            .Single(metadata => metadata.SessionId == "session-a");
        Require(sessionMetadata.Title == "Visual debugging" && sessionMetadata.IsTitleUserEdited,
            "chat session title was not persisted");

        await repository.DeleteSessionAsync("session-a");
        Require((await repository.GetBySessionAsync("session-a")).Count == 0,
            "chat history session was not deleted");
        Require((await repository.GetBySessionAsync("session-b")).Count == 1,
            "deleting a session removed another session");
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
    Require(orchestrator.IsConnected && !orchestrator.IsConnecting,
        "orchestrator did not expose the established connection state");
    Require(capture.IsCapturing && orchestrator.IsMicrophoneOn, "media did not start with the conversation");
    Require(!orchestrator.IsScreenShareOn && screen.RunCount == 0,
        "screen sharing did not remain off when the conversation started");
    await orchestrator.SetScreenShareEnabledAsync(true);
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
    Require(!orchestrator.IsConnected, "orchestrator did not expose the unavailable connection state");

    client.SetAvailable(true);
    await WaitUntilAsync(() => capture.IsCapturing && orchestrator.IsMicrophoneOn &&
        orchestrator.IsScreenShareOn && screen.RunCount == 3,
        "media did not resume after reconnection");
    await orchestrator.StopAsync();
}

static async Task ValidateScreenShareNoticeFollowsRealFrameAsync()
{
    // Gemini hallucinated a desktop when told (or led to assume) it could see the screen without any image.
    // The "screen sharing is on" notice must only follow a screenshot that was really sent.
    FakeLiveClient droppedClient = new();
    await using (SessionOrchestrator dropped = new(new FakeAudioCapture(), new FakeAudioPlayback(), droppedClient,
        new FrameProducingScreenCapture(3), new FakeImageProcessing(), new FakeChatHistory()))
    {
        await dropped.StartAsync("test-key");
        Require(droppedClient.SentInOrder.Count == 0, "a frame or screen notice was sent before screen sharing was enabled");
        await dropped.SetScreenShareEnabledAsync(true);
        await Task.Delay(300);
        Require(droppedClient.SentInOrder.Count == 0,
            "the screen-sharing notice was sent although every frame was dropped by the privacy filter");
        await dropped.StopAsync();
    }

    FakeLiveClient client = new();
    await using SessionOrchestrator orchestrator = new(new FakeAudioCapture(), new FakeAudioPlayback(), client,
        new FrameProducingScreenCapture(3), new EncodingImageProcessing(), new FakeChatHistory());
    await orchestrator.StartAsync("test-key");
    await orchestrator.SetScreenShareEnabledAsync(true);
    await WaitUntilAsync(() => client.SentInOrder.Count >= 4, "screen frames were not sent after enabling screen share");
    string[] sent;
    lock (client.SentInOrder) sent = client.SentInOrder.ToArray();
    Require(sent[0] == "frame" && sent[1] == SessionOrchestrator.ScreenShareOnNotice,
        "the screen-sharing notice did not immediately follow the first real frame");
    Require(sent.Count(item => item == SessionOrchestrator.ScreenShareOnNotice) == 1,
        "the screen-sharing notice was repeated for later frames");
    await orchestrator.StopAsync();
}

static async Task ValidateSpeakingStateAsync()
{
    FakeLiveClient client = new();
    await using SessionOrchestrator orchestrator = new(
        new FakeAudioCapture(), new FakeAudioPlayback(), client, new FakeScreenCapture(),
        new FakeImageProcessing(), new FakeChatHistory());

    await orchestrator.StartAsync("test-key");
    client.ReceiveAudio(new byte[] { 1, 2 });
    Require(orchestrator.IsSpeaking, "Gemini audio did not set speaking state");
    await WaitUntilAsync(() => !orchestrator.IsSpeaking, "speaking state did not clear after silence");

    client.ReceiveAudio(new byte[] { 3 });
    client.Interrupt();
    Require(!orchestrator.IsSpeaking, "interruption did not clear speaking state");

    client.ReceiveAudio(new byte[] { 4 });
    client.SetAvailable(false);
    await WaitUntilAsync(() => !orchestrator.IsSpeaking, "disconnect did not clear speaking state");

    client.SetAvailable(true);
    await WaitUntilAsync(() => orchestrator.IsRunning, "session did not remain active after reconnect");
    client.ReceiveAudio(new byte[] { 5 });
    await orchestrator.StopAsync();
    Require(!orchestrator.IsSpeaking, "stopping the session did not clear speaking state");
}

static async Task ValidateReconnectContextRestoreAsync()
{
    FakeLiveClient client = new();
    SeededChatHistory history = new([
        new ChatMessage { SessionId = "active", Role = "user", Text = "please continue helping me", CreatedAtUtc = DateTime.UtcNow.AddSeconds(-2) },
        new ChatMessage { SessionId = "active", Role = "assistant", Text = "sure, we were changing the wifi setting", CreatedAtUtc = DateTime.UtcNow.AddSeconds(-1) }
    ]);
    await using SessionOrchestrator orchestrator = new(
        new FakeAudioCapture(), new FakeAudioPlayback(), client, new FakeScreenCapture(),
        new FakeImageProcessing(), history);

    await orchestrator.StartAsync("test-key");
    client.AnnounceSessionReady(isReconnect: true, attemptedResumption: true, wasSessionResumed: false, isWebSearchAvailable: true);
    client.SetAvailable(false);
    client.SetAvailable(true);
    await WaitUntilAsync(() => client.TextInputs.Any(text => text.Contains("connection recovered with a fresh Gemini session", StringComparison.Ordinal)),
        "reconnect context was not restored after resumption failed");
    await orchestrator.StopAsync();
}

static async Task ValidateSentFrameDiagnosticsAsync()
{
    string root = Path.Combine(Path.GetTempPath(), $"GeminiLiveShare.Tests.Frames.{Guid.NewGuid():N}");
    try
    {
        FileSessionDiagnostics diagnostics = new(root);
        DiagnosticsDebugSettings diagnosticsSettings = new(
            settingsPath: Path.Combine(root, "diagnostics-settings.json"),
            sentFramesDirectory: Path.Combine(root, "frames"));
        diagnosticsSettings.SaveSentFrames = true;
        FakeLiveClient client = new();

        await using SessionOrchestrator orchestrator = new(
            new FakeAudioCapture(), new FakeAudioPlayback(), client,
            new FrameProducingScreenCapture(1), new EncodingImageProcessing(), new FakeChatHistory(),
            diagnostics: diagnostics,
            diagnosticsSettings: diagnosticsSettings);

        await orchestrator.StartAsync("test-key");
        await orchestrator.SetScreenShareEnabledAsync(true);
        await WaitUntilAsync(() => Directory.Exists(diagnostics.SentFramesDirectory) &&
            Directory.EnumerateFiles(diagnostics.SentFramesDirectory, "*.jpg", SearchOption.AllDirectories).Any(),
            "sent JPEG diagnostics were not written while debug frame capture was enabled");
        await orchestrator.StopAsync();
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, true);
        }
    }
}
static async Task ValidateDesktopIntentGroundingAsync()
{
    FakeLiveClient client = new();
    RecordingChatHistory history = new();
    SessionOrchestrator orchestrator = new(
        new FakeAudioCapture(),
        new FakeAudioPlayback(),
        client,
        new FrameProducingScreenCapture(1),
        new EncodingImageProcessing(),
        history,
        desktopAutomation: new FakeDesktopAutomationService(iconCount: 56, taskbarCount: 13));

    await orchestrator.StartAsync("test-key");
    await orchestrator.SetScreenShareEnabledAsync(true);
    await WaitUntilAsync(() => client.SentInOrder.Any(item => item == "frame"), "visual context did not become active");

    client.EmitTranscription("user", "How many icons are there on my desktop?");
    await WaitUntilAsync(() => client.TextInputs.Any(text => text.Contains("Say exactly", StringComparison.OrdinalIgnoreCase) && text.Contains("56 desktop icons", StringComparison.OrdinalIgnoreCase)),
        "deterministic desktop count prompt was not sent for spoken output");

    client.EmitTranscription("assistant", "There are exactly 56 desktop icons.");
    await WaitUntilAsync(() => history.Messages.Any(message =>
            message.Role == "assistant" && message.Text.Contains("56 desktop icons", StringComparison.OrdinalIgnoreCase)),
        "spoken deterministic desktop count reply was not persisted");

    client.EmitTranscription("user", "How many taskbar icons are there?");
    await WaitUntilAsync(() => client.TextInputs.Any(text => text.Contains("13 taskbar app icons", StringComparison.OrdinalIgnoreCase)),
        "deterministic taskbar count prompt was not sent for spoken output");

    await orchestrator.StopAsync();
}
static async Task ValidateCountIntentRequiresVisualContextAsync()
{
    FakeLiveClient client = new();
    RecordingChatHistory history = new();
    SessionOrchestrator orchestrator = new(
        new FakeAudioCapture(),
        new FakeAudioPlayback(),
        client,
        new FrameProducingScreenCapture(1),
        new EncodingImageProcessing(),
        history,
        desktopAutomation: new FakeDesktopAutomationService(iconCount: 56, taskbarCount: 13));

    await orchestrator.StartAsync("test-key");
    // keep screen sharing OFF: deterministic visual count must not run yet.
    client.EmitTranscription("user", "How many icons are there on my desktop?");
    await WaitUntilAsync(() => client.TextInputs.Any(text => text.Contains(GeminiLiveClient.NoScreenReply, StringComparison.Ordinal)),
        "count intent without visual context did not produce no-screen authoritative reply");
    Require(!client.TextInputs.Any(text => text.Contains("56 desktop icons", StringComparison.OrdinalIgnoreCase)),
        "count was emitted before visual context became active");

    await orchestrator.StopAsync();
}
static async Task ValidateCountFollowUpUsesRecentTargetAsync()
{
    FakeLiveClient client = new();
    RecordingChatHistory history = new();
    SessionOrchestrator orchestrator = new(
        new FakeAudioCapture(),
        new FakeAudioPlayback(),
        client,
        new FrameProducingScreenCapture(1),
        new EncodingImageProcessing(),
        history,
        desktopAutomation: new FakeDesktopAutomationService(iconCount: 56, taskbarCount: 13));

    await orchestrator.StartAsync("test-key");
    await orchestrator.SetScreenShareEnabledAsync(true);
    await WaitUntilAsync(() => client.SentInOrder.Any(item => item == "frame"), "visual context did not become active");

    client.EmitTranscription("user", "How many icons are present on my desktop excluding taskbar?");
    await WaitUntilAsync(() => client.TextInputs.Count(text => text.Contains("56 desktop icons", StringComparison.OrdinalIgnoreCase)) == 1,
        "initial desktop count prompt was not sent deterministically");

    client.EmitTranscription("user", "Are you sure there are just 42 icons?");
    await WaitUntilAsync(() => client.TextInputs.Count(text => text.Contains("56 desktop icons", StringComparison.OrdinalIgnoreCase)) == 2,
        "follow-up count question did not reuse recent desktop target deterministically");

    await orchestrator.StopAsync();
}
static async Task ValidateUnreliableCountGuardAsync()
{
    FakeLiveClient client = new();
    RecordingChatHistory history = new();
    SessionOrchestrator orchestrator = new(
        new FakeAudioCapture(),
        new FakeAudioPlayback(),
        client,
        new FrameProducingScreenCapture(1),
        new EncodingImageProcessing(),
        history,
        desktopAutomation: new FakeDesktopAutomationService(iconCount: 0, taskbarCount: 0, desktopReliable: false, taskbarReliable: false));

    await orchestrator.StartAsync("test-key");
    await orchestrator.SetScreenShareEnabledAsync(true);
    await WaitUntilAsync(() => client.SentInOrder.Any(item => item == "frame"), "visual context did not become active");

    client.EmitTranscription("user", "How many icons are there on my desktop?");
    await WaitUntilAsync(() => client.TextInputs.Any(text => text.Contains("can't verify an exact desktop icon count", StringComparison.OrdinalIgnoreCase)),
        "unreliable desktop count did not produce guarded spoken prompt");

    client.EmitTranscription("assistant", "There are exactly 0 icons.");
    await Task.Delay(120);
    Require(!history.Messages.Any(message => message.Role == "assistant" && message.Text.Contains("exactly 0", StringComparison.OrdinalIgnoreCase)),
        "numeric drift response was not suppressed after guarded deterministic count");

    client.EmitTranscription("user", "How many number of icons are there on taskbar only?");
    await WaitUntilAsync(() => client.TextInputs.Any(text => text.Contains("can't verify an exact taskbar app-icon count", StringComparison.OrdinalIgnoreCase)),
        "unreliable taskbar count did not produce guarded spoken prompt");

    await orchestrator.StopAsync();
}
static async Task ValidateZoomRegionToolAsync()
{
    FakeLiveClient client = new();
    RecordingChatHistory history = new();
    RecordingImageProcessing image = new();
    StaticZoomVisionService zoom = new("The label reads: Confirm Purchase");
    await using SessionOrchestrator orchestrator = new(
        new FakeAudioCapture(),
        new FakeAudioPlayback(),
        client,
        new FrameProducingScreenCapture(1),
        image,
        history,
        desktopAutomation: new FakeDesktopAutomationService(iconCount: 56, taskbarCount: 13),
        zoomVisionService: zoom);

    await orchestrator.StartAsync("test-key");
    await orchestrator.SetScreenShareEnabledAsync(true);
    await WaitUntilAsync(() => client.SentInOrder.Any(item => item == "frame"),
        "screen frame was not sent before zoom tool call");

    client.EmitToolCalls([
        new ToolCallRequest(
            "zoom-1",
            "zoom_region",
            JsonSerializer.SerializeToElement(new
            {
                cells = new[] { "B2" },
                question = "What does the small button text say?"
            }))
    ]);

    await WaitUntilAsync(() => client.ToolResponses.Count > 0, "zoom_region tool response was not sent");
    JsonElement response = client.ToolResponses[^1].Response;
    Require(response.GetProperty("ok").GetBoolean(), "zoom_region response did not succeed");
    Require(response.GetProperty("answer").GetString() == "The label reads: Confirm Purchase",
        "zoom_region response did not return the zoom model answer");
    Require(zoom.Calls == 1, "zoom model was not called exactly once");
    Require(zoom.LastQuestion == "What does the small button text say?", "zoom question did not round-trip");
    await orchestrator.StopAsync();
}

static async Task ValidateHighlightElementToolAsync()
{
    FakeLiveClient client = new();
    RecordingHighlightOverlay overlay = new();
    await using SessionOrchestrator orchestrator = new(
        new FakeAudioCapture(),
        new FakeAudioPlayback(),
        client,
        new FakeScreenCapture(),
        new FakeImageProcessing(),
        new FakeChatHistory(),
        desktopAutomation: new FakeDesktopAutomationService(iconCount: 0, taskbarCount: 0),
        highlightOverlay: overlay);

    await orchestrator.StartAsync("test-key");
    client.EmitToolCalls([
        new ToolCallRequest(
            "highlight-1",
            "highlight_element",
            JsonSerializer.SerializeToElement(new { name = "Codex", role = "button" }))
    ]);

    await WaitUntilAsync(() => client.ToolResponses.Count > 0, "highlight_element tool response was not sent");
    JsonElement response = client.ToolResponses[^1].Response;
    Require(response.GetProperty("ok").GetBoolean(), "highlight_element did not succeed");
    Require(response.GetProperty("selected").GetProperty("name").GetString() == "Codex",
        "highlight_element selected the wrong control");
    Require(overlay.ShowCalls == 1 && overlay.LastBounds.Width == 80 && overlay.LastBounds.Height == 30,
        "highlight overlay did not receive the UI Automation bounds");
    await orchestrator.StopAsync();
}

static async Task ValidateWebSearchToolAsync()
{
    FakeLiveClient client = new();
    FakeWebSearchService search = new();
    await using SessionOrchestrator orchestrator = new(
        new FakeAudioCapture(),
        new FakeAudioPlayback(),
        client,
        new FakeScreenCapture(),
        new FakeImageProcessing(),
        new FakeChatHistory(),
        webSearchService: search);

    await orchestrator.StartAsync("test-key");
    client.EmitToolCalls([
        new ToolCallRequest(
            "search-1",
            "web_search",
            JsonSerializer.SerializeToElement(new { query = "who makes Antigravity" }))
    ]);

    await WaitUntilAsync(() => client.ToolResponses.Count > 0, "web_search tool response was not sent");
    JsonElement response = client.ToolResponses[^1].Response;
    Require(response.GetProperty("ok").GetBoolean(), "web_search did not succeed");
    Require(response.GetProperty("summary").GetString() == "A sourced test result.",
        "web_search returned the wrong summary");
    Require(search.LastQuery == "who makes Antigravity", "web_search query did not round-trip");
    await orchestrator.StopAsync();
}

static async Task ValidateToolCallDoesNotTriggerSilentRecoveryAsync()
{
    FakeLiveClient client = new();
    FakeWebSearchService search = new(TimeSpan.FromSeconds(1));
    await using SessionOrchestrator orchestrator = new(
        new FakeAudioCapture(),
        new FakeAudioPlayback(),
        client,
        new FakeScreenCapture(),
        new FakeImageProcessing(),
        new FakeChatHistory(),
        webSearchService: search);

    await orchestrator.StartAsync("test-key");
    client.EmitTranscription("user", "search for the current weather");
    client.EmitToolCalls([
        new ToolCallRequest(
            "slow-search-1",
            "web_search",
            JsonSerializer.SerializeToElement(new { query = "current weather" }))
    ]);

    await WaitUntilAsync(() => client.ToolResponses.Count > 0,
        "slow web_search tool response was not sent");
    // The old watchdog fired four seconds after user transcription even when a tool was still/just finished running.
    // Keep the assertion past that boundary.
    await Task.Delay(TimeSpan.FromSeconds(4));
    Require(client.ConnectCalls == 1 && client.DisconnectCalls == 0,
        "a tool call was incorrectly treated as a silent turn and reconnected the session");
    await orchestrator.StopAsync();
}

static async Task ValidateFreshFrameOnUserSpeechAsync()
{
    FakeLiveClient client = new();
    RecordingImageProcessing image = new();
    await using SessionOrchestrator orchestrator = new(
        new FakeAudioCapture(),
        new FakeAudioPlayback(),
        client,
        new FakeScreenCapture(),
        image,
        new FakeChatHistory());

    await orchestrator.StartAsync("test-key");
    await orchestrator.SetScreenShareEnabledAsync(true);
    client.EmitTranscription("user", "please read this tiny text");
    await WaitUntilAsync(() => image.ForceSendNextFrameCalls > 0,
        "user speech did not force the next unchanged frame refresh");
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
    public bool IsEchoCancellationActive => false;
    public void Start() => IsCapturing = true;
    public void Stop() => IsCapturing = false;
    public void Dispose() { }
}

file sealed class FakeAudioPlayback : IAudioPlaybackService
{
    public bool HasQueuedAudio => false;
    public void Start() { }
    public void Play(byte[] pcmAudio) { }
    public void CompleteResponse() { }
    public void Clear() { }
    public void Stop() { }
    public void Dispose() { }
}

file sealed class FakeLiveClient : IGeminiLiveClient
{
    public event EventHandler<byte[]>? AudioReceived;
#pragma warning disable CS0067
    public event EventHandler? TurnCompleted;
#pragma warning restore CS0067
    public event EventHandler? Interrupted;
    public event EventHandler<string>? StatusChanged { add { } remove { } }
    public event EventHandler<TranscriptionEventArgs>? TranscriptionReceived;
    public event EventHandler<ConnectionAvailabilityChangedEventArgs>? ConnectionAvailabilityChanged;
    public event EventHandler<SessionReadyEventArgs>? SessionReady;
    public event EventHandler<ToolCallsEventArgs>? ToolCallsReceived;
    public bool IsConnected { get; private set; }
    public int ConnectCalls { get; private set; }
    public int DisconnectCalls { get; private set; }
    public List<string> TextInputs { get; } = [];
    public List<ToolResponsePayload> ToolResponses { get; } = [];

    public void EmitTranscription(string role, string text) =>
        TranscriptionReceived?.Invoke(this, new TranscriptionEventArgs(role, text));

    public void EmitToolCalls(IReadOnlyList<ToolCallRequest> calls) =>
        ToolCallsReceived?.Invoke(this, new ToolCallsEventArgs(calls));

    public Task ConnectAsync(string apiKey, CancellationToken cancellationToken = default)
    {
        ConnectCalls++;
        IsConnected = true;
        ConnectionAvailabilityChanged?.Invoke(this, new ConnectionAvailabilityChangedEventArgs(true));
        return Task.CompletedTask;
    }

    public Task SendAudioAsync(byte[] pcmAudio, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public List<string> SentInOrder { get; } = [];
    public Task SendVideoFrameAsync(string base64Jpeg, CancellationToken cancellationToken = default)
    {
        lock (SentInOrder) SentInOrder.Add("frame");
        return Task.CompletedTask;
    }
    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        lock (SentInOrder) SentInOrder.Add(text);
        TextInputs.Add(text);
        return Task.CompletedTask;
    }
    public Task SendAudioStreamEndAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SendToolResponseAsync(IReadOnlyList<ToolResponsePayload> responses, CancellationToken cancellationToken = default)
    {
        ToolResponses.AddRange(responses);
        return Task.CompletedTask;
    }
    public Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        DisconnectCalls++;
        IsConnected = false;
        return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void AnnounceSessionReady(bool isReconnect, bool attemptedResumption, bool wasSessionResumed, bool isWebSearchAvailable) =>
        SessionReady?.Invoke(this, new SessionReadyEventArgs(isReconnect, attemptedResumption, wasSessionResumed, isWebSearchAvailable));

    public void SetAvailable(bool available)
    {
        IsConnected = available;
        ConnectionAvailabilityChanged?.Invoke(this, new ConnectionAvailabilityChangedEventArgs(available));
    }

    public void ReceiveAudio(byte[] audio) => AudioReceived?.Invoke(this, audio);

    public void Interrupt() => Interrupted?.Invoke(this, EventArgs.Empty);
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

file sealed class FrameProducingScreenCapture(int frames) : IScreenCaptureService
{
    public async Task RunAsync(
        Func<SoftwareBitmap, CancellationToken, Task> frameHandler,
        CancellationToken cancellationToken)
    {
        for (int i = 0; i < frames; i++)
        {
            using SoftwareBitmap frame = new(BitmapPixelFormat.Bgra8, 4, 4, BitmapAlphaMode.Premultiplied);
            await frameHandler(frame, cancellationToken);
        }

        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class EncodingImageProcessing : IImageProcessingService
{
    public int JpegQuality { get; set; } = 90;
    public void ResetChangeDetection() { }
    public void ForceSendNextFrame() { }
    public Task<FrameEncodeResult> EncodeForGeminiAsync(SoftwareBitmap frame, CancellationToken cancellationToken) =>
        Task.FromResult(FrameEncodeResult.Encoded([0xFF, 0xD8, 0xFF], fullResolutionJpeg: [0xFF, 0xD8, 0xFF], fullResolutionWidth: 4, fullResolutionHeight: 4));
}

file sealed class FakeImageProcessing : IImageProcessingService
{
    public int JpegQuality { get; set; } = 90;
    public void ResetChangeDetection() { }
    public void ForceSendNextFrame() { }
    public Task<FrameEncodeResult> EncodeForGeminiAsync(SoftwareBitmap frame, CancellationToken cancellationToken) =>
        Task.FromResult(FrameEncodeResult.Dropped);
}

file sealed class RecordingImageProcessing : IImageProcessingService
{
    public int JpegQuality { get; set; } = 90;
    public int ForceSendNextFrameCalls { get; private set; }
    public void ResetChangeDetection() { }
    public void ForceSendNextFrame() => ForceSendNextFrameCalls++;

    public Task<FrameEncodeResult> EncodeForGeminiAsync(SoftwareBitmap frame, CancellationToken cancellationToken)
    {
        using SKBitmap bitmap = new(frame.PixelWidth, frame.PixelHeight, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(SKColors.White);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData encoded = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        byte[] jpeg = encoded.ToArray();
        return Task.FromResult(FrameEncodeResult.Encoded(jpeg, fullResolutionJpeg: jpeg, fullResolutionWidth: frame.PixelWidth, fullResolutionHeight: frame.PixelHeight));
    }
}

file sealed class RecordingHighlightOverlay : IHighlightOverlayService
{
    public bool IsVisible { get; private set; }
    public int ShowCalls { get; private set; }
    public System.Drawing.Rectangle LastBounds { get; private set; }

    public Task ShowAsync(System.Drawing.Rectangle bounds, string label, TimeSpan duration, CancellationToken cancellationToken = default)
    {
        ShowCalls++;
        LastBounds = bounds;
        IsVisible = true;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        IsVisible = false;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class FakeWebSearchService(TimeSpan? delay = null) : IWebSearchService
{
    private readonly TimeSpan _delay = delay ?? TimeSpan.Zero;
    public string? LastQuery { get; private set; }

    public async Task<WebSearchResult> SearchAsync(string apiKey, string query, CancellationToken cancellationToken = default)
    {
        LastQuery = query;
        await Task.Delay(_delay, cancellationToken);
        return new WebSearchResult("A sourced test result.", ["Example source"], true);
    }
}

file sealed class StaticZoomVisionService(string answer) : IZoomVisionService
{
    public int Calls { get; private set; }
    public string? LastQuestion { get; private set; }

    public Task<string> AnalyzeAsync(string apiKey, byte[] croppedJpeg, string question, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastQuestion = question;
        return Task.FromResult(answer);
    }
}
file sealed class SeededChatHistory(IReadOnlyList<ChatMessage> messages) : IChatHistoryRepository
{
    public event EventHandler<ChatMessageAddedEventArgs>? MessageAdded;

    public Task AddAsync(ChatMessage message)
    {
        MessageAdded?.Invoke(this, new ChatMessageAddedEventArgs(message));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> GetBySessionAsync(string sessionId) =>
        Task.FromResult<IReadOnlyList<ChatMessage>>(messages);

    public Task<IReadOnlyList<ChatMessage>> GetAllAsync() =>
        Task.FromResult<IReadOnlyList<ChatMessage>>(messages);

    public Task<IReadOnlyList<ChatSessionMetadata>> GetSessionMetadataAsync() =>
        Task.FromResult<IReadOnlyList<ChatSessionMetadata>>(Array.Empty<ChatSessionMetadata>());

    public Task SetSessionTitleAsync(string sessionId, string title, bool isUserEdited) => Task.CompletedTask;

    public Task DeleteSessionAsync(string sessionId) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
file sealed class RecordingChatHistory : IChatHistoryRepository
{
    public event EventHandler<ChatMessageAddedEventArgs>? MessageAdded;
    public List<ChatMessage> Messages { get; } = [];

    public Task AddAsync(ChatMessage message)
    {
        Messages.Add(message);
        MessageAdded?.Invoke(this, new ChatMessageAddedEventArgs(message));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> GetBySessionAsync(string sessionId) =>
        Task.FromResult<IReadOnlyList<ChatMessage>>(Messages.Where(message => message.SessionId == sessionId).ToArray());

    public Task<IReadOnlyList<ChatMessage>> GetAllAsync() => Task.FromResult<IReadOnlyList<ChatMessage>>(Messages);

    public Task<IReadOnlyList<ChatSessionMetadata>> GetSessionMetadataAsync() =>
        Task.FromResult<IReadOnlyList<ChatSessionMetadata>>(Array.Empty<ChatSessionMetadata>());

    public Task SetSessionTitleAsync(string sessionId, string title, bool isUserEdited) => Task.CompletedTask;

    public Task DeleteSessionAsync(string sessionId) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

file sealed class FakeDesktopAutomationService(
    int iconCount,
    int taskbarCount,
    bool desktopReliable = true,
    bool taskbarReliable = true) : IDesktopAutomationService
{
    public DesktopElementSnapshot? GetElementUnderCursor() =>
        new("Codex", "ControlType.ListItem", ["ControlType.Pane:Desktop"], new System.Drawing.Rectangle(100, 100, 80, 30));

    public IReadOnlyList<DesktopItemSnapshot> ListTaskbarItems() =>
        Enumerable.Range(1, taskbarCount)
            .Select(index => new DesktopItemSnapshot($"Taskbar {index}", "ControlType.Button", new System.Drawing.Rectangle(index * 10, 0, 10, 10)))
            .ToArray();

    public IReadOnlyList<DesktopItemSnapshot> ListDesktopIcons() =>
        Enumerable.Range(1, iconCount)
            .Select(index => new DesktopItemSnapshot($"Icon {index}", "ControlType.ListItem", new System.Drawing.Rectangle((index % 10) * 10, (index / 10) * 10, 10, 10)))
            .ToArray();

    public IReadOnlyList<DesktopItemSnapshot> FindElementsByNameRole(string name, string? role, string? location = null) =>
        name.Contains("Codex", StringComparison.OrdinalIgnoreCase)
            ? [new DesktopItemSnapshot("Codex", "ControlType.Button", new System.Drawing.Rectangle(100, 100, 80, 30))]
            : [];

    public DesktopIconCountSnapshot GetDesktopIconCount()
    {
        IReadOnlyList<DesktopItemSnapshot> items = ListDesktopIcons();
        return new DesktopIconCountSnapshot(
            Count: iconCount,
            SourceItemCount: iconCount,
            VisibleUiItemCount: items.Count,
            HiddenOrFilteredCount: Math.Max(0, iconCount - items.Count),
            IsReliable: desktopReliable,
            ReliabilityNote: desktopReliable ? "ok" : "desktop host not found",
            SourceStrategy: desktopReliable ? "test-shell-count" : "test-unreliable",
            SourcePolicy: "Desktop policy: shell FolderView item count when available; otherwise visible UIA list items. UIA remains the source for labels/positions.",
            VisibleItems: items);
    }

    public TaskbarItemCountSnapshot GetTaskbarItemCount()
    {
        IReadOnlyList<DesktopItemSnapshot> items = ListTaskbarItems();
        return new TaskbarItemCountSnapshot(
            Count: taskbarCount,
            AppButtons: taskbarCount,
            TrayButtons: 7,
            SystemButtons: 5,
            IsReliable: taskbarReliable,
            ReliabilityNote: taskbarReliable ? "ok" : "taskbar host not found",
            SourceStrategy: taskbarReliable ? "test-tasklist" : "test-unreliable",
            SourcePolicy: "Taskbar policy: app buttons only; Start/Search/Widgets/system tray/clock/overflow excluded.",
            AppItems: items);
    }

    public FocusedWindowSnapshot? GetFocusedWindow() =>
        new("explorer", "Desktop", null);
}
file sealed class FakeChatHistory : IChatHistoryRepository
{
    public event EventHandler<ChatMessageAddedEventArgs>? MessageAdded { add { } remove { } }
    public Task AddAsync(ChatMessage message) => Task.CompletedTask;
    public Task<IReadOnlyList<ChatMessage>> GetBySessionAsync(string sessionId) =>
        Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
    public Task<IReadOnlyList<ChatMessage>> GetAllAsync() =>
        Task.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
    public Task<IReadOnlyList<ChatSessionMetadata>> GetSessionMetadataAsync() =>
        Task.FromResult<IReadOnlyList<ChatSessionMetadata>>(Array.Empty<ChatSessionMetadata>());
    public Task SetSessionTitleAsync(string sessionId, string title, bool isUserEdited) => Task.CompletedTask;
    public Task DeleteSessionAsync(string sessionId) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
