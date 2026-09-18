using System.Text.Json;

namespace GeminiLiveShare.Core.Diagnostics;

public interface IDiagnosticsDebugSettings
{
    bool SaveSentFrames { get; set; }

    string SentFramesDirectory { get; }
}

public sealed class DiagnosticsDebugSettings : IDiagnosticsDebugSettings
{
    private readonly object _sync = new();
    private readonly string _settingsPath;
    private bool _saveSentFrames;

    public DiagnosticsDebugSettings(string? settingsPath = null, string? sentFramesDirectory = null)
    {
        string rootDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GeminiLiveShare");
        _settingsPath = settingsPath ?? Path.Combine(rootDirectory, "diagnostics-settings.json");
        SentFramesDirectory = sentFramesDirectory ?? Path.Combine(rootDirectory, "Diagnostics", "sent-frames");
        Load();
    }

    public bool SaveSentFrames
    {
        get
        {
            lock (_sync)
            {
                return _saveSentFrames;
            }
        }
        set
        {
            lock (_sync)
            {
                if (_saveSentFrames == value)
                {
                    return;
                }

                _saveSentFrames = value;
                Persist();
            }
        }
    }

    public string SentFramesDirectory { get; }

    private void Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return;
            }

            PersistedDiagnosticsSettings? persisted = JsonSerializer.Deserialize<PersistedDiagnosticsSettings>(
                File.ReadAllText(_settingsPath));
            _saveSentFrames = persisted?.SaveSentFrames ?? false;
        }
        catch
        {
            // Debug diagnostics are optional; default to OFF if the setting cannot be read.
            _saveSentFrames = false;
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(new PersistedDiagnosticsSettings(_saveSentFrames)));
    }

    private sealed record PersistedDiagnosticsSettings(bool SaveSentFrames);
}
