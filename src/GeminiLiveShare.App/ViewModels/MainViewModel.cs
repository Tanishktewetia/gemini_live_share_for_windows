using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeminiLiveShare.Core.BrowserAgent;
using GeminiLiveShare.Core.Gemini;
using GeminiLiveShare.Core.Security;
using GeminiLiveShare.Core.Storage;

namespace GeminiLiveShare.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SessionOrchestrator _orchestrator;
    private readonly IApiKeyVaultService _apiKeyVault;
    private readonly IChatHistoryRepository _history;
    private readonly ITitleGenerationService _titleGeneration;
    private readonly SynchronizationContext _uiContext;
    private readonly HashSet<string> _titleGenerationStarted = [];
    private int _loadVersion;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StartStopLabel))]
    private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _connectionStatus = "Disconnected";
    [ObservableProperty] private bool _isMicrophoneOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SessionHeader))]
    private ChatSessionViewModel? _selectedSession;
    [ObservableProperty] private bool _hasMessages;

    public MainViewModel(
        SessionOrchestrator orchestrator,
        IApiKeyVaultService apiKeyVault,
        IChatHistoryRepository history,
        ITitleGenerationService? titleGeneration = null,
        BrowserAgentBridge? browserAgentBridge = null)
    {
        _orchestrator = orchestrator;
        _apiKeyVault = apiKeyVault;
        _history = history;
        _titleGeneration = titleGeneration ?? new GeminiTitleGenerationService();
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _orchestrator.StatusChanged += OnStatusChanged;
        _orchestrator.MicrophoneStateChanged += OnMicrophoneStateChanged;
        _history.MessageAdded += OnMessageAdded;
        if (browserAgentBridge is not null)
        {
            browserAgentBridge.StatusChanged += OnBrowserAgentStatusChanged;
        }
        _ = LoadSessionsAsync();
    }

    public ObservableCollection<ChatSessionViewModel> Sessions { get; } = [];
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public string StartStopLabel => IsRunning ? "Stop Conversation" : "Start Conversation";
    public string SessionHeader => SelectedSession?.HeaderText ?? "No conversation selected";
    public event EventHandler? SettingsRequested;

    [RelayCommand(CanExecute = nameof(CanStartOrStop))]
    private async Task StartOrStopAsync()
    {
        IsBusy = true;
        NotifyCommands();
        try
        {
            if (IsRunning)
                await StopAsync();
            else
            {
                ClearSelection();
                await StartAsync();
            }
        }
        catch (Exception ex)
        {
            IsRunning = false;
            IsMicrophoneOn = false;
            ConnectionStatus = $"Unable to start conversation: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartOrStop))]
    private async Task NewConversationAsync()
    {
        IsBusy = true;
        NotifyCommands();
        try
        {
            if (IsRunning)
                await StopAsync();
            ClearSelection();
            await StartAsync();
        }
        catch (Exception ex)
        {
            IsRunning = false;
            IsMicrophoneOn = false;
            ConnectionStatus = $"Unable to start conversation: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleMicrophone))]
    private async Task ToggleMicrophoneAsync()
    {
        IsBusy = true;
        NotifyCommands();
        try
        {
            await _orchestrator.SetMicrophoneEnabledAsync(!IsMicrophoneOn);
            IsMicrophoneOn = _orchestrator.IsMicrophoneOn;
        }
        catch (Exception ex)
        {
            IsMicrophoneOn = _orchestrator.IsMicrophoneOn;
            ConnectionStatus = $"Unable to change microphone state: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    [RelayCommand] private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);
    [RelayCommand] private void RequestDelete(ChatSessionViewModel session) => session.IsDeleteConfirmationOpen = true;
    [RelayCommand] private void CancelDelete(ChatSessionViewModel session) => session.IsDeleteConfirmationOpen = false;

    public async Task RenameSessionAsync(ChatSessionViewModel session, string title)
    {
        ArgumentNullException.ThrowIfNull(session);
        string normalizedTitle = string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return;
        }

        normalizedTitle = normalizedTitle[..Math.Min(normalizedTitle.Length, 80)];
        session.SetTitle(normalizedTitle, true);
        await _history.SetSessionTitleAsync(session.SessionId, normalizedTitle, true);
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync(ChatSessionViewModel session)
    {
        session.IsDeleteConfirmationOpen = false;
        await _history.DeleteSessionAsync(session.SessionId);
        Sessions.Remove(session);
        if (SelectedSession == session)
            ClearSelection();
    }

    partial void OnSelectedSessionChanged(ChatSessionViewModel? value)
    {
        OnPropertyChanged(nameof(SessionHeader));
        _ = LoadSelectedAsync(value);
    }

    private async Task LoadSessionsAsync()
    {
        IReadOnlyList<ChatMessage> all = await _history.GetAllAsync();
        IReadOnlyList<ChatSessionMetadata> metadata = await _history.GetSessionMetadataAsync();
        Dictionary<string, ChatSessionMetadata> metadataBySession = metadata
            .ToDictionary(item => item.SessionId, StringComparer.Ordinal);
        foreach (var group in all.GroupBy(message => message.SessionId)
                     .OrderByDescending(group => group.Max(message => message.CreatedAtUtc)))
        {
            ChatMessage? firstUser = group.Where(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase))
                .OrderBy(message => message.Id).FirstOrDefault();
            metadataBySession.TryGetValue(group.Key, out ChatSessionMetadata? sessionMetadata);
            Sessions.Add(new ChatSessionViewModel(group.Key,
                sessionMetadata?.Title ?? ChatSessionViewModel.CreateSummary(firstUser?.Text ?? "New conversation"),
                group.Max(message => message.CreatedAtUtc),
                sessionMetadata?.IsTitleUserEdited == true));
        }
    }

    private async Task LoadSelectedAsync(ChatSessionViewModel? session)
    {
        int version = Interlocked.Increment(ref _loadVersion);
        if (session is null)
        {
            Messages.Clear();
            HasMessages = false;
            return;
        }

        IReadOnlyList<ChatMessage> messages = await _history.GetBySessionAsync(session.SessionId);
        if (version != _loadVersion || SelectedSession != session)
            return;
        Messages.Clear();
        foreach (ChatMessage message in messages)
            Messages.Add(new ChatMessageViewModel(message));
        HasMessages = Messages.Count > 0;
    }

    private async Task StartAsync()
    {
        string? key = _apiKeyVault.GetApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            ConnectionStatus = "No API key is saved.";
            SettingsRequested?.Invoke(this, EventArgs.Empty);
            return;
        }
        await _orchestrator.StartAsync(key);
        IsRunning = true;
        IsMicrophoneOn = _orchestrator.IsMicrophoneOn;
        ConnectionStatus = "Connected";
    }

    private async Task StopAsync()
    {
        await _orchestrator.StopAsync();
        IsRunning = false;
        IsMicrophoneOn = false;
        ConnectionStatus = "Disconnected";
        if (SelectedSession is { IsTitleUserEdited: false } session && _titleGenerationStarted.Add(session.SessionId))
        {
            await Task.Delay(750);
            await GenerateTitleAsync(session);
        }
    }

    private void ClearSelection()
    {
        SelectedSession = null;
        Messages.Clear();
        HasMessages = false;
    }

    private bool CanStartOrStop() => !IsBusy;
    private bool CanToggleMicrophone() => IsRunning && !IsBusy;
    private void NotifyCommands()
    {
        StartOrStopCommand.NotifyCanExecuteChanged();
        NewConversationCommand.NotifyCanExecuteChanged();
        ToggleMicrophoneCommand.NotifyCanExecuteChanged();
    }

    private void OnStatusChanged(object? sender, string status) => _uiContext.Post(_ => ConnectionStatus = status, null);
    private void OnBrowserAgentStatusChanged(object? sender, string status) => _uiContext.Post(_ => ConnectionStatus = status, null);
    private void OnMicrophoneStateChanged(object? sender, EventArgs e) => _uiContext.Post(_ =>
    {
        IsMicrophoneOn = _orchestrator.IsMicrophoneOn;
        ToggleMicrophoneCommand.NotifyCanExecuteChanged();
    }, null);
    private void OnMessageAdded(object? sender, ChatMessageAddedEventArgs e) => _uiContext.Post(_ => AddLiveMessage(e.Message), null);

    private void AddLiveMessage(ChatMessage message)
    {
        ChatSessionViewModel? session = Sessions.FirstOrDefault(item => item.SessionId == message.SessionId);
        if (session is null)
        {
            session = new ChatSessionViewModel(message.SessionId,
                message.Role.Equals("user", StringComparison.OrdinalIgnoreCase)
                    ? ChatSessionViewModel.CreateSummary(message.Text) : "New conversation",
                message.CreatedAtUtc);
            Sessions.Insert(0, session);
        }
        else
        {
            session.Update(message);
            Sessions.Move(Sessions.IndexOf(session), 0);
        }

        if (IsRunning && SelectedSession?.SessionId != message.SessionId)
            SelectedSession = session;
        if (SelectedSession?.SessionId == message.SessionId && Messages.All(item => item.Id != message.Id))
        {
            Messages.Add(new ChatMessageViewModel(message));
            HasMessages = true;
        }

    }

    private async Task GenerateTitleAsync(ChatSessionViewModel session)
    {
        try
        {
            string? apiKey = _apiKeyVault.GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return;
            }

            IReadOnlyList<ChatMessage> messages = await _history.GetBySessionAsync(session.SessionId).ConfigureAwait(false);
            if (!messages.Any(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            string? title = await _titleGeneration
                .GenerateAsync(messages, apiKey).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(title))
            {
                return;
            }

            _uiContext.Post(_ => ApplyGeneratedTitle(session, title), null);
        }
        catch (Exception ex)
        {
            _uiContext.Post(_ => ConnectionStatus = $"Unable to generate conversation title: {ex.Message}", null);
        }
    }

    private void ApplyGeneratedTitle(ChatSessionViewModel session, string title)
    {
        if (session.IsTitleUserEdited || !Sessions.Contains(session))
        {
            return;
        }

        session.SetTitle(title, false);
        _ = PersistGeneratedTitleAsync(session, title);
    }

    private async Task PersistGeneratedTitleAsync(ChatSessionViewModel session, string title)
    {
        try
        {
            await _history.SetSessionTitleAsync(session.SessionId, title, false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _uiContext.Post(_ => ConnectionStatus = $"Unable to save conversation title: {ex.Message}", null);
        }
    }
}
