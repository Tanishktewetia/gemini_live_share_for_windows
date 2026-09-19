using System.Net;
using System.Text;
using System.Text.Json;

namespace GeminiLiveShare.Core.Gemini;

/// <summary>
/// Performs web retrieval directly through Exa, with Tavily as the fallback.
/// The Live model is deliberately not asked to perform internet retrieval.
/// </summary>
public sealed class GeminiWebSearchService : IWebSearchService
{
    private const string ExaEndpoint = "https://api.exa.ai/search";
    private const string TavilyEndpoint = "https://api.tavily.com/search";
    private const int MaxResults = 5;
    private const int MaxSummaryCharacters = 2400;

    private readonly HttpClient _httpClient;
    private readonly Func<string?> _exaApiKeyProvider;
    private readonly Func<string?> _tavilyApiKeyProvider;

    public GeminiWebSearchService(
        HttpClient? httpClient = null,
        Func<string?>? exaApiKeyProvider = null,
        Func<string?>? tavilyApiKeyProvider = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _exaApiKeyProvider = exaApiKeyProvider ?? (() => LoadConfiguredKey("EXA_API_KEY"));
        _tavilyApiKeyProvider = tavilyApiKeyProvider ?? (() => LoadConfiguredKey("TAVILY_API_KEY"));
    }

