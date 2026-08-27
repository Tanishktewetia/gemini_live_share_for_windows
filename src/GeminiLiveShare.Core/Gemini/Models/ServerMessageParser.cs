using System.Text.Json;

namespace GeminiLiveShare.Core.Gemini.Models;

internal sealed record ParsedServerMessage(
    bool SetupComplete,
    string? Error,
    IReadOnlyList<byte[]> AudioChunks,
    bool Interrupted,
    string? InputTranscription,
    string? OutputTranscription,
    bool TurnComplete,
    bool GoAway,
    string? GoAwayTimeLeft,
    bool? Resumable,
    string? NewHandle);

internal static class ServerMessageParser
{
    public static ParsedServerMessage Parse(ReadOnlyMemory<byte> json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string? error = root.TryGetProperty("error", out JsonElement errorElement)
            ? GetServerErrorMessage(errorElement)
            : null;

        List<byte[]> audioChunks = [];
        bool interrupted = false;
        bool turnComplete = false;
        string? inputTranscription = null;
        string? outputTranscription = null;
        if (root.TryGetProperty("serverContent", out JsonElement serverContent))
        {
            interrupted = serverContent.TryGetProperty("interrupted", out JsonElement interruptedElement) &&
                interruptedElement.ValueKind == JsonValueKind.True;
            turnComplete = serverContent.TryGetProperty("turnComplete", out JsonElement turnCompleteElement) &&
                turnCompleteElement.ValueKind == JsonValueKind.True;
            inputTranscription = GetTranscription(serverContent, "inputTranscription");
            outputTranscription = GetTranscription(serverContent, "outputTranscription");
            ReadAudio(serverContent, audioChunks);
        }

        bool goAway = root.TryGetProperty("goAway", out JsonElement goAwayElement);
        string? timeLeft = goAway && goAwayElement.TryGetProperty("timeLeft", out JsonElement timeLeftElement)
            ? timeLeftElement.GetString()
            : null;
        bool? resumable = null;
        string? newHandle = null;
        if (root.TryGetProperty("sessionResumptionUpdate", out JsonElement update))
        {
            if (update.TryGetProperty("resumable", out JsonElement resumableElement) &&
                resumableElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                resumable = resumableElement.GetBoolean();
            }

            if (update.TryGetProperty("newHandle", out JsonElement handleElement))
            {
                newHandle = handleElement.GetString();
            }
        }

        return new ParsedServerMessage(
            root.TryGetProperty("setupComplete", out _), error, audioChunks, interrupted,
            inputTranscription, outputTranscription, turnComplete, goAway, timeLeft, resumable, newHandle);
    }

    private static string? GetTranscription(JsonElement serverContent, string propertyName)
    {
        return serverContent.TryGetProperty(propertyName, out JsonElement transcription) &&
            transcription.TryGetProperty("text", out JsonElement text)
            ? text.GetString()
            : null;
    }

    private static void ReadAudio(JsonElement serverContent, List<byte[]> audioChunks)
    {
        if (!serverContent.TryGetProperty("modelTurn", out JsonElement modelTurn) ||
            !modelTurn.TryGetProperty("parts", out JsonElement parts) || parts.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (JsonElement part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("inlineData", out JsonElement inlineData) &&
                inlineData.TryGetProperty("mimeType", out JsonElement mimeType) &&
                mimeType.GetString()?.StartsWith("audio/pcm", StringComparison.OrdinalIgnoreCase) == true &&
                inlineData.TryGetProperty("data", out JsonElement data) &&
                !string.IsNullOrEmpty(data.GetString()))
            {
                audioChunks.Add(Convert.FromBase64String(data.GetString()!));
            }
        }
    }

    private static string GetServerErrorMessage(JsonElement error)
    {
        string code = error.TryGetProperty("code", out JsonElement codeElement) ? codeElement.ToString() : "unknown code";
        string status = error.TryGetProperty("status", out JsonElement statusElement) ? Sanitize(statusElement.ToString()) : "unknown status";
        string message = error.TryGetProperty("message", out JsonElement messageElement) ? Sanitize(messageElement.ToString()) : "No reason was provided.";
        return $"Gemini Live API error ({code}, {status}): {message}";
    }

    private static string Sanitize(string value)
    {
        string sanitized = new(value.Where(character => !char.IsControl(character)).Take(300).ToArray());
        return sanitized.Length == 0 ? "No reason was provided." : sanitized;
    }
}