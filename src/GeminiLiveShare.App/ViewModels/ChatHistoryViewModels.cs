using CommunityToolkit.Mvvm.ComponentModel;
using GeminiLiveShare.Core.Storage;

namespace GeminiLiveShare.App.ViewModels;

public sealed partial class ChatSessionViewModel : ObservableObject
{
    public ChatSessionViewModel(
        string sessionId,
        string summary,
        DateTime latestMessageUtc,
        bool isTitleUserEdited = false)
    {
        SessionId = sessionId;
        _summary = summary;
        _latestMessageUtc = latestMessageUtc;
        IsTitleUserEdited = isTitleUserEdited;
    }

    public string SessionId { get; }

    public bool IsTitleUserEdited { get; private set; }

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
    }

    public void SetTitle(string title, bool isUserEdited)
    {
        Summary = title;
        IsTitleUserEdited = isUserEdited;
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

    public DateTime CreatedAtLocal { get; } = message.CreatedAtUtc.ToLocalTime();

    public string TimeLabel { get; } = message.CreatedAtUtc.ToLocalTime().ToString("h:mm:ss tt");
}
