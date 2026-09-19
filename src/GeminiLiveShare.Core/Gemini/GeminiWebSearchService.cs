using System.Net;
using System.Text;
using System.Text.Json;

namespace GeminiLiveShare.Core.Gemini;

public sealed class GeminiWebSearchService : IWebSearchService
{
    // These are the lightweight generateContent models verified for the API key used by
    // this app. The Live model name is not automatically valid for regular REST calls.
    private static readonly string[] Models = ["gemini-flash-lite-latest", "gemini-3.5-flash-lite"];
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<WebSearchResult> SearchAsync(string apiKey, string query, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        Exception? lastError = null;
        foreach (string model in Models)
        {
            try
            {
                return await SearchAsync(model, apiKey, query, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSearchUnavailableException ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("No web search model was available.");
    }

    private static async Task<WebSearchResult> SearchAsync(string model, string apiKey, string query, CancellationToken cancellationToken)
    {
        foreach (object tools in BuildToolVariants())
        {
            object request = new
            {
                contents = new[]
                {
                    new
                    {
                        parts = new[]
                        {
                            new
                            {
                                text = "Search the web and answer with concise facts. Include 3-5 source names or domains when possible. Query: " + query
                            }
                        }
                    }
                },
                tools,
                generationConfig = new { temperature = 0.1, maxOutputTokens = 450 }
            };

            using HttpRequestMessage httpRequest = new(
                HttpMethod.Post,
                $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent")
            {
                Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Add("x-goog-api-key", apiKey);

            using HttpResponseMessage response = await HttpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string message = $"{model} returned HTTP {(int)response.StatusCode}: {ReadErrorMessage(body)}";
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests or
                    HttpStatusCode.ServiceUnavailable or HttpStatusCode.InternalServerError)
                {
                    throw new WebSearchUnavailableException(message);
                }

                if ((int)response.StatusCode == 400)
                {
                    // try the next tool schema variant
                    continue;
                }

                throw new InvalidOperationException(message);
            }

            if (!TryParseResult(body, out WebSearchResult result))
            {
                continue;
            }

            return result;
        }

        throw new WebSearchUnavailableException("Web search tool schema was rejected by the API.");
    }

    private static bool TryParseResult(string body, out WebSearchResult result)
    {
        result = new WebSearchResult(string.Empty, Array.Empty<string>(), false);
        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
            candidates.GetArrayLength() == 0 ||
            !candidates[0].TryGetProperty("content", out JsonElement content) ||
            !content.TryGetProperty("parts", out JsonElement parts))
        {
            return false;
        }

        string summary = string.Concat(parts.EnumerateArray()
            .Where(part => !(part.TryGetProperty("thought", out JsonElement thought) && thought.ValueKind == JsonValueKind.True))
            .Select(part => part.TryGetProperty("text", out JsonElement partText) ? partText.GetString() : null))
            .Trim();
        if (string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        List<string> sources = new();
        if (candidates[0].TryGetProperty("groundingMetadata", out JsonElement grounding) &&
            grounding.TryGetProperty("groundingChunks", out JsonElement chunks) &&
            chunks.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement chunk in chunks.EnumerateArray())
            {
                if (!chunk.TryGetProperty("web", out JsonElement web))
                {
                    continue;
                }

                string? title = web.TryGetProperty("title", out JsonElement titleElement)
                    ? titleElement.GetString()
                    : null;
                string? uri = web.TryGetProperty("uri", out JsonElement uriElement)
                    ? uriElement.GetString()
                    : null;
                string? source = !string.IsNullOrWhiteSpace(title)
                    ? title
                    : uri;
                if (!string.IsNullOrWhiteSpace(source))
                {
                    sources.Add(source);
                }
            }
        }

        result = new WebSearchResult(summary, sources.Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray(), true);
        return true;
    }

    private static IEnumerable<object> BuildToolVariants()
    {
        yield return new object[] { new { googleSearch = new { } } };
        yield return new object[] { new { google_search = new { } } };
    }

    private static string ReadErrorMessage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            string message = document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? string.Empty;
            return message.Length > 240 ? message[..240] : message;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "no error details";
        }
    }

    private sealed class WebSearchUnavailableException(string message) : Exception(message);
}
