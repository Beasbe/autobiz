using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoParser.Core.Validate;

/// <summary>Общие преобразования текста: регистр, пробелы, похожие латинские/кириллические символы.</summary>
public static partial class TextNorm
{
    private const string LatinLook = "ABCEHKMOPTXYabcehkmoptxy";
    private const string CyrLook = "АВСЕНКМОРТХУавсенкмортху";

    /// <summary>Ключ для сравнения меток: нижний регистр, ё→е, латинские двойники → кириллица, только буквы/цифры и одиночные пробелы.</summary>
    public static string Key(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool space = false;
        foreach (var ch0 in s)
        {
            var ch = char.ToLowerInvariant(ch0);
            if (ch == 'ё') ch = 'е';
            var i = LatinLook.IndexOf(ch);
            if (i >= 0) ch = CyrLook[i];
            if (char.IsLetterOrDigit(ch))
            {
                if (space && sb.Length > 0) sb.Append(' ');
                sb.Append(ch);
                space = false;
            }
            else space = true;
        }
        return sb.ToString();
    }

    public static string ToCyrillicLookalikes(string s)
    {
        var a = s.ToCharArray();
        for (int i = 0; i < a.Length; i++)
        {
            var k = LatinLook.IndexOf(a[i]);
            if (k >= 0) a[i] = CyrLook[k];
        }
        return new string(a);
    }

    public static string CollapseSpaces(string s) => Spaces().Replace(s, " ").Trim();

    /// <summary>Убирает разделители между меткой и значением.</summary>
    public static string TrimSeparators(string s) => s.Trim().Trim('-', '–', '—', ':', '|', '.', ',', ';', ' ', '_', '=').Trim();

    /// <summary>«МИХАЙЛОВА ТАТЬЯНА ВЛАДИМИРОВНА» → «Михайлова Татьяна Владимировна».</summary>
    public static string TitleCasePerson(string s)
    {
        s = CollapseSpaces(s);
        var ti = CultureInfo.GetCultureInfo("ru-RU").TextInfo;
        return string.Join(" ", s.Split(' ').Select(w => string.Join("-", w.Split('-').Select(p => p.Length == 0 ? p : ti.ToTitleCase(p.ToLowerInvariant())))));
    }

    /// <summary>«Белый» из «БЕЛЫЙ» — первая буква заглавная.</summary>
    public static string Capitalize(string s)
    {
        s = CollapseSpaces(s).ToLowerInvariant();
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    /// <summary>Расстояние Левенштейна (для нечёткого поиска меток в OCR-тексте).</summary>
    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Цифры, которые OCR часто путает с буквами (в числовом контексте).</summary>
    public static string FixDigits(string s)
    {
        var a = s.ToCharArray();
        for (int i = 0; i < a.Length; i++)
            a[i] = a[i] switch
            {
                'О' or 'о' or 'O' or 'o' or 'Q' or 'D' => '0',
                'З' or 'з' => '3',
                'I' or 'l' or '|' or 'і' or 'І' or '!' => '1',
                'б' or 'Б' => '6',
                'S' or 's' => '5',
                'В' => '8',
                'Ч' or 'ч' => '4',
                _ => a[i],
            };
        return new string(a);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