    public async Task<WebSearchResult> SearchAsync(string apiKey, string query, CancellationToken cancellationToken = default)
    {
        // apiKey remains in the interface because the orchestrator also uses it for
        // the Live session. Direct search providers use their own keys instead.
        _ = apiKey;
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        List<string> failures = new();
        string? exaApiKey = NormalizeSecret(_exaApiKeyProvider());
        if (!string.IsNullOrWhiteSpace(exaApiKey))
        {
            try
            {
                return await SearchExaAsync(exaApiKey, query, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SearchProviderUnavailableException or HttpRequestException ||
                                         ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                failures.Add(FormatProviderFailure("Exa", ex));
            }
        }
        else
        {
            failures.Add("Exa API key is not configured.");
        }

        string? tavilyApiKey = NormalizeSecret(_tavilyApiKeyProvider());
        if (!string.IsNullOrWhiteSpace(tavilyApiKey))
        {
            try
            {
                return await SearchTavilyAsync(tavilyApiKey, query, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SearchProviderUnavailableException or HttpRequestException ||
                                         ex is TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                failures.Add(FormatProviderFailure("Tavily", ex));
            }
        }
        else
        {
            failures.Add("Tavily API key is not configured.");
        }

        throw new SearchProviderUnavailableException(
            "Direct web search is unavailable. Configure EXA_API_KEY and/or TAVILY_API_KEY in the app environment. " +
            string.Join("; ", failures));
    }

    private async Task<WebSearchResult> SearchExaAsync(string apiKey, string query, CancellationToken cancellationToken)
    {
        object request = new
        {
            query,
            type = "auto",
            numResults = MaxResults,
            contents = new
            {
                highlights = new
                {
                    maxCharacters = 800
                }
            }
        };

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, ExaEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.TryAddWithoutValidation("x-api-key", apiKey);

        using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess("Exa", response.StatusCode, body);
        return ParseSearchResponse("exa", body, ParseExaResults);
    }

    private async Task<WebSearchResult> SearchTavilyAsync(string apiKey, string query, CancellationToken cancellationToken)
    {
        object request = new
        {
            query,
            search_depth = "advanced",
            max_results = MaxResults,
            include_answer = false,
            include_raw_content = false,
            topic = "general"
        };

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, TavilyEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        EnsureSuccess("Tavily", response.StatusCode, body);
        return ParseSearchResponse("tavily", body, ParseTavilyResults);
    }

    private static WebSearchResult ParseSearchResponse(
        string provider,
        string body,
        Func<JsonElement, IReadOnlyList<SearchHit>> resultParser)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            IReadOnlyList<SearchHit> hits = resultParser(document.RootElement);
            if (hits.Count == 0)
            {
                throw new SearchProviderUnavailableException($"{provider} returned no usable search results.");
            }

            string summary = string.Join(" ", hits
                .Select(hit => string.IsNullOrWhiteSpace(hit.Snippet)
                    ? hit.Title
                    : $"{hit.Title}: {hit.Snippet}")
                .Where(text => !string.IsNullOrWhiteSpace(text)));
            summary = Trim(summary, MaxSummaryCharacters);
            if (string.IsNullOrWhiteSpace(summary))
            {
                throw new SearchProviderUnavailableException($"{provider} returned no readable search text.");
            }

            string[] sources = hits
                .Select(hit => string.IsNullOrWhiteSpace(hit.Title) ? hit.Url : hit.Title)
                .Where(source => !string.IsNullOrWhiteSpace(source))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxResults)
                .ToArray();
            return new WebSearchResult(summary, sources, true, provider);
        }
        catch (JsonException ex)
        {
            throw new SearchProviderUnavailableException($"{provider} returned invalid JSON: {ex.Message}");
        }
    }

    private static IReadOnlyList<SearchHit> ParseExaResults(JsonElement root)
    {
        if (!root.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SearchHit>();
        }

        List<SearchHit> hits = new();
        foreach (JsonElement result in results.EnumerateArray())
        {
            string title = GetString(result, "title");
            string url = GetString(result, "url");
            List<string> snippets = new();
            if (result.TryGetProperty("highlights", out JsonElement highlights) && highlights.ValueKind == JsonValueKind.Array)
            {
                snippets.AddRange(highlights.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty));
            }

            if (snippets.Count == 0)
            {
                string text = GetString(result, "text");
                if (!string.IsNullOrWhiteSpace(text))
                {
                    snippets.Add(text);
                }
            }

            AddHit(hits, title, url, snippets);
        }

        return hits;
    }

    private static IReadOnlyList<SearchHit> ParseTavilyResults(JsonElement root)
    {
        if (!root.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SearchHit>();
        }

        List<SearchHit> hits = new();
        foreach (JsonElement result in results.EnumerateArray())
        {
            AddHit(hits, GetString(result, "title"), GetString(result, "url"), [GetString(result, "content")]);
        }

        return hits;
    }

    private static void AddHit(List<SearchHit> hits, string title, string url, IEnumerable<string> snippets)
    {
        string snippet = Trim(string.Join(" ", snippets.Where(value => !string.IsNullOrWhiteSpace(value))), 700);
        if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(url) || !string.IsNullOrWhiteSpace(snippet))
        {
            hits.Add(new SearchHit(Trim(title, 220), url, snippet));
        }
    }

    private static string GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static void EnsureSuccess(string provider, HttpStatusCode statusCode, string body)
    {
        if (statusCode is >= HttpStatusCode.OK and < HttpStatusCode.MultipleChoices)
        {
            return;
        }

        throw new SearchProviderUnavailableException(
            $"{provider} returned HTTP {(int)statusCode}: {ReadErrorMessage(body)}");
    }

    private static string FormatProviderFailure(string provider, Exception exception) =>
        exception is SearchProviderUnavailableException
            ? exception.Message
            : $"{provider} request failed ({exception.GetType().Name}).";

    private static string ReadErrorMessage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("detail", out JsonElement detail))
            {
                return Trim(detail.GetString() ?? "no error details", 240);
            }

            if (document.RootElement.TryGetProperty("message", out JsonElement message))
            {
                return Trim(message.GetString() ?? "no error details", 240);
            }

            if (document.RootElement.TryGetProperty("error", out JsonElement error))
            {
                return error.ValueKind == JsonValueKind.String
                    ? Trim(error.GetString() ?? "no error details", 240)
                    : Trim(error.GetRawText(), 240);
            }
        }
        catch (JsonException)
        {
            // Return a stable generic message rather than exposing provider response text.
        }

        return "no error details";
    }

    private static string? LoadConfiguredKey(string name)
    {
        string? processValue = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(processValue))
        {
            return NormalizeSecret(processValue);
        }

        foreach (string path in EnumerateDotEnvPaths())
        {
            try
            {
                foreach (string line in File.ReadLines(path))
                {
                    if (TryParseDotEnvLine(line, name, out string value))
                    {
                        return NormalizeSecret(value);
                    }
                }
            }
            catch (IOException)
            {
                // Another process may be writing the file; continue to the next candidate.
            }
            catch (UnauthorizedAccessException)
            {
                // Do not fail the session because an optional .env candidate is unreadable.
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateDotEnvPaths()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            DirectoryInfo? directory = new(start);
            while (directory is not null)
            {
                string path = Path.Combine(directory.FullName, ".env");
                if (seen.Add(path))
                {
                    yield return path;
                }

                directory = directory.Parent;
            }
        }
    }

    private static bool TryParseDotEnvLine(string line, string expectedName, out string value)
    {
        value = string.Empty;
        string trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return false;
        }

        if (trimmed.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..].TrimStart();
        }

        int separator = trimmed.IndexOf('=');
        if (separator <= 0 || !trimmed[..separator].Trim().Equals(expectedName, StringComparison.Ordinal))
        {
            return false;
        }

        value = trimmed[(separator + 1)..].Trim();
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }

        return !string.IsNullOrWhiteSpace(value);
    }

    private static string? NormalizeSecret(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().Trim('"', '\'');

    private static string Trim(string value, int maxCharacters) =>
        string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Length > maxCharacters ? value[..maxCharacters] + "…" : value;

    private sealed record SearchHit(string Title, string Url, string Snippet);

    private sealed class SearchProviderUnavailableException(string message) : Exception(message);
}
