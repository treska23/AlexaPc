using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bardo.Mobile.Infrastructure;

/// <summary>
/// Corrige errores pequeños y previsibles del ASR sólo en órdenes inocuas y
/// de vocabulario cerrado. No aproxima órdenes de encendido/apagado del PC.
/// </summary>
internal static class SpokenCommandNormalizer
{
    private static readonly HashSet<string> TelevisionWords =
        new(StringComparer.Ordinal)
        {
            "tele", "tv", "television", "bravia", "sony"
        };

    private static readonly HashSet<string> NavigationWords =
        new(StringComparer.Ordinal)
        {
            "canal", "siguiente", "anterior", "adelante", "atras"
        };

    public static string NormalizeForRelay(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        string folded = Fold(command);
        folded = Regex.Replace(
            folded,
            @"[^\p{L}\p{N}+]+",
            " ",
            RegexOptions.CultureInvariant).Trim();

        if (folded.Length == 0)
        {
            return string.Empty;
        }

        string[] words = folded.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool televisionContext = words.Any(TelevisionWords.Contains);
        bool navigationContext = words.Any(NavigationWords.Contains);
        bool volumeContext = words.Contains("volumen", StringComparer.Ordinal)
                             || televisionContext;

        for (int i = 0; i < words.Length; i++)
        {
            string word = words[i];

            // Variantes observadas/probables al hablar deprisa en castellano.
            if (televisionContext && !navigationContext && IsNear(word, "pausa", 1))
            {
                words[i] = "pausa";
                continue;
            }

            if (televisionContext && IsNear(word, "tele", 1))
            {
                words[i] = "tele";
                continue;
            }

            if (televisionContext && IsNear(word, "bravia", 2))
            {
                words[i] = "bravia";
                continue;
            }

            if (volumeContext && IsNear(word, "volumen", 2))
            {
                words[i] = "volumen";
                continue;
            }

            if (volumeContext && IsNear(word, "sube", 1))
            {
                words[i] = "sube";
                continue;
            }

            if (volumeContext && IsNear(word, "baja", 1))
            {
                words[i] = "baja";
                continue;
            }

            if (televisionContext && IsNear(word, "reanuda", 2))
            {
                words[i] = "reanuda";
                continue;
            }

            if (televisionContext && IsNear(word, "reproduce", 2))
            {
                words[i] = "reproduce";
                continue;
            }

            if (televisionContext && IsNear(word, "silencio", 2))
            {
                words[i] = "silencio";
                continue;
            }

            // Nombres de apps: son órdenes de bajo riesgo y el modelo puede
            // deformarlos bastante si se dicen con pronunciación española.
            words[i] = NormalizeAppName(word);
        }

        return string.Join(' ', words);
    }

    private static string NormalizeAppName(string word)
    {
        if (IsNear(word, "youtube", 2))
        {
            return "youtube";
        }

        if (IsNear(word, "netflix", 2))
        {
            return "netflix";
        }

        if (IsNear(word, "disney", 2))
        {
            return "disney";
        }

        if (IsNear(word, "movistar", 2))
        {
            return "movistar";
        }

        return word;
    }

    private static bool IsNear(string candidate, string expected, int maximumDistance)
    {
        if (candidate.Length < 3 || expected.Length < 3)
        {
            return string.Equals(candidate, expected, StringComparison.Ordinal);
        }

        // Evita correcciones agresivas entre palabras sin parecido fonético inicial.
        if (candidate[0] != expected[0])
        {
            return false;
        }

        if (Math.Abs(candidate.Length - expected.Length) > maximumDistance)
        {
            return false;
        }

        return EditDistance(candidate, expected, maximumDistance) <= maximumDistance;
    }

    private static int EditDistance(string left, string right, int stopAfter)
    {
        int[] previous = Enumerable.Range(0, right.Length + 1).ToArray();
        int[] current = new int[right.Length + 1];

        for (int i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            int rowMinimum = current[0];

            for (int j = 1; j <= right.Length; j++)
            {
                int cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
                rowMinimum = Math.Min(rowMinimum, current[j]);
            }

            if (rowMinimum > stopAfter)
            {
                return rowMinimum;
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static string Fold(string value)
    {
        string decomposed = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (char c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
