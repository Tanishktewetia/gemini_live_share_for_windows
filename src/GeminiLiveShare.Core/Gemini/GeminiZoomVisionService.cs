using System.Net;
using System.Text;
using System.Text.Json;

namespace GeminiLiveShare.Core.Gemini;

public sealed class GeminiZoomVisionService : IZoomVisionService
{
    // Non-Live call for zoomed crops. Keep a pinned fallback if alias is unavailable.
    // Keep zoom on the lightweight regular REST models; the Live model name is not
    // automatically valid for generateContent image requests.
    private static readonly string[] Models = ["gemini-flash-lite-latest", "gemini-3.5-flash-lite"];
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };

    public async Task<string> AnalyzeAsync(string apiKey, byte[] croppedJpeg, string question, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentNullException.ThrowIfNull(croppedJpeg);
        ArgumentException.ThrowIfNullOrWhiteSpace(question);

        Exception? lastError = null;
        foreach (string model in Models)
        {
            try
            {
                return await AnalyzeAsync(model, apiKey, croppedJpeg, question, cancellationToken).ConfigureAwait(false);
            }
            catch (ZoomModelUnavailableException ex)
            {
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("No zoom vision model was available.");
    }

    private static async Task<string> AnalyzeAsync(string model, string apiKey, byte[] croppedJpeg, string question, CancellationToken cancellationToken)
    {
        object request = new
        {
            contents = new[]
            {
                new
                {
                    parts = new object[]
                    {
                        new { text = BuildPrompt(question) },
                        new { inlineData = new { mimeType = "image/jpeg", data = Convert.ToBase64String(croppedJpeg) } }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.1,
                maxOutputTokens = 300,
                mediaResolution = "MEDIA_RESOLUTION_HIGH"
            }
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
                throw new ZoomModelUnavailableException(message);
            }

            throw new InvalidOperationException(message);
        }

        using JsonDocument document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("candidates", out JsonElement candidates) ||
            candidates.GetArrayLength() == 0 ||
            !candidates[0].TryGetProperty("content", out JsonElement content) ||
            !content.TryGetProperty("parts", out JsonElement parts))
        {
            return "I couldn't read anything reliable from that zoomed region.";
        }

        string text = string.Concat(parts.EnumerateArray()
            .Where(part => !(part.TryGetProperty("thought", out JsonElement thought) && thought.ValueKind == JsonValueKind.True))
            .Select(part => part.TryGetProperty("text", out JsonElement partText) ? partText.GetString() : null));
        return string.IsNullOrWhiteSpace(text)
            ? "I couldn't read anything reliable from that zoomed region."
            : text.Trim();
    }

    private static string BuildPrompt(string question) =>
        "You are reading a zoomed crop of the user's screen. " +
        "Answer only from clearly visible evidence in this crop. " +
        "If text is blurry or unreadable, say exactly what is unclear and do not guess.\n" +
        $"User question: {question}";

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

    private sealed class ZoomModelUnavailableException(string message) : Exception(message);
}
