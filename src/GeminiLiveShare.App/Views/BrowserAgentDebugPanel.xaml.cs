using System.Text.Json;
using System.Windows;
using GeminiLiveShare.Core.BrowserAgent;

namespace GeminiLiveShare.App.Views;

public partial class BrowserAgentDebugPanel : Window
{
    private readonly BrowserAgentBridge _browserAgentBridge;

    public BrowserAgentDebugPanel(BrowserAgentBridge browserAgentBridge)
    {
        _browserAgentBridge = browserAgentBridge;
        InitializeComponent();
    }

    private async void OnGetActivePageClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button)
        {
            button.IsEnabled = false;
        }

        try
        {
            using JsonDocument arguments = JsonDocument.Parse("{}");
            var result = await _browserAgentBridge.SendToolCallAsync(
                "get_active_page",
                arguments.RootElement);
            ResultBox.Text = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception exception)
        {
            ResultBox.Text = JsonSerializer.Serialize(new
            {
                ok = false,
                error = exception.Message
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        finally
        {
            if (sender is System.Windows.Controls.Button completedButton)
            {
                completedButton.IsEnabled = true;
            }
        }
    }
}
