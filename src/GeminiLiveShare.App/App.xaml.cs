using System.Windows;
using GeminiLiveShare.App.ViewModels;
using GeminiLiveShare.App.Views;
using GeminiLiveShare.Core.Audio;
using GeminiLiveShare.Core.Gemini;
using GeminiLiveShare.Core.Interop;
using GeminiLiveShare.Core.Security;
using GeminiLiveShare.Core.Storage;
using GeminiLiveShare.Core.Vision;

namespace GeminiLiveShare.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private SessionOrchestrator? _sessionOrchestrator;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApiKeyVaultService apiKeyVault = new();
        SensitiveContentFilterSettings filterSettings = new();
        OverlayAppearanceSettings overlaySettings = new();
        ChatHistoryRepository chatHistory = new();
        _sessionOrchestrator = new SessionOrchestrator(
            new AudioCaptureService(),
            new AudioPlaybackService(),
            new GeminiLiveClient(),
            new ScreenCaptureService(),
            new ImageProcessingService(
                new CredentialBlurService(),
                new OcrCredentialDetector(),
                filterSettings),
            chatHistory);

        MainViewModel viewModel = new(_sessionOrchestrator, apiKeyVault, chatHistory);
        MainWindow window = new(viewModel, apiKeyVault, filterSettings, _sessionOrchestrator, overlaySettings);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_sessionOrchestrator is not null)
        {
            _sessionOrchestrator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }
}
