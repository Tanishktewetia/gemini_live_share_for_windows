using System.Windows;
using GeminiLiveShare.App.ViewModels;
using GeminiLiveShare.Core.Security;

namespace GeminiLiveShare.App.Views;
public partial class MainWindow : Window
{
    private readonly IApiKeyVaultService _apiKeyVault;

    public MainWindow(MainViewModel viewModel, IApiKeyVaultService apiKeyVault)
    {
        InitializeComponent();
        DataContext = viewModel;
        _apiKeyVault = apiKeyVault;
        viewModel.SettingsRequested += OnSettingsRequested;
    }

    private void OnSettingsRequested(object? sender, EventArgs e)
    {
        SettingsWindow settingsWindow = new(new SettingsViewModel(_apiKeyVault))
        {
            Owner = this
        };
        settingsWindow.ShowDialog();
    }
}