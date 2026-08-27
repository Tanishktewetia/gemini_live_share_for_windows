using GeminiLiveShare.Core.Security;
using GeminiLiveShare.Core.Vision;
using SkiaSharp;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

ValidateMatcher();
await ValidateSanitizationBeforeEncodingAsync();
await ValidateDetectorFailureDropsFrameAsync();
Console.WriteLine("Credential filtering validation passed.");

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