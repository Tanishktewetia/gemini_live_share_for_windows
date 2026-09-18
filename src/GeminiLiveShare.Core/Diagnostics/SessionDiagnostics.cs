using System.Diagnostics;
using System.Globalization;

namespace GeminiLiveShare.Core.Diagnostics;

public interface ISessionDiagnostics
{
    void Log(string message);

    void SaveSentFrame(string sessionId, long frameNumber, byte[] jpegBytes);
}

/// <summary>Discards diagnostics; used by tests and when no log is configured.</summary>
public sealed class NullSessionDiagnostics : ISessionDiagnostics
{
    public static NullSessionDiagnostics Instance { get; } = new();

    public void Log(string message)
    {
    }

    public void SaveSentFrame(string sessionId, long frameNumber, byte[] jpegBytes)
    {
    }
}

/// <summary>
/// Appends timestamped session events to %LOCALAPPDATA%\GeminiLiveShare\logs\session-yyyyMMdd.log so manual tests
/// and user reports ("the voice was breaking") leave measurable evidence: microphone loss, frame upload times,
/// skipped frames, JPEG quality changes and connection events. Never logs audio, transcripts or keys.
/// </summary>
public sealed class FileSessionDiagnostics : ISessionDiagnostics
{
    private const int RetainedLogFiles = 14;
    private const int RetainedFrameDays = 3;
    private readonly object _writeLock = new();
    private readonly string _directory;

    public FileSessionDiagnostics(string? directory = null, string? sentFramesDirectory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GeminiLiveShare", "logs");
        SentFramesDirectory = sentFramesDirectory ?? Path.Combine(_directory, "sent-frames");
        try
        {
            Directory.CreateDirectory(_directory);
            Directory.CreateDirectory(SentFramesDirectory);
            foreach (FileInfo old in new DirectoryInfo(_directory).GetFiles("session-*.log")
                         .OrderByDescending(file => file.Name).Skip(RetainedLogFiles))
            {
                old.Delete();
            }

            DateOnly cutoff = DateOnly.FromDateTime(DateTime.Now.AddDays(-RetainedFrameDays));
            foreach (DirectoryInfo dayDirectory in new DirectoryInfo(SentFramesDirectory).EnumerateDirectories())
            {
                if (!DateOnly.TryParseExact(dayDirectory.Name, "yyyyMMdd", out DateOnly day) || day >= cutoff)
                {
                    continue;
                }

                dayDirectory.Delete(recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Session diagnostics directory unavailable: {ex.Message}");
        }
    }

    public string CurrentLogPath => Path.Combine(_directory, $"session-{DateTime.Now:yyyyMMdd}.log");

    public string SentFramesDirectory { get; }

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

    public void SaveSentFrame(string sessionId, long frameNumber, byte[] jpegBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(jpegBytes);

        string stamp = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        string frameDirectory = Path.Combine(SentFramesDirectory, stamp, sessionId);
        string fileName = $"{DateTime.Now:HHmmssfff}-f{frameNumber:D6}.jpg";
        try
        {
            lock (_writeLock)
            {
                Directory.CreateDirectory(frameDirectory);
                File.WriteAllBytes(Path.Combine(frameDirectory, fileName), jpegBytes);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Session diagnostics frame write failed: {ex.Message}");
        }
    }
}
