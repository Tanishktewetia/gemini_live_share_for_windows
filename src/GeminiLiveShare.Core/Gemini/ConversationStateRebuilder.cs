using System.Text;
using GeminiLiveShare.Core.BrowserAgent;
using GeminiLiveShare.Core.BrowserAgent.Models;
using GeminiLiveShare.Core.Storage;
using System.Text.Json;

namespace GeminiLiveShare.Core.Gemini;

public sealed class ConversationStateRebuilder(
    IChatHistoryRepository chatHistory,
    BrowserAgentBridge? browserAgentBridge = null)
{
    private const int ReconnectTurns = 8;
    private readonly IChatHistoryRepository _chatHistory = chatHistory;
    private readonly BrowserAgentBridge? _browserAgentBridge = browserAgentBridge;

    public async Task<string?> BuildReconnectContextAsync(
        string sessionId,
        bool screenShareEnabled,
        bool includeBrowserPageContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        IReadOnlyList<ChatMessage> allTurns = await _chatHistory.GetBySessionAsync(sessionId).ConfigureAwait(false);
        ChatMessage[] recentTurns = allTurns
            .Where(message => !string.IsNullOrWhiteSpace(message.Text))
            .TakeLast(ReconnectTurns)
            .ToArray();

        string? browserContext = includeBrowserPageContext
            ? await BuildBrowserPageContextSummaryAsync(cancellationToken).ConfigureAwait(false)
            : null;

        if (recentTurns.Length == 0 && string.IsNullOrWhiteSpace(browserContext))
        {
            return null;
        }

        StringBuilder builder = new();
        builder.AppendLine("App notice (not spoken by the user): connection recovered with a fresh Gemini session.");
        builder.AppendLine("Continue the same conversation immediately. Do not greet again.");
        builder.AppendLine($"Current app state: screen sharing is {(screenShareEnabled ? "ON" : "OFF") }.");

        if (!string.IsNullOrWhiteSpace(browserContext))
        {
            builder.AppendLine(browserContext);
        }

        if (recentTurns.Length > 0)
        {
            builder.AppendLine("Recent conversation turns (oldest to newest):");
            foreach (ChatMessage turn in recentTurns)
            {
                string role = turn.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "Assistant" : "User";
                builder.Append("- ").Append(role).Append(": ").AppendLine(NormalizeWhitespace(turn.Text));
            }
        }

        return builder.ToString().Trim();
    }

    public async Task<string?> BuildBrowserPageContextNoticeAsync(CancellationToken cancellationToken = default)
    {
        string? summary = await BuildBrowserPageContextSummaryAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return null;
        }

        return "Browser page context was explicitly requested by the user. " +
            "Use only the supplied URL, title, and field metadata. Do not invent fields or values. " +
            "Password fields are intentionally omitted. Button fields are controls, not fillable text fields.\n" +
            summary;
    }

    private async Task<string?> BuildBrowserPageContextSummaryAsync(CancellationToken cancellationToken)
    {
        if (_browserAgentBridge is null)
        {
            return null;
        }

        using JsonDocument emptyArguments = JsonDocument.Parse("{}");
        ToolCallResult page = await _browserAgentBridge
            .SendToolCallAsync("get_active_page", emptyArguments.RootElement)
            .ConfigureAwait(false);
        ToolCallResult fields = await _browserAgentBridge
            .SendToolCallAsync("get_form_fields", emptyArguments.RootElement)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return $"Active page: {page.Payload.GetRawText()}\nForm fields: {fields.Payload.GetRawText()}";
    }

    private static string NormalizeWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
