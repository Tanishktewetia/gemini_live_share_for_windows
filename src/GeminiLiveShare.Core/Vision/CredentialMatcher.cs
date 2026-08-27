using System.Text.RegularExpressions;
using Windows.Foundation;
using Windows.Media.Ocr;

namespace GeminiLiveShare.Core.Vision;

internal sealed record SensitiveLine(string Text, Rect Bounds, string Reason);

internal static class CredentialMatcher
{
    private static readonly Regex KeywordWithValue = new(
        @"\b(password|passwd|passcode|pin|api[ _-]*key|apikey|secret|access[ _-]*token|auth(?:entication)?|credential)\b(?:\s+is\b|\s*[:=]\s*|\s+)\S+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CredentialLabel = new(
        @"\b(password|passwd|passcode|pin|api[ _-]*key|apikey|secret|access[ _-]*token|auth(?:entication)?|credential)s?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LongToken = new(
        @"\b(?=[A-Za-z0-9_-]{20,}\b)(?=[A-Za-z0-9_-]*[A-Z])(?=[A-Za-z0-9_-]*[a-z])(?=[A-Za-z0-9_-]*\d)[A-Za-z0-9_-]{20,}\b",
        RegexOptions.Compiled);
    private static readonly Regex Card = new(
        @"(?<!\d)(?:\d[ -]?){13,19}(?!\d)",
        RegexOptions.Compiled);

    public static IReadOnlyList<SensitiveLine> Find(OcrResult result)
    {
        List<SensitiveLine> lines = result.Lines
            .Select(line => new SensitiveLine(line.Text, Bounds(line), string.Empty))
            .Where(line => !line.Bounds.IsEmpty)
            .ToList();

        return Find(lines);
    }

    internal static IReadOnlyList<SensitiveLine> Find(IReadOnlyList<SensitiveLine> lines)
    {
        List<SensitiveLine> matches = new();

        for (int index = 0; index < lines.Count; index++)
        {
            SensitiveLine line = lines[index];
            string? reason = Reason(line.Text);
            if (reason is not null)
            {
                AddUnique(matches, line with { Reason = reason });
            }

            if (!CredentialLabel.IsMatch(line.Text))
            {
                continue;
            }

            // OCR commonly returns a form label and its value as separate lines, even when they
            // are visually on the same row. Protect the label and the nearest value either to its
            // right or directly below it. Use geometry instead of OCR result order because that
            // order is not guaranteed to follow the visual layout.
            AddUnique(matches, line with { Reason = "credential label" });
            SensitiveLine? valueToRight = lines
                .Where(candidate => candidate.Bounds != line.Bounds &&
                    IsLikelyValueToRight(line.Bounds, candidate.Bounds))
                .OrderBy(candidate => candidate.Bounds.X - (line.Bounds.X + line.Bounds.Width))
                .FirstOrDefault();
            if (valueToRight is not null)
            {
                AddUnique(matches, valueToRight with { Reason = "value beside credential label" });
            }

            SensitiveLine? valueLine = lines
                .Where(candidate => candidate.Bounds != line.Bounds &&
                    IsLikelyValueBelow(line.Bounds, candidate.Bounds))
                .OrderBy(candidate => candidate.Bounds.Y - (line.Bounds.Y + line.Bounds.Height))
                .FirstOrDefault();
            if (valueLine is not null)
            {
                AddUnique(matches, valueLine with { Reason = "value below credential label" });
            }
        }

        return matches;
    }

    private static bool IsLikelyValueToRight(Rect label, Rect candidate)
    {
        double horizontalGap = candidate.X - (label.X + label.Width);
        double maximumGap = Math.Max(48, label.Height * 3);
        double verticalOverlap = Math.Min(label.Y + label.Height, candidate.Y + candidate.Height) -
            Math.Max(label.Y, candidate.Y);
        double minimumOverlap = Math.Min(label.Height, candidate.Height) * 0.5;
        return horizontalGap >= -2 && horizontalGap <= maximumGap &&
            verticalOverlap >= minimumOverlap;
    }

    private static bool IsLikelyValueBelow(Rect label, Rect candidate)
    {
        double verticalGap = candidate.Y - (label.Y + label.Height);
        double maximumGap = Math.Max(48, label.Height * 3);
        bool overlapsHorizontally = candidate.X <= label.X + Math.Max(label.Width * 2, 300) &&
            candidate.X + candidate.Width >= label.X - 40;
        return verticalGap >= -2 && verticalGap <= maximumGap && overlapsHorizontally;
    }

    private static void AddUnique(List<SensitiveLine> matches, SensitiveLine candidate)
    {
        if (!matches.Any(existing => existing.Bounds == candidate.Bounds))
        {
            matches.Add(candidate);
        }
    }

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
        if (KeywordWithValue.IsMatch(text))
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