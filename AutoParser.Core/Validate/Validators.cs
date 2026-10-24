using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AutoParser.Core.Validate;

/// <summary>Государственный регистрационный знак: Н222ОУ152.</summary>
public static partial class PlateNormalizer
{
    private const string PlateLetters = "АВЕКМНОРСТУХ";

    [GeneratedRegex(@"^[АВЕКМНОРСТУХ]\d{3}[АВЕКМНОРСТУХ]{2}\d{2,3}$")]
    public static partial Regex Pattern();

    /// <summary>Нормализует строку-кандидат. Возвращает null, если это не похоже на ГРЗ.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = TextNorm.ToCyrillicLookalikes(raw.ToUpperInvariant());
        s = new string(s.Where(char.IsLetterOrDigit).ToArray());
        if (s.Length is < 8 or > 9) return null;

        var a = s.ToCharArray();
        for (int i = 0; i < a.Length; i++)
        {
            bool letterPos = i == 0 || i == 4 || i == 5;
            a[i] = letterPos ? ToPlateLetter(a[i]) : ToDigit(a[i]);
        }
        var r = new string(a);
        return Pattern().IsMatch(r) ? r : null;
    }

    /// <summary>Ищет ГРЗ в произвольной строке (в т.ч. «г/н Е41 ОТУ/З6»).</summary>
    public static IEnumerable<string> FindAll(string text)
    {
        var upper = TextNorm.ToCyrillicLookalikes(text.ToUpperInvariant());
        foreach (Match m in Candidate().Matches(upper))
        {
            var n = Normalize(m.Value);
            if (n is not null) yield return n;
        }
    }

    public static int? RegionCode(string plate) =>
        Pattern().IsMatch(plate) && int.TryParse(plate[6..], out var c) ? c : null;

    private static char ToPlateLetter(char c) => c switch
    {
        '0' => 'О', '8' => 'В', '4' => 'А', '3' => 'З',
        _ => c,
    };

    private static char ToDigit(char c) => c switch
    {
        'О' or 'Q' or 'D' => '0', 'З' => '3', 'I' or 'L' or '|' or 'І' => '1',
        'Б' => '6', 'Ч' => '4', 'S' => '5', 'В' => '8',
        _ => c,
    };

    // Буква, 3 цифры, 2 буквы, 2–3 цифры, с возможными пробелами/дробями от OCR («Е41 ОТУ/З6»).
    [GeneratedRegex(@"(?<![А-ЯA-Z0-9])[А-ЯA-Z0-9]\s?[0-9ОЗI|]\s?[0-9ОЗI|]\s?[0-9ОЗI|]\s?[А-ЯA-Z]\s?[А-ЯA-Z]\s?/?\s?[0-9ОЗI|БВ]\s?[0-9ОЗI|БВ](?:[0-9ОЗI|БВ])?(?![А-ЯA-Z0-9])")]
    private static partial Regex Candidate();
}

/// <summary>VIN: 17 символов, без I, O, Q.</summary>
public static partial class VinValidator
{
    [GeneratedRegex(@"^[A-HJ-NPR-Z0-9]{17}$")]
    public static partial Regex Pattern();

