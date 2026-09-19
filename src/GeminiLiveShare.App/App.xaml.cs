using System.Threading;
using System.Windows;
using GeminiLiveShare.App.ViewModels;
using GeminiLiveShare.App.Views;
using GeminiLiveShare.Core.Audio;
using GeminiLiveShare.Core.BrowserAgent;
using GeminiLiveShare.Core.Diagnostics;
using GeminiLiveShare.Core.Desktop;
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
    private const string SingleInstanceMutexName = "Global\\GeminiLiveShare.App.SingleInstance";
    private Mutex? _singleInstanceMutex;
    private SessionOrchestrator? _sessionOrchestrator;
    private BrowserAgentBridge? _browserAgentBridge;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            Shutdown(1);
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);

        ApiKeyVaultService apiKeyVault = new();
        SensitiveContentFilterSettings filterSettings = new();
        OverlayAppearanceSettings overlaySettings = new();
        DiagnosticsDebugSettings diagnosticsSettings = new();
        HighlightSettings highlightSettings = new();
        AudioCaptureService audioCapture = new();
        ChatHistoryRepository chatHistory = new();
        _browserAgentBridge = new BrowserAgentBridge();
        _browserAgentBridge.Start();
        _sessionOrchestrator = new SessionOrchestrator(
            audioCapture,
            new AudioPlaybackService(),
            new GeminiLiveClient(),
            new ScreenCaptureService(),
            new ImageProcessingService(
                new CredentialBlurService(),
                new OcrCredentialDetector(),
                filterSettings),
            chatHistory,
            _browserAgentBridge,
            new FileSessionDiagnostics(sentFramesDirectory: diagnosticsSettings.SentFramesDirectory),
            diagnosticsSettings,
            new DesktopAutomationService(),
            new GeminiZoomVisionService(),
            new HighlightOverlayService(highlightSettings),
            new GeminiWebSearchService(),
            highlightSettings);

        MainViewModel viewModel = new(_sessionOrchestrator, apiKeyVault, chatHistory, browserAgentBridge: _browserAgentBridge);
        MainWindow window = new(viewModel, apiKeyVault, filterSettings, _sessionOrchestrator, overlaySettings, diagnosticsSettings, _browserAgentBridge, highlightSettings, audioCapture);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _sessionOrchestrator?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _browserAgentBridge?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
            _singleInstanceMutex = null;
            base.OnExit(e);
        }
    }
}
