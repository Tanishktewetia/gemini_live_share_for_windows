using System.Text.Json;

namespace GeminiLiveShare.Core.BrowserAgent;

public sealed class BrowserAgentToolRegistry
{
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<JsonElement>>> _handlers =
        new(StringComparer.Ordinal);

    public BrowserAgentToolRegistry()
    {
        _handlers["get_active_page"] = static (_, _) =>
            Task.FromResult(JsonSerializer.SerializeToElement(new { }));
    }

    public bool Contains(string toolName) =>
        !string.IsNullOrWhiteSpace(toolName) && _handlers.ContainsKey(toolName);
}