    /// <summary>Приводит к латинице, O→0, I→1, Q→0, убирает пробелы. null — не VIN.</summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var sb = new StringBuilder();
        foreach (var c0 in raw.ToUpperInvariant())
        {
            var c = Map(c0);
            if (c != '\0') sb.Append(c);
        }
        var s = sb.ToString();
        return Pattern().IsMatch(s) ? s : null;
    }

    /// <summary>Ищет VIN в строке, в том числе написанный с пробелами между символами.</summary>
    public static IEnumerable<string> FindAll(string text)
    {
        var up = text.ToUpperInvariant();
        foreach (Match m in Candidate().Matches(up))
        {
            var n = Normalize(m.Value);
            if (n is not null && n.Any(char.IsLetter) && n.Count(char.IsDigit) >= 4) yield return n;
        }
        // «W B A H F 3 1 0 2 0 W X 1 6 3 6 2»
        foreach (Match m in Spaced().Matches(up))
        {
            var n = Normalize(m.Value);
            if (n is not null && n.Any(char.IsLetter)) yield return n;
        }
    }

    /// <summary>Число несовпадающих позиций (VIN из OCR часто отличается на 1–2 символа).</summary>
    public static int Distance(string a, string b) =>
        a.Length != b.Length ? int.MaxValue : a.Zip(b).Count(p => p.First != p.Second);

    /// <summary>Контрольная цифра (позиция 9) — обязательна только для Северной Америки, поэтому лишь как подсказка.</summary>
    public static bool CheckDigitValid(string vin)
    {
        if (!Pattern().IsMatch(vin)) return false;
        const string letters = "ABCDEFGHJKLMNPRSTUVWXYZ";
        int[] values = [1, 2, 3, 4, 5, 6, 7, 8, 1, 2, 3, 4, 5, 7, 9, 2, 3, 4, 5, 6, 7, 8, 9];
        int[] w = [8, 7, 6, 5, 4, 3, 2, 10, 0, 9, 8, 7, 6, 5, 4, 3, 2];
        int sum = 0;
        for (int i = 0; i < 17; i++)
        {
            var c = vin[i];
            int v = char.IsDigit(c) ? c - '0' : values[letters.IndexOf(c)];
            sum += v * w[i];
        }
        var r = sum % 11;
        return vin[8] == (r == 10 ? 'X' : (char)('0' + r));
    }

    private static char Map(char c) => c switch
    {
        >= 'A' and <= 'Z' or >= '0' and <= '9' => c switch { 'O' => '0', 'Q' => '0', 'I' => '1', _ => c },
        'А' => 'A', 'В' => 'B', 'С' => 'C', 'Е' => 'E', 'Н' => 'H', 'К' => 'K', 'М' => 'M', 'О' => '0',
        'Р' => 'P', 'Т' => 'T', 'Х' => 'X', 'У' => 'Y', 'З' => '3', 'Ш' => 'W', 'Ь' => '6', 'Б' => '6',
        'Г' => 'r', 'П' => 'N', 'Ч' => '4', 'І' => '1', '|' => '1',
        _ => '\0',
    };

    [GeneratedRegex(@"(?<![A-ZА-Я0-9])[A-ZА-Я0-9]{17}(?![A-ZА-Я0-9])")]
    private static partial Regex Candidate();

    [GeneratedRegex(@"(?<![A-ZА-Я0-9])(?:[A-ZА-Я0-9] ){16}[A-ZА-Я0-9](?![A-ZА-Я0-9])")]
    private static partial Regex Spaced();
}

/// <summary>СТС: «99 65 № 680398».</summary>
public static partial class StsNormalizer
{
    [GeneratedRegex(@"(?<!\d)(\d{2})\s?(\d{2}|[0-9]{1}[А-ЯA-Z]{1}|[А-ЯA-Z]{2})\s?(?:№|N[o°9Q2]?|Ne)?\s?(\d{6})(?!\d)")]
    private static partial Regex Pattern();

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var m = Pattern().Match(raw.ToUpperInvariant());
        return m.Success ? $"{m.Groups[1].Value} {TextNorm.ToCyrillicLookalikes(m.Groups[2].Value)} № {m.Groups[3].Value}" : null;
    }

    public static IEnumerable<string> FindAll(string text)
    {
        foreach (Match m in Pattern().Matches(text.ToUpperInvariant()))
            yield return $"{m.Groups[1].Value} {TextNorm.ToCyrillicLookalikes(m.Groups[2].Value)} № {m.Groups[3].Value}";
    }

    /// <summary>«Серия - 9965», «Номер - 680398» → «99 65 № 680398».</summary>
    public static string? FromSeriesNumber(string series, string number)
    {
        var s = new string(series.Where(char.IsLetterOrDigit).ToArray());
        var n = new string(number.Where(char.IsDigit).ToArray());
        if (s.Length != 4 || n.Length != 6) return null;
        return $"{s[..2]} {TextNorm.ToCyrillicLookalikes(s[2..])} № {n}";
    }
}

/// <summary>ПТС: «77 ОХ № 739280» или ЭПТС из 15 цифр.</summary>
public static partial class PtsNormalizer
{
    [GeneratedRegex(@"(?<!\d)(\d{2})\s?([А-ЯA-Z]{2})\s?(?:№|N)?\s?(\d{6})(?!\d)")]
    private static partial Regex Paper();

    [GeneratedRegex(@"(?<!\d)(\d{15})(?!\d)")]
    private static partial Regex Electronic();

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var up = raw.ToUpperInvariant();
        var m = Paper().Match(up);
        if (m.Success) return $"{m.Groups[1].Value} {TextNorm.ToCyrillicLookalikes(m.Groups[2].Value)} № {m.Groups[3].Value}";
        var e = Electronic().Match(up.Replace(" ", ""));
        return e.Success ? $"ЭПТС {e.Groups[1].Value}" : null;
    }
}

