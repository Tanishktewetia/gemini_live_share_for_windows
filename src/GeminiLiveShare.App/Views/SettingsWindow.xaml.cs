using System.Windows;
using GeminiLiveShare.App.ViewModels;

namespace GeminiLiveShare.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        if (viewModel.HasSavedApiKey)
        {
            viewModel.StatusMessage = "An API key is already saved. Enter a new key to replace it.";
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.Save(ApiKeyInput.Password))
        {
            ApiKeyInput.Clear();
        }
    }
}