using System.Text;
using System.Text.Json;
using GeminiLiveShare.Core.Storage;

namespace GeminiLiveShare.Core.Gemini;

public sealed class GeminiTitleGenerationService : ITitleGenerationService
{
    private const string Model = "gemini-2.5-flash";
    private const int MaxTitleLength = 60;
    private static readonly Uri Endpoint = new(
        $"https://generativelanguage.googleapis.com/v1beta/models/{Model}:generateContent");
    private static readonly HttpClient HttpClient = new();

    public async Task<string?> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        string conversation = string.Join("\n", messages
            .Where(message => !string.IsNullOrWhiteSpace(message.Text))
            .Select(message => $"{message.Role}: {message.Text.Trim()}")
            .TakeLast(40));
        ArgumentException.ThrowIfNullOrWhiteSpace(conversation);

        string prompt = "You are naming a completed conversation. Infer the main topic from the entire transcript, " +
                "not just its first line. Create a concise descriptive title with 3 to 6 words. Return only the title, " +
                        "no quotation marks, no punctuation at the end, and never more than 60 characters. " +
                        $"Conversation transcript:\n{conversation[..Math.Min(conversation.Length, 8000)]}";
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