/// <summary>Мощность «190 КВТ/258,3 Л.С.», «80 л.с.» → «190/258,3».</summary>
public static partial class PowerParser
{
    public const double HpPerKw = 1.35962;

    [GeneratedRegex(@"(\d{2,3}(?:[.,]\d{1,2})?)\s*(?:КВТ|KW|КW|KВТ)\s*[/\\(]?\s*(\d{2,4}(?:[.,]\d{1,2})?)\s*(?:Л\.?\s*С|HP|ЛС)", RegexOptions.IgnoreCase)]
    private static partial Regex KwHp();

    [GeneratedRegex(@"(\d{2,4}(?:[.,]\d{1,2})?)\s*(?:Л\.\s?С\.?|Л/С|ЛС\b|HP\b)", RegexOptions.IgnoreCase)]
    private static partial Regex HpOnly();

    [GeneratedRegex(@"(\d{2,3}(?:[.,]\d{1,2})?)\s*(?:КВТ|KW)\b", RegexOptions.IgnoreCase)]
    private static partial Regex KwOnly();

    [GeneratedRegex(@"(?:Мощность[^\d]{0,40})(\d{2,3}(?:[.,]\d)?)\s*/\s*(\d{2,4}(?:[.,]\d)?)", RegexOptions.IgnoreCase)]
    private static partial Regex Labeled();

    public sealed record Power(double Kw, double Hp, bool Computed)
    {
        public override string ToString() => $"{Fmt(Kw)}/{Fmt(Hp)}";
    }

    public static Power? Parse(string text)
    {
        var m = KwHp().Match(text);
        if (m.Success) return Make(Num(m.Groups[1].Value), Num(m.Groups[2].Value));
        m = Labeled().Match(text);
        if (m.Success) return Make(Num(m.Groups[1].Value), Num(m.Groups[2].Value));
        m = KwOnly().Match(text);
        if (m.Success) { var kw = Num(m.Groups[1].Value); return Valid(kw, kw * HpPerKw) ? new Power(kw, Math.Round(kw * HpPerKw, 1), true) : null; }
        m = HpOnly().Match(text);
        if (m.Success) { var hp = Num(m.Groups[1].Value); return Valid(hp / HpPerKw, hp) ? new Power(Math.Round(hp / HpPerKw, 1), hp, true) : null; }
        return null;
    }

    /// <summary>Число без единиц: единица берётся из подписи поля («Мощность (кВт)», «Мощность (л.с.)»).</summary>
    public static Power? ParseBare(string raw, string labelKey)
    {
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"^\s*(\d{2,4}(?:[.,]\d{1,2})?)\s*$");
        if (!m.Success) return null;
        var v = Num(m.Groups[1].Value);
        bool kw = labelKey.Contains("квт") || labelKey.Contains("kw");
        bool hp = labelKey.Contains("л с") || labelKey.Contains("лс") || labelKey.Contains("лес");
        if (kw) return Valid(v, v * HpPerKw) ? new Power(v, Math.Round(v * HpPerKw, 1), true) : null;
        if (hp) return Valid(v / HpPerKw, v) ? new Power(Math.Round(v / HpPerKw, 1), v, true) : null;
        return null;
    }

    private static Power? Make(double kw, double hp) => Valid(kw, hp) && Math.Abs(kw * HpPerKw - hp) < hp * 0.05 ? new Power(kw, hp, false) : null;

    private static bool Valid(double kw, double hp) => kw is >= 20 and <= 800 && hp is >= 25 and <= 1100;

    private static double Num(string s) => double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture);

    public static string Fmt(double v) => v.ToString(v % 1 == 0 ? "0" : "0.#", CultureInfo.GetCultureInfo("ru-RU"));
}

public static partial class DateNormalizer
{
    [GeneratedRegex(@"(?<!\d)([0-3]?[0-9OОЗ])\s?[.,/-]\s?([01]?[0-9OОЗ])\s?[.,/-]\s?((?:19|20)[0-9OОЗ]{2})(?!\d)")]
    private static partial Regex Numeric();

    private static readonly string[] Months =
        ["январ", "феврал", "март", "апрел", "ма", "июн", "июл", "август", "сентябр", "октябр", "ноябр", "декабр"];

