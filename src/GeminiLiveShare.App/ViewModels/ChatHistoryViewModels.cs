using CommunityToolkit.Mvvm.ComponentModel;
using GeminiLiveShare.Core.Storage;

namespace GeminiLiveShare.App.ViewModels;

public sealed partial class ChatSessionViewModel : ObservableObject
{
    public ChatSessionViewModel(string sessionId, string summary, DateTime latestMessageUtc)
    {
        SessionId = sessionId;
        _summary = summary;
        _latestMessageUtc = latestMessageUtc;
    }

    public string SessionId { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    private string _summary;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RelativeDate))]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    private DateTime _latestMessageUtc;

    [ObservableProperty]
    private bool _isDeleteConfirmationOpen;

    public string RelativeDate => FormatRelativeDate(LatestMessageUtc);

    public string HeaderText => $"{Summary}  ·  {LatestMessageUtc.ToLocalTime():MMM d, yyyy}";

    public void Update(ChatMessage message)
    {
        if (message.CreatedAtUtc > LatestMessageUtc)
        {
            LatestMessageUtc = message.CreatedAtUtc;
        }

        if (message.Role.Equals("user", StringComparison.OrdinalIgnoreCase) && Summary == "New conversation")
        {
            Summary = CreateSummary(message.Text);
        }
    }

    public static string CreateSummary(string text)
    {
        string normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "New conversation";
        }

        return normalized.Length > 40 ? $"{normalized[..40]}..." : normalized;
    }

    private static string FormatRelativeDate(DateTime utc)
    {
        DateTime local = utc.ToLocalTime();
        DateTime today = DateTime.Today;
        if (local.Date == today)
        {
            return $"Today, {local:h:mm tt}";
        }

        return local.Date == today.AddDays(-1) ? "Yesterday" : local.ToString("MMM d, yyyy");
    }
}

public sealed class ChatMessageViewModel(ChatMessage message)
{
    public int Id { get; } = message.Id;

    public string Text { get; } = message.Text;

    public bool IsUser { get; } = message.Role.Equals("user", StringComparison.OrdinalIgnoreCase);
}