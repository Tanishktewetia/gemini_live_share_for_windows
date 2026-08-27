using System.Windows;
using GeminiLiveShare.App.ViewModels;
using GeminiLiveShare.Core.Security;

namespace GeminiLiveShare.App.Views;
public partial class MainWindow : Window
{
    private readonly IApiKeyVaultService _apiKeyVault;
    private readonly ISensitiveContentFilterSettings _filterSettings;

    public MainWindow(
        MainViewModel viewModel,
        IApiKeyVaultService apiKeyVault,
        ISensitiveContentFilterSettings filterSettings)
    {
        InitializeComponent();
        DataContext = viewModel;
        _apiKeyVault = apiKeyVault;
        _filterSettings = filterSettings;
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
}