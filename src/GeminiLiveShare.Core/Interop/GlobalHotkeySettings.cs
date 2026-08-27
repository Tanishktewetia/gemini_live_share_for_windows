using System.Text.Json;

namespace GeminiLiveShare.Core.Interop;

public sealed record GlobalHotkeyConfiguration(HotkeyModifiers Modifiers, uint VirtualKey)
{
    public static GlobalHotkeyConfiguration Default { get; } = new(HotkeyModifiers.Control, 0x59);
}

public sealed class GlobalHotkeySettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _settingsPath;

    public GlobalHotkeySettings(string? settingsPath = null)
    {
        _settingsPath = settingsPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GeminiLiveShare",
            "hotkey-settings.json");
    }

    public GlobalHotkeyConfiguration Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                GlobalHotkeyConfiguration? configuration =
                    JsonSerializer.Deserialize<GlobalHotkeyConfiguration>(File.ReadAllText(_settingsPath), JsonOptions);
                if (configuration is not null && configuration.VirtualKey != 0)
                {
                    return configuration;
                }
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        GlobalHotkeyConfiguration defaultConfiguration = GlobalHotkeyConfiguration.Default;
        try
        {
            Save(defaultConfiguration);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return defaultConfiguration;
    }

    public void Save(GlobalHotkeyConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (configuration.VirtualKey == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration), "A virtual key is required.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(configuration, JsonOptions));
    }
}