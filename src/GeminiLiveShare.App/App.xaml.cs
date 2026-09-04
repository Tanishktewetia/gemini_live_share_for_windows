using System.Windows;
using GeminiLiveShare.App.ViewModels;
using GeminiLiveShare.App.Views;
using GeminiLiveShare.Core.Audio;
using GeminiLiveShare.Core.BrowserAgent;
using GeminiLiveShare.Core.Gemini;
using GeminiLiveShare.Core.Interop;
using GeminiLiveShare.Core.Security;
using GeminiLiveShare.Core.Storage;
using GeminiLiveShare.Core.Vision;

namespace GeminiLiveShare.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : System.Windows.Application
{
    private SessionOrchestrator? _sessionOrchestrator;
    private BrowserAgentBridge? _browserAgentBridge;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApiKeyVaultService apiKeyVault = new();
        SensitiveContentFilterSettings filterSettings = new();
        OverlayAppearanceSettings overlaySettings = new();
        ChatHistoryRepository chatHistory = new();
        _browserAgentBridge = new BrowserAgentBridge();
        _browserAgentBridge.Start();
        _sessionOrchestrator = new SessionOrchestrator(
            new AudioCaptureService(),
            new AudioPlaybackService(),
            new GeminiLiveClient(),
            new ScreenCaptureService(),
            new ImageProcessingService(
                new CredentialBlurService(),
                new OcrCredentialDetector(),
                filterSettings),
            chatHistory,
            _browserAgentBridge);

        MainViewModel viewModel = new(_sessionOrchestrator, apiKeyVault, chatHistory, browserAgentBridge: _browserAgentBridge);
        MainWindow window = new(viewModel, apiKeyVault, filterSettings, _sessionOrchestrator, overlaySettings, _browserAgentBridge);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_sessionOrchestrator is not null)
        {
            _sessionOrchestrator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (_browserAgentBridge is not null)
        {
            _browserAgentBridge.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        base.OnExit(e);
    }
}
