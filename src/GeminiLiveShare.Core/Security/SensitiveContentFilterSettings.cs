namespace GeminiLiveShare.Core.Security;

public sealed class SensitiveContentFilterSettings : ISensitiveContentFilterSettings
{
    private const string EnabledValue = "enabled";
    private const string DisabledValue = "disabled";
    private readonly object _sync = new();
    private readonly string _settingsPath;
    private bool _isEnabled;

    public SensitiveContentFilterSettings()
    {
        string settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GeminiLiveShare");
        _settingsPath = Path.Combine(settingsDirectory, "sensitive-content-filter.txt");
        _isEnabled = Load();
    }

    public bool IsEnabled
    {
        get
        {
            lock (_sync)
            {
                return _isEnabled;
            }
        }
        set
        {
            lock (_sync)
            {
                if (_isEnabled == value)
                {
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
                File.WriteAllText(_settingsPath, value ? EnabledValue : DisabledValue);
                _isEnabled = value;
            }
        }
    }

    private bool Load()
    {
        try
        {
            return !File.Exists(_settingsPath) ||
                !string.Equals(File.ReadAllText(_settingsPath).Trim(), DisabledValue, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Protection defaults to enabled if the local setting cannot be read.
            return true;
        }
    }
}