using System.Text;

namespace GeminiLiveShare.Core.Gemini;

internal sealed class OutputTranscriptionAccumulator
{
    private readonly StringBuilder _text = new();

    public string? Process(string? chunk, bool turnComplete, bool interrupted)
    {
        if (!string.IsNullOrEmpty(chunk))
        {
            _text.Append(chunk);
        }

        if (interrupted)
        {
            string? interruptedText = _text.Length == 0 ? null : _text.ToString();
            _text.Clear();
            return interruptedText;
        }

        if (!turnComplete || _text.Length == 0)
        {
            return null;
        }

        string completedText = _text.ToString();
        _text.Clear();
        return completedText;
    }

    public void Clear() => _text.Clear();
}