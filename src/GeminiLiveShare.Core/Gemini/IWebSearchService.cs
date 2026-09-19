namespace GeminiLiveShare.Core.Gemini;

public interface IWebSearchService
{
    Task<WebSearchResult> SearchAsync(string apiKey, string query, CancellationToken cancellationToken = default);
}

public sealed record WebSearchResult(string Summary, IReadOnlyList<string> Sources, bool IsReliable, string Provider = "unknown");
