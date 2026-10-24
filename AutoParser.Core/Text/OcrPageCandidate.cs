namespace AutoParser.Core.Text;

/// <summary>
/// Страница, распознанная при одном повороте, и её «осмысленность» (чем больше, тем лучше).
/// Движок возвращает несколько кандидатов, а <see cref="OcrPageRunner"/> выбирает лучший.
/// </summary>
public sealed record OcrPageCandidate(int Rotation, IReadOnlyList<TextLine> Lines, int Score)
{
    /// <summary>Слово из 3+ букв одного алфавита (кириллица или латиница): фильтр «мусорного» распознавания.</summary>
    public static bool LooksLikeWord(string s)
    {
        var letters = s.Where(char.IsLetter).ToArray();
        if (letters.Length < 3 || letters.Length < s.Length * 0.7) return false;
        bool cyr = letters.All(IsCyrillic), lat = letters.All(c => c < 128);
        return cyr || lat;
    }

    public static bool IsCyrillic(char c) => c is >= 'А' and <= 'я' or 'Ё' or 'ё';

    /// <summary>Общая оценка текста: суммарная длина «словоподобных» слов.</summary>
    public static int ScoreOf(IEnumerable<TextLine> lines) =>
        lines.SelectMany(l => l.Words).Sum(w => LooksLikeWord(w.Text) ? w.Text.Length : 0);

    /// <summary>Движки без собственной уверенности: эвристика по составу слова.</summary>
    public static double WordConfidence(string s)
    {
        var letters = s.Where(char.IsLetter).ToArray();
        if (letters.Length == 0) return 0.8;
        bool mixed = letters.Any(IsCyrillic) && letters.Any(c => c < 128);
        if (mixed) return 0.55;
        if (s.Any(c => "|\\~^_{}[]".Contains(c))) return 0.5;
        return 0.85;
    }
}
