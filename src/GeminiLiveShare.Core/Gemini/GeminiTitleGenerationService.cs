using System.Net;
using System.Text;
using System.Text.Json;
using GeminiLiveShare.Core.Storage;

namespace GeminiLiveShare.Core.Gemini;

public sealed class GeminiTitleGenerationService : ITitleGenerationService
{
    // "-latest" aliases follow Google's current model, so a retired pinned model cannot silently break titles
    // again (gemini-2.5-flash started returning 404 "no longer available to new users"). The flash-lite tier
    // answers in about 1 s without spending the output budget on thinking. The pinned fallback is used when
    // the alias is unavailable or overloaded.
    private static readonly string[] Models = ["gemini-flash-lite-latest", "gemini-3.5-flash-lite"];
    private const int MaxTitleLength = 60;
    private const string NoTopicMarker = "NONE";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<string?> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        string transcript = BuildTranscript(messages);
        if (string.IsNullOrWhiteSpace(transcript))
        {
            return null;
        }

        string prompt =
            "Write a title for the conversation below, the way a chat app names a conversation in its sidebar.\n" +
            "Rules:\n" +
            "- 2 to 6 words, Title Case, in the language the user speaks.\n" +
            "- Capture the user's main goal or topic across the whole conversation, not its first line.\n" +
            "- Ignore greetings, mic checks and small talk (for example \"hello\", \"can you hear me\").\n" +
            "- Be specific: prefer \"Fixing WPF Microphone Echo\" over \"Technical Help\".\n" +
            "- No quotation marks, emoji, or trailing punctuation. Return only the title.\n" +
            $"- If there is no real topic yet (only greetings or noise), return exactly {NoTopicMarker}.\n\n" +
            $"Conversation transcript (voice, automatically transcribed):\n{transcript}";

        Exception? lastError = null;
        foreach (string model in Models)
        {
            try
            {
                string? raw = await RequestTitleAsync(model, prompt, apiKey, cancellationToken).ConfigureAwait(false);
                return CleanTitle(raw);
            }
            catch (TitleModelUnavailableException ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("No title model was available.");
    }

    internal static string BuildTranscript(IReadOnlyList<ChatMessage> messages)
    {
        // Voice transcription arrives in fragments, so merge consecutive rows from the same speaker.
        List<(string Role, StringBuilder Text)> turns = [];
        foreach (ChatMessage message in messages)
        {
            string text = message.Text?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                continue;
            }

            string role = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) ? "User" : "Assistant";
            if (turns.Count > 0 && turns[^1].Role == role)
            {
                turns[^1].Text.Append(' ').Append(text);
            }
            else
            {
                turns.Add((role, new StringBuilder(text)));
            }
        }

        // Keep the opening and the most recent turns; long voice sessions can exceed a useful prompt size.
        IEnumerable<(string Role, StringBuilder Text)> selected = turns.Count <= 30
            ? turns
            : turns.Take(10).Concat(turns.TakeLast(20));
        string transcript = string.Join("\n", selected.Select(turn =>
        {
            string text = turn.Text.ToString();
            return $"{turn.Role}: {(text.Length > 600 ? text[..600] + "..." : text)}";
        }));
        return transcript.Length > 12_000 ? transcript[..12_000] : transcript;
    }

    internal static string? CleanTitle(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string title = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
        title = title.Replace("*", string.Empty).Replace("#", string.Empty).Trim();
        if (title.StartsWith("Title:", StringComparison.OrdinalIgnoreCase))
        {
            title = title["Title:".Length..];
        }

        title = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Trim('"', '\'', '`', '.', ',', ':', ';', '!', ' ', '“', '”');
        if (title.Length == 0 || title.Equals(NoTopicMarker, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return title.Length <= MaxTitleLength ? title : title[..MaxTitleLength].TrimEnd();
    }

    private static async Task<string?> RequestTitleAsync(
        string model,
        string prompt,
        string apiKey,
        CancellationToken cancellationToken)
    {
        object request = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            // Leave headroom so a model that thinks can still finish writing the title.
            generationConfig = new { temperature = 0.3, maxOutputTokens = 256 }
        };

        using HttpRequestMessage httpRequest = new(
            HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent")
        {
            Content = new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Add("x-goog-api-key", apiKey);

        using HttpResponseMessage response = await HttpClient
            .SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string message = $"{model} returned HTTP {(int)response.StatusCode}: {ReadErrorMessage(body)}";
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests or
                HttpStatusCode.ServiceUnavailable or HttpStatusCode.InternalServerError)
            {
                throw new TitleModelUnavailableException(message);
            }

            throw new InvalidOperationException(message);
        }

        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
            candidates.GetArrayLength() == 0 ||
            !candidates[0].TryGetProperty("content", out JsonElement content) ||
            !content.TryGetProperty("parts", out JsonElement parts))
        {
            return null;
        }

        return string.Concat(parts.EnumerateArray()
            .Where(part => !(part.TryGetProperty("thought", out JsonElement thought) && thought.ValueKind == JsonValueKind.True))
            .Select(part => part.TryGetProperty("text", out JsonElement text) ? text.GetString() : null));
    }

    private static string ReadErrorMessage(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            string message = document.RootElement.GetProperty("error").GetProperty("message").GetString() ?? string.Empty;
            return message.Length > 200 ? message[..200] : message;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return "no error details";
        }
    }

    private sealed class TitleModelUnavailableException(string message) : Exception(message);
}
