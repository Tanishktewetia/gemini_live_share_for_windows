using System.Text.Json;

namespace GeminiLiveShare.Core.Gemini;

public sealed record ToolCallRequest(string Id, string Name, JsonElement Args);

public sealed record ToolResponsePayload(string Id, string Name, JsonElement Response);

public sealed class ToolCallsEventArgs(IReadOnlyList<ToolCallRequest> calls) : EventArgs
{
    public IReadOnlyList<ToolCallRequest> Calls { get; } = calls;
}