    [GeneratedRegex(@"«?(\d{1,2})»?\s+(январ|феврал|март|апрел|ма[яй]|июн|июл|август|сентябр|октябр|ноябр|декабр)[а-я]*\s+(\d{4})", RegexOptions.IgnoreCase)]
    private static partial Regex Verbal();

    public static IEnumerable<DateOnly> FindAll(string text)
    {
        foreach (Match m in Numeric().Matches(text))
        {
            if (TryMake(TextNorm.FixDigits(m.Groups[1].Value), TextNorm.FixDigits(m.Groups[2].Value),
                    TextNorm.FixDigits(m.Groups[3].Value), out var d)) yield return d;
        }
        foreach (Match m in Verbal().Matches(text))
        {
            var mon = Array.FindIndex(Months, x => m.Groups[2].Value.ToLowerInvariant().StartsWith(x)) + 1;
            if (TryMake(m.Groups[1].Value, mon.ToString(), m.Groups[3].Value, out var d)) yield return d;
        }
    }

    public static DateOnly? First(string text) => FindAll(text).Select(d => (DateOnly?)d).FirstOrDefault();

    public static string Format(DateOnly d) => d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    private static bool TryMake(string d, string m, string y, out DateOnly date)
    {
        date = default;
        if (!int.TryParse(d, out var dd) || !int.TryParse(m, out var mm) || !int.TryParse(y, out var yy)) return false;
        if (yy < 1950 || yy > DateTime.Today.Year + 1 || mm is < 1 or > 12 || dd < 1 || dd > DateTime.DaysInMonth(yy, mm)) return false;
        date = new DateOnly(yy, mm, dd);
        return true;
    }
}

/// <summary>Полис ОСАГО: «ХХХ №0390237711».</summary>
public static partial class PolicyNormalizer
{
    // Серии ОСАГО: ХХХ, ТТТ, ААВ, ААС, ААК, ААМ, ААН, ААО, ААР, ААЕ, РРР, МММ, ННН, ЕЕЕ, ККК, ВВВ, ССС.
    [GeneratedRegex(@"(?<![А-ЯA-Z])([ХXТTАAРPМMНHЕEКKВBСCО0O]{3})\s*(?:№|N[o°9Q2]?|Ne)?\s*([0-9ОOЗIНH|]{10})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex Pattern();

    [GeneratedRegex(@"N[QoО°92](?=[ХXТTАA])")]
    private static partial Regex OcrNumberSign();

    private static readonly HashSet<string> Series =
        ["ХХХ", "ТТТ", "ААВ", "ААС", "ААК", "ААМ", "ААН", "ААО", "ААР", "ААЕ", "РРР", "МММ", "ННН", "ЕЕЕ", "ККК", "ВВВ", "ССС"];

    public sealed record Policy(string Series, string Number, int Index, int Length)
    {
        public override string ToString() => $"{Series} №{Number}";
    }

    public static IEnumerable<Policy> FindAll(string text)
    {
        // «NQXXX0622257614» — OCR знака «№» перед серией.
        text = OcrNumberSign().Replace(text, "№ ");
        foreach (Match m in Pattern().Matches(text))
        {
            var series = TextNorm.ToCyrillicLookalikes(m.Groups[1].Value.ToUpperInvariant()).Replace('0', 'О');
            if (!Series.Contains(series)) continue;
            var number = TextNorm.FixDigits(m.Groups[2].Value.Replace('Н', '1').Replace('H', '1'));
            if (number.All(char.IsDigit)) yield return new Policy(series, number, m.Index, m.Length);
        }
    }
}

/// <summary>Числа из OCR: «221 049» → «221049», «1 ооо.оо» → 1000.</summary>
public static class NumberNormalizer
{
    public static string? Integer(string? raw, int minDigits = 1, int maxDigits = 7)
    {
        if (raw is null) return null;
        var fixedS = TextNorm.FixDigits(raw);
        var m = System.Text.RegularExpressions.Regex.Match(fixedS, @"\d[\d\s]*\d|\d");
        if (!m.Success) return null;
        var digits = new string(m.Value.Where(char.IsDigit).ToArray());
        return digits.Length >= minDigits && digits.Length <= maxDigits ? digits.TrimStart('0').PadLeft(1, '0') : null;
    }

    public static double? Decimal(string raw)
    {
        var s = TextNorm.FixDigits(raw).Replace(" ", "").Replace(' ', ' ').Replace(" ", "").Replace(',', '.').Replace('-', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
