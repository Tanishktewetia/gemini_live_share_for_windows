using System.Text.Json;
using System.Windows;
using GeminiLiveShare.Core.BrowserAgent;
using GeminiLiveShare.Core.BrowserAgent.Models;

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

    private async void OnGetFormFieldsClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button button)
        {
            button.IsEnabled = false;
        }

        try
        {
            using JsonDocument arguments = JsonDocument.Parse("{}");
            ToolCallResult result = await _browserAgentBridge.SendToolCallAsync(
                "get_form_fields",
                arguments.RootElement);
            if (result.Payload.TryGetProperty("ok", out JsonElement ok) && !ok.GetBoolean())
            {
                ResultBox.Text = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
                return;
            }

            PageSnapshot? snapshot = JsonSerializer.Deserialize<PageSnapshot>(result.Payload.GetRawText());
            if (snapshot is null)
            {
                ResultBox.Text = "No page snapshot was returned.";
                return;
            }

            List<string> lines = [$"Page: {snapshot.Title ?? "(untitled)"}", $"URL: {snapshot.Url}", ""];
            lines.AddRange(snapshot.Fields.Select(field =>
                $"[{field.Id}] {field.Label} | type={field.Type} | required={field.Required} | value={field.Value}"));
            if (snapshot.Notices.Count > 0)
            {
                lines.Add("");
                lines.AddRange(snapshot.Notices.Select(notice => $"Notice: {notice}"));
            }

            ResultBox.Text = string.Join(Environment.NewLine, lines);
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
