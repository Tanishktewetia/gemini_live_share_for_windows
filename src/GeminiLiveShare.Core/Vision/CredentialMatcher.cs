using System.Text.RegularExpressions;
using Windows.Foundation;
using Windows.Media.Ocr;

namespace GeminiLiveShare.Core.Vision;

internal sealed record SensitiveLine(string Text, Rect Bounds, string Reason);

internal static class CredentialMatcher
{
    private static readonly Regex Keyword = new(
        @"\b(password|passwd|api[ _]*key|apikey|secret|token|auth|credential)\b\s*[:= ]\s*\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LongToken = new(
        @"\b(?=[A-Za-z0-9_-]{20,}\b)(?=[A-Za-z0-9_-]*[A-Z])(?=[A-Za-z0-9_-]*[a-z])(?=[A-Za-z0-9_-]*\d)[A-Za-z0-9_-]{20,}\b",
        RegexOptions.Compiled);
    private static readonly Regex Card = new(
        @"(?<!\d)(?:\d[ -]?){13,19}(?!\d)",
        RegexOptions.Compiled);

    public static IReadOnlyList<SensitiveLine> Find(OcrResult result) => result.Lines
        .Select(line => (line, reason: Reason(line.Text)))
        .Where(match => match.reason is not null)
        .Select(match => new SensitiveLine(match.line.Text, Bounds(match.line), match.reason!))
        .ToList();

    private static Rect Bounds(OcrLine line)
    {
        List<OcrWord> words = line.Words.ToList();
        if (words.Count == 0)
        {
            return new Rect();
        }

        double left = words.Min(word => word.BoundingRect.X);
        double top = words.Min(word => word.BoundingRect.Y);
        double right = words.Max(word => word.BoundingRect.X + word.BoundingRect.Width);
        double bottom = words.Max(word => word.BoundingRect.Y + word.BoundingRect.Height);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static string? Reason(string text)
    {
        if (Keyword.IsMatch(text))
        {
            return "keyword + value";
        }

        if (LongToken.IsMatch(text))
        {
            return "high-entropy token";
        }

        if (Card.IsMatch(text.Replace(" ", string.Empty).Replace("-", string.Empty)))
        {
            return "card-number-like";
        }

        return null;
    }
}