using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using SkiaSharp;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace GeminiLiveShare.Core.Vision;

public sealed class OcrCredentialDetector : IOcrCredentialDetector
{
    private static readonly TimeSpan OcrTimeout = TimeSpan.FromMilliseconds(500);
    private const float RectanglePadding = 8;

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
                    Math.Max(0, (float)match.Bounds.X - RectanglePadding),
                    Math.Max(0, (float)match.Bounds.Y - RectanglePadding),
                    Math.Min(frame.PixelWidth, (float)(match.Bounds.X + match.Bounds.Width) + RectanglePadding),
                    Math.Min(frame.PixelHeight, (float)(match.Bounds.Y + match.Bounds.Height) + RectanglePadding)))
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException(
                $"Windows OCR exceeded {OcrTimeout.TotalMilliseconds:0} ms; the frame must be dropped.");
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"Windows OCR failed; the frame must be dropped: {ex}");
            throw new InvalidOperationException("Windows OCR could not safely inspect the frame.", ex);
        }
    }
}