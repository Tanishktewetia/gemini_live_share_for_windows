namespace GeminiLiveShare.Core.Gemini;

internal static class ReconnectPolicy
{
    public static IReadOnlyList<TimeSpan> Delays { get; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16)
    ];

    public static string? HandleAfterSetupFailure(string? attemptedHandle) =>
        string.IsNullOrWhiteSpace(attemptedHandle) ? attemptedHandle : null;
}