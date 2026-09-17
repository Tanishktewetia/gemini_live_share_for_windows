using System.Diagnostics;
using System.Globalization;

namespace GeminiLiveShare.Core.Diagnostics;

public interface ISessionDiagnostics
{
    void Log(string message);
}

/// <summary>Discards diagnostics; used by tests and when no log is configured.</summary>
public sealed class NullSessionDiagnostics : ISessionDiagnostics
{
    public static NullSessionDiagnostics Instance { get; } = new();

    public void Log(string message)
    {
    }
}

/// <summary>
/// Appends timestamped session events to %LOCALAPPDATA%\GeminiLiveShare\logs\session-yyyyMMdd.log so manual tests
/// and user reports ("the voice was breaking") leave measurable evidence: microphone loss, frame upload times,
/// skipped frames, JPEG quality changes and connection events. Never logs audio, images, transcripts or keys.
/// </summary>
public sealed class FileSessionDiagnostics : ISessionDiagnostics
{
    private const int RetainedLogFiles = 14;
    private readonly object _writeLock = new();
    private readonly string _directory;

    public FileSessionDiagnostics(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeminiLiveShare", "logs");
        try
        {
            Directory.CreateDirectory(_directory);
            foreach (FileInfo old in new DirectoryInfo(_directory).GetFiles("session-*.log")
                         .OrderByDescending(file => file.Name).Skip(RetainedLogFiles))
            {
                old.Delete();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Session diagnostics directory unavailable: {ex.Message}");
        }
    }

    public string CurrentLogPath => Path.Combine(_directory, $"session-{DateTime.Now:yyyyMMdd}.log");

    public void Log(string message)
    {
        string line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)}  {message}{Environment.NewLine}";
        try
        {
            lock (_writeLock)
            {
                File.AppendAllText(CurrentLogPath, line);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Session diagnostics write failed: {ex.Message}");
        }
    }
}
