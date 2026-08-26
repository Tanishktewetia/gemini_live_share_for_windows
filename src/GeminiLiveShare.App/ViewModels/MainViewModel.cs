using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GeminiLiveShare.Core.Gemini;
using GeminiLiveShare.Core.Security;

namespace GeminiLiveShare.App.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly SessionOrchestrator _sessionOrchestrator;
    private readonly IApiKeyVaultService _apiKeyVault;
    private readonly SynchronizationContext _uiContext;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopLabel))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _connectionStatus = "Disconnected";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicrophoneLabel))]
    private bool _isMicrophoneOn;

    public MainViewModel(SessionOrchestrator sessionOrchestrator, IApiKeyVaultService apiKeyVault)
    {
        _sessionOrchestrator = sessionOrchestrator;
        _apiKeyVault = apiKeyVault;
        _uiContext = SynchronizationContext.Current ?? new SynchronizationContext();
        _sessionOrchestrator.StatusChanged += OnStatusChanged;
        _sessionOrchestrator.MicrophoneStateChanged += OnMicrophoneStateChanged;
        AddLog("Application ready. Save an API key in Settings, then start a conversation.");
    }

    public ObservableCollection<string> LogEntries { get; } = [];

    public string StartStopLabel => IsRunning ? "Stop conversation" : "Start conversation";

    public string MicrophoneLabel => IsMicrophoneOn ? "🎙 Microphone ON" : "🎙 Microphone OFF";

    public event EventHandler? SettingsRequested;

    [RelayCommand(CanExecute = nameof(CanStartOrStop))]
    private async Task StartOrStopAsync()
    {
        IsBusy = true;
        StartOrStopCommand.NotifyCanExecuteChanged();
        try
        {
            if (IsRunning)
            {
                await _sessionOrchestrator.StopAsync();
                IsRunning = false;
                IsMicrophoneOn = false;
                ConnectionStatus = "Disconnected";
                ToggleMicrophoneCommand.NotifyCanExecuteChanged();
                return;
            }

            string? apiKey = _apiKeyVault.GetApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                AddLog("No API key is saved. Open Settings and save one first.");
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            await _sessionOrchestrator.StartAsync(apiKey);
            IsRunning = true;
            IsMicrophoneOn = _sessionOrchestrator.IsMicrophoneOn;
            ConnectionStatus = "Connected";
            ToggleMicrophoneCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            IsRunning = false;
            IsMicrophoneOn = false;
            ConnectionStatus = "Disconnected";
            AddLog($"Unable to start conversation: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            StartOrStopCommand.NotifyCanExecuteChanged();
            ToggleMicrophoneCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanToggleMicrophone))]
    private async Task ToggleMicrophoneAsync()
    {
        IsBusy = true;
        StartOrStopCommand.NotifyCanExecuteChanged();
        ToggleMicrophoneCommand.NotifyCanExecuteChanged();
        try
        {
            await _sessionOrchestrator.SetMicrophoneEnabledAsync(!IsMicrophoneOn);
            IsMicrophoneOn = _sessionOrchestrator.IsMicrophoneOn;
        }
        catch (Exception ex)
        {
            IsMicrophoneOn = _sessionOrchestrator.IsMicrophoneOn;
            AddLog($"Unable to change microphone state: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            StartOrStopCommand.NotifyCanExecuteChanged();
            ToggleMicrophoneCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void OpenSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private bool CanStartOrStop() => !IsBusy;

    private bool CanToggleMicrophone() => IsRunning && !IsBusy;

    private void OnStatusChanged(object? sender, string status)
    {
        _uiContext.Post(_ =>
        {
            ConnectionStatus = status;
            AddLog(status);
        }, null);
    }

    private void OnMicrophoneStateChanged(object? sender, EventArgs e)
    {
        _uiContext.Post(_ =>
        {
            IsMicrophoneOn = _sessionOrchestrator.IsMicrophoneOn;
            ToggleMicrophoneCommand.NotifyCanExecuteChanged();
        }, null);
    }

    private void AddLog(string message) => LogEntries.Add($"{DateTime.Now:HH:mm:ss}  {message}");
}