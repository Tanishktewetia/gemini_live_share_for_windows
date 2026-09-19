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
    // Titles work like a chat app sidebar: "New conversation" until the first real exchange, then a model-written
    // title, refined once more from the full transcript when the conversation ends. Never the first spoken line.
    private const string DefaultTitle = "New conversation";
    private const int MaxEarlyTitleAttempts = 3;
    private const int MaxStartupTitleBackfill = 20;
    private readonly Dictionary<string, int> _earlyTitleAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _titleRequestVersions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _finalTitleRequested = new(StringComparer.Ordinal);
    private string? _liveSessionId;
    private int _loadVersion;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(StartStopLabel))]
    private bool _isRunning;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _connectionStatus = "Disconnected";
    [ObservableProperty] private bool _isMicrophoneOn;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(SessionHeader)), NotifyPropertyChangedFor(nameof(HasSelectedSession))]
    private ChatSessionViewModel? _selectedSession;
    [ObservableProperty] private bool _hasMessages;
    [ObservableProperty] private bool _showWebSearchUnavailable;
    [ObservableProperty] private bool _showWebSearchFallback;
    [ObservableProperty] private bool _showReconnected;

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
        _orchestrator.ConnectionStateChanged += OnConnectionStateChanged;
        _history.MessageAdded += OnMessageAdded;
        if (browserAgentBridge is not null)
        {
            browserAgentBridge.StatusChanged += OnBrowserAgentStatusChanged;
        }
        _ = LoadSessionsAsync();
    }

    public ObservableCollection<ChatSessionViewModel> Sessions { get; } = [];
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public bool HasSelectedSession => SelectedSession is not null;
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

    public Task<IReadOnlyList<ChatMessage>> GetSessionMessagesAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return _history.GetBySessionAsync(sessionId);
    }

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

    partial void OnSelectedSessionChanged(ChatSessionViewModel? oldValue, ChatSessionViewModel? newValue)
    {
        // The header must follow title changes (early/final generated titles, renames) of the open conversation,
        // not only changes of which conversation is selected.
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnSelectedSessionPropertyChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnSelectedSessionPropertyChanged;
        OnPropertyChanged(nameof(SessionHeader));
        _ = LoadSelectedAsync(newValue);
    }

    private void OnSelectedSessionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatSessionViewModel.HeaderText))
            OnPropertyChanged(nameof(SessionHeader));
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
            metadataBySession.TryGetValue(group.Key, out ChatSessionMetadata? sessionMetadata);
            Sessions.Add(new ChatSessionViewModel(group.Key,
                string.IsNullOrWhiteSpace(sessionMetadata?.Title) ? DefaultTitle : sessionMetadata.Title,
                group.Max(message => message.CreatedAtUtc),
                sessionMetadata?.IsTitleUserEdited == true));
        }

        await BackfillMissingTitlesAsync();
    }

    private async Task BackfillMissingTitlesAsync()
    {
        // Earlier builds never stored a title (the title model returned 404), so name those conversations now.
        ChatSessionViewModel[] untitled = Sessions
            .Where(session => session.Summary == DefaultTitle && !session.IsTitleUserEdited)
            .Take(MaxStartupTitleBackfill)
            .ToArray();
        foreach (ChatSessionViewModel session in untitled)
        {
            if (session.SessionId == _liveSessionId || !Sessions.Contains(session))
            {
                continue;
            }

            if (!await GenerateTitleAsync(session, reportErrors: false))
            {
                return;
            }
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
        RefreshConnectionBadges();
    }

    private async Task StopAsync()
    {
        await _orchestrator.StopAsync();
        IsRunning = false;
        IsMicrophoneOn = false;
        ConnectionStatus = "Disconnected";
        RefreshConnectionBadges();
        string? liveSessionId = _liveSessionId;
        _liveSessionId = null;
        ChatSessionViewModel? session = Sessions.FirstOrDefault(item => item.SessionId == liveSessionId);
        if (session is { IsTitleUserEdited: false } && _finalTitleRequested.Add(session.SessionId))
        {
            // Let the final transcription fragments reach the database, then name the whole conversation.
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

    private void OnStatusChanged(object? sender, string status) => _uiContext.Post(_ =>
    {
        ConnectionStatus = status;
        RefreshConnectionBadges();
    }, null);
    private void OnBrowserAgentStatusChanged(object? sender, string status) => _uiContext.Post(_ => ConnectionStatus = status, null);

    private void OnConnectionStateChanged(object? sender, EventArgs e) => _uiContext.Post(_ => RefreshConnectionBadges(), null);
    private void OnMicrophoneStateChanged(object? sender, EventArgs e) => _uiContext.Post(_ =>
    {
        IsMicrophoneOn = _orchestrator.IsMicrophoneOn;
        ToggleMicrophoneCommand.NotifyCanExecuteChanged();
    }, null);
    private void OnMessageAdded(object? sender, ChatMessageAddedEventArgs e) => _uiContext.Post(_ => AddLiveMessage(e.Message), null);

    private void RefreshConnectionBadges()
    {
        ShowWebSearchUnavailable = IsRunning && !_orchestrator.IsWebSearchAvailable;
        ShowWebSearchFallback = IsRunning && _orchestrator.WebSearchMode.Equals("App web_search", StringComparison.Ordinal);
        ShowReconnected = IsRunning && _orchestrator.HasReconnected;
    }
    private void AddLiveMessage(ChatMessage message)
    {
        ChatSessionViewModel? session = Sessions.FirstOrDefault(item => item.SessionId == message.SessionId);
        if (session is null)
        {
            session = new ChatSessionViewModel(message.SessionId, DefaultTitle, message.CreatedAtUtc);
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

        if (IsRunning)
        {
            _liveSessionId = message.SessionId;
            RequestEarlyTitle(session, message);
        }
    }

    private void RequestEarlyTitle(ChatSessionViewModel session, ChatMessage message)
    {
        // Name the conversation as soon as Gemini has answered something, like a chat app does after the first
        // exchange. Greeting-only openings return no title, so retry on the next few assistant replies.
        if (!message.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ||
            session.IsTitleUserEdited || session.Summary != DefaultTitle)
        {
            return;
        }

        int attempts = _earlyTitleAttempts.GetValueOrDefault(session.SessionId);
        if (attempts >= MaxEarlyTitleAttempts)
        {
            return;
        }

        _earlyTitleAttempts[session.SessionId] = attempts + 1;
        _ = GenerateTitleAsync(session);
    }

    /// <returns>False when titles cannot be generated right now (no key or request failure).</returns>
    private async Task<bool> GenerateTitleAsync(ChatSessionViewModel session, bool reportErrors = true)
    {
        int version = _titleRequestVersions.GetValueOrDefault(session.SessionId) + 1;
        _titleRequestVersions[session.SessionId] = version;
        try
        {
            string? apiKey = _apiKeyVault.GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return false;
            }

            IReadOnlyList<ChatMessage> messages = await _history.GetBySessionAsync(session.SessionId).ConfigureAwait(false);
            if (!messages.Any(message => message.Role.Equals("user", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            string? title = await _titleGeneration
                .GenerateAsync(messages, apiKey).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(title))
            {
                _uiContext.Post(_ => ApplyGeneratedTitle(session, title, version), null);
            }

            return true;
        }
        catch (Exception ex)
        {
            if (reportErrors)
            {
                _uiContext.Post(_ => ConnectionStatus = $"Unable to generate conversation title: {ex.Message}", null);
            }

            return false;
        }
    }

    private void ApplyGeneratedTitle(ChatSessionViewModel session, string title, int version)
    {
        // An older request (e.g. the early title) must not overwrite a newer one (the end-of-conversation title).
        if (session.IsTitleUserEdited || !Sessions.Contains(session) ||
            _titleRequestVersions.GetValueOrDefault(session.SessionId) != version)
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
