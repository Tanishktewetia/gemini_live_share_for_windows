using System.Text.Json;

namespace GeminiLiveShare.Core.Interop;

public enum OverlayTheme
{
    Dark,
    Light
}

public enum OverlayPosition
{
    TopCenter,
    BottomCenter,
    Custom
}

public sealed class OverlayAppearanceSettings
{
    private readonly object _sync = new();
    private readonly string _settingsPath;
    private OverlayTheme _theme;
    private OverlayPosition _position;
    private double? _customLeft;
    private double? _customTop;

    public OverlayAppearanceSettings(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GeminiLiveShare",
            "overlay-settings.json");
        Load();
    }

    public OverlayTheme Theme
    {
        get { lock (_sync) return _theme; }
        set { lock (_sync) { _theme = value; SaveUnsafe(); } }
    }

    public OverlayPosition Position
    {
        get { lock (_sync) return _position; }
        set { lock (_sync) { _position = value; SaveUnsafe(); } }
    }

    public double? CustomLeft
    {
        get { lock (_sync) return _customLeft; }
        private set { _customLeft = value; }
    }

    public double? CustomTop
    {
        get { lock (_sync) return _customTop; }
        private set { _customTop = value; }
    }

    public void ResetPosition()
    {
        lock (_sync)
        {
            _position = OverlayPosition.TopCenter;
            _customLeft = null;
            _customTop = null;
            SaveUnsafe();
        }
    }

    public void SaveCurrentPosition(double left, double top)
    {
        lock (_sync)
        {
            _position = OverlayPosition.Custom;
            _customLeft = left;
            _customTop = top;
            SaveUnsafe();
        }
    }

    private void Load()
    {
        lock (_sync)
        {
            _theme = OverlayTheme.Dark;
            _position = OverlayPosition.TopCenter;
            try
            {
                if (!File.Exists(_settingsPath))
                {
                    return;
                }

                PersistedOverlaySettings? value = JsonSerializer.Deserialize<PersistedOverlaySettings>(
                    File.ReadAllText(_settingsPath));
                if (value is null)
                {
                    return;
                }

                _theme = Enum.IsDefined(value.Theme) ? value.Theme : OverlayTheme.Dark;
                _position = Enum.IsDefined(value.Position) ? value.Position : OverlayPosition.TopCenter;
                _customLeft = value.CustomLeft;
                _customTop = value.CustomTop;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // Safe defaults are used when the optional appearance file is unavailable.
            }
        }
    }

    private void SaveUnsafe()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new PersistedOverlaySettings(
                _theme, _position, _customLeft, _customTop)));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PersistedOverlaySettings(
        OverlayTheme Theme,
        OverlayPosition Position,
        double? CustomLeft,
        double? CustomTop);
}
