namespace GeminiLiveShare.Core.Gemini;

public sealed class SessionReadyEventArgs(
    bool isReconnect,
    bool attemptedResumption,
    bool wasSessionResumed,
    bool isWebSearchAvailable,
    string webSearchMode = "Off") : EventArgs
{
    public bool IsReconnect { get; } = isReconnect;

    public bool AttemptedResumption { get; } = attemptedResumption;

    public bool WasSessionResumed { get; } = wasSessionResumed;

    public bool IsWebSearchAvailable { get; } = isWebSearchAvailable;

    public string WebSearchMode { get; } = webSearchMode;
}
