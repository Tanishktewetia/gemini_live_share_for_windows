using System.Text;
using System.Text.Json;

namespace GeminiLiveShare.Core.Gemini;

public sealed class GeminiTitleGenerationService : ITitleGenerationService
{
    private const string Model = "gemini-3.7-flash";
    private const int MaxTitleLength = 60;
    private static readonly Uri Endpoint = new(
        $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent");
    private static readonly HttpClient HttpClient = new();

    public async Task<string?> GenerateAsync(
        string firstUserMessage,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(firstUserMessage);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        string prompt = "Create a concise title for this conversation. Return only the title, with 3 to 6 words, " +
                        "no quotation marks, no punctuation at the end, and never more than 60 characters. " +
                        $"Conversation opener: {firstUserMessage.Trim()[..Math.Min(firstUserMessage.Trim().Length, 2000)]}";
        object request = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[] { new { text = prompt } }
                }
            },
            generationConfig = new
            {
                temperature = 0.2,
                maxOutputTokens = 20
            }
        };

        using HttpRequestMessage httpRequest = new(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json")
        };
        httpRequest.Headers.Add("x-goog-api-key", apiKey);

        using HttpResponseMessage response = await HttpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using Stream responseStream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(responseStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!document.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
            candidates.GetArrayLength() == 0)
        {
            return null;
        }

        JsonElement parts = candidates[0].GetProperty("content").GetProperty("parts");
        string? title = parts.EnumerateArray()
            .Select(part => part.TryGetProperty("text", out JsonElement text) ? text.GetString() : null)
            .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        title = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim('"', '\'', '.', ',', ':', ';', ' ', '\r', '\n', '\t');
        return string.IsNullOrWhiteSpace(title)
            ? null
            : title[..Math.Min(title.Length, MaxTitleLength)];
    }
}
