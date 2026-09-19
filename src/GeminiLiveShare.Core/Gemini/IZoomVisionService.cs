namespace GeminiLiveShare.Core.Gemini;

public interface IZoomVisionService
{
    Task<string> AnalyzeAsync(string apiKey, byte[] croppedJpeg, string question, CancellationToken cancellationToken = default);
}
