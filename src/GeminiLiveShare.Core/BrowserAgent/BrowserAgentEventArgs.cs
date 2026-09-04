using System.Text.Json;

namespace GeminiLiveShare.Core.BrowserAgent;

public sealed class BrowserAgentEventArgs(JsonElement payload) : EventArgs
{
    public JsonElement Payload { get; } = payload.Clone();
}
