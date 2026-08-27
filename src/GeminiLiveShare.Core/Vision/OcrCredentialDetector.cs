using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace GeminiLiveShare.Core.Vision;

public sealed class OcrCredentialDetector : IOcrCredentialDetector
{
    private static readonly TimeSpan OcrTimeout = TimeSpan.FromMilliseconds(500);

    public async Task<IReadOnlyList<SKRect>> DetectAsync(
        SoftwareBitmap frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            OcrEngine recognizer = OcrEngine.TryCreateFromUserProfileLanguages() ??
                throw new InvalidOperationException("No Windows OCR language is installed.");
            Task<OcrResult> recognition = recognizer.RecognizeAsync(frame).AsTask();
            _ = recognition.ContinueWith(
                completed => Trace.WriteLine(
                    $"Windows OCR failed after its frame timed out: {completed.Exception?.GetBaseException().Message}"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            OcrResult result = await recognition.WaitAsync(OcrTimeout, cancellationToken).ConfigureAwait(false);
            return CredentialMatcher.Find(result)
                .Select(match => new SKRect(
                    (float)match.Bounds.X,
                    (float)match.Bounds.Y,
                    (float)(match.Bounds.X + match.Bounds.Width),
                    (float)(match.Bounds.Y + match.Bounds.Height)))
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            Trace.WriteLine($"Windows OCR exceeded {OcrTimeout.TotalMilliseconds:0} ms; using zero OCR rectangles for this frame.");
            return Array.Empty<SKRect>();
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Windows OCR failed; using zero OCR rectangles for this frame: {ex}");
            return Array.Empty<SKRect>();
        }
    }
}