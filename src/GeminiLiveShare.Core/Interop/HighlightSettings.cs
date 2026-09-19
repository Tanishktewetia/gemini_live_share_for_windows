using System.Text.Json;

namespace GeminiLiveShare.Core.Interop;

public sealed class HighlightSettings
{
    private readonly object _sync = new();
    private readonly string _path;
    private bool _isEnabled = true;
    private bool _showArrow;

    public HighlightSettings(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeminiLiveShare", "highlight-settings.json");
        Load();
    }

    public bool IsEnabled { get { lock (_sync) return _isEnabled; } set { lock (_sync) { _isEnabled = value; SaveUnsafe(); } } }
    public bool ShowArrow { get { lock (_sync) return _showArrow; } set { lock (_sync) { _showArrow = value; SaveUnsafe(); } } }

    private void Load()
    {
        lock (_sync)
        {
            try
            {
                if (!File.Exists(_path)) return;
                Persisted? value = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(_path));
                if (value is not null) { _isEnabled = value.IsEnabled; _showArrow = value.ShowArrow; }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }

    private void SaveUnsafe()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new Persisted(_isEnabled, _showArrow)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record Persisted(bool IsEnabled, bool ShowArrow);
}
