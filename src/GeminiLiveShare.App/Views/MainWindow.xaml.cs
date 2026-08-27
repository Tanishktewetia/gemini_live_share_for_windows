using System.Windows;
using GeminiLiveShare.App.ViewModels;
using GeminiLiveShare.Core.Security;
using GeminiLiveShare.Core.Gemini;

namespace GeminiLiveShare.App.Views;
public partial class MainWindow : Window
{
    private readonly IApiKeyVaultService _apiKeyVault;
    private readonly ISensitiveContentFilterSettings _filterSettings;
    private readonly SessionOrchestrator _sessionOrchestrator;

    public MainWindow(
        MainViewModel viewModel,
        IApiKeyVaultService apiKeyVault,
        ISensitiveContentFilterSettings filterSettings,
        SessionOrchestrator sessionOrchestrator)
    {
        InitializeComponent();
        DataContext = viewModel;
        _apiKeyVault = apiKeyVault;
        _filterSettings = filterSettings;
        _sessionOrchestrator = sessionOrchestrator;
        viewModel.SettingsRequested += OnSettingsRequested;
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        SettingsWindow settingsWindow = new(new SettingsViewModel(_apiKeyVault, _filterSettings))
        {
            Owner = this
        };
        settingsWindow.ShowDialog();
    }

    // TEMP: manual test button for Phase 5a, remove after verification.
    private void OnTestOverlayClick(object sender, RoutedEventArgs e) => new OverlayWindow(_sessionOrchestrator).Show();
}