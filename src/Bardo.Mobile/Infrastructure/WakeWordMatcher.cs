using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bardo.Mobile.Infrastructure;

internal static class WakeWordMatcher
{
    private static readonly string[] BardoAliases =
    [
        "bardo",
        "pardo",
        "vardo",
        "barbo",
        "borde"
    ];

    public static bool Matches(string transcript, string wakeWord)
    {
        string normalizedTranscript = Normalize(transcript);
        string normalizedWakeWord = Normalize(wakeWord);

        if (normalizedTranscript.Length == 0 || normalizedWakeWord.Length == 0)
        {
            return false;
        }

        string[] words = normalizedTranscript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(word => word.Equals(normalizedWakeWord, StringComparison.Ordinal)))
        {
            return true;
        }

        if (normalizedWakeWord == "bardo" &&
            words.Any(word => BardoAliases.Contains(word, StringComparer.Ordinal)))
        {
            return true;
        }

        // El motor local ya está limitado a español. Admitimos una sola edición
        // únicamente en frases cortas para ganar tolerancia sin convertir cualquier
        // conversación de fondo en una activación accidental.
        return words.Length <= 3 &&
               words.Any(word => Math.Abs(word.Length - normalizedWakeWord.Length) <= 1 &&
                                 LevenshteinDistance(word, normalizedWakeWord) <= 1);
    }

    public static string ExtractCommandAfterWakeWord(string transcript, string wakeWord)
    {
        string text = transcript.Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var candidates = Normalize(wakeWord) == "bardo"
            ? BardoAliases.Append(wakeWord).Distinct(StringComparer.OrdinalIgnoreCase)
            : [wakeWord];

        foreach (string candidate in candidates)
        {
            Match match = Regex.Match(
                text,
                $@"^\s*{Regex.Escape(candidate)}(?:(?:[\s,;:.-]+)(?<command>.*))?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            if (match.Success)
            {
                return match.Groups["command"].Value.Trim();
            }
        }

        return string.Empty;
    }

    private static string Normalize(string value)
    {
        string decomposed = (value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(character) ? character : ' ');
        }

        return Regex.Replace(builder.ToString(), @"\s+", " ").Trim();
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
        int[] current = new int[right.Length + 1];

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
