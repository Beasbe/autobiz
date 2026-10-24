using System.Text.RegularExpressions;
using AutoParser.Core.Validate;

namespace AutoParser.Core.Parsing;

public enum ValueType
{
    Text, MakeModel, Plate, Vin, Body, Integer, Year, Date, Sts, Pts, Person, Color, VehicleType, Category, Policy, Place, Power, Gearbox, Stop,
}

/// <summary>Проверка и нормализация значения по типу поля. Factor — множитель уверенности (правки OCR снижают её).</summary>
public static partial class ValueTypes
{
    public readonly record struct Parsed(string Value, double Factor);

    public static Parsed? Parse(ValueType type, string raw, string? labelKey = null)
    {
        raw = TextNorm.TrimSeparators(TextNorm.CollapseSpaces(JunkRx().Replace(raw.Replace('№', ' ').Replace("N9", " "), " ")));
        if (raw.Length == 0) return null;
        switch (type)
        {
            case ValueType.Plate:
            {
                var p = PlateNormalizer.Normalize(raw) ?? PlateNormalizer.FindAll(raw).FirstOrDefault();
                return p is null ? null : new Parsed(p, Changed(raw, p) ? 0.9 : 1);
            }
            case ValueType.Vin:
            {
                var v = VinValidator.Normalize(raw) ?? VinValidator.FindAll(raw).FirstOrDefault()
                        ?? VinValidator.Normalize(raw.Replace(" ", ""));
                if (v is null) return null;
                return new Parsed(v, raw.Replace(" ", "").Equals(v, StringComparison.OrdinalIgnoreCase) ? 1 : 0.8);
            }
            case ValueType.Body:
            {
                if (IsAbsent(raw)) return new Parsed("отсутствует", 1);
                var v = VinValidator.Normalize(raw.Replace(" ", ""));
                if (v is not null) return new Parsed(v, 1);
                var alnum = new string(raw.Where(char.IsLetterOrDigit).ToArray());
                // 16–18 символов — это почти наверняка испорченный OCR VIN: лучше взять VIN.
                if (alnum.Length is >= 16 and <= 20) return null;
                return alnum.Length is >= 6 and <= 20 && alnum.Count(char.IsDigit) >= 4 ? new Parsed(alnum.ToUpperInvariant(), 0.7) : null;
            }
            case ValueType.Integer:
            {
                var n = NumberNormalizer.Integer(raw, 1, 7);
                if (n is null || !raw.TrimStart().Take(3).Any(char.IsDigit)) return null;
                return new Parsed(n, 1);
            }
            case ValueType.Year:
            {
                var m = YearRx().Match(TextNorm.FixDigits(raw));
                return m.Success && int.Parse(m.Value) <= DateTime.Today.Year + 1 ? new Parsed(m.Value, 1) : null;
            }
            case ValueType.Date:
            {
                var d = DateNormalizer.First(raw);
                return d is null ? null : new Parsed(DateNormalizer.Format(d.Value), 1);
            }
            case ValueType.Sts:
            {
                var s = StsNormalizer.Normalize(raw);
                return s is null ? null : new Parsed(s, 1);
            }
            case ValueType.Pts:
            {
                var s = PtsNormalizer.Normalize(raw);
                return s is null ? null : new Parsed(s, 1);
            }
            case ValueType.Person:
            {
                var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => w.Trim('.', ',', '-', '–', '"', '«', '»'))
                    .Where(w => w.Length > 0).ToList();
                if (words.Count < 2 || words.Count > 6) return null;
                if (!words.All(w => w.All(c => IsCyr(c) || c == '-') && w.Length >= 2)) return null;
                if (words.Any(w => StopWords.Contains(w.ToLowerInvariant()) || OrgRx().IsMatch(w))) return null;
                return new Parsed(TextNorm.TitleCasePerson(string.Join(" ", words)), 1);
            }
            case ValueType.Color:
            {
                if (!raw.Any(IsCyr) || raw.Length > 60) return null;
                if (!ColorRx().IsMatch(raw)) return null;
                return new Parsed(TextNorm.Capitalize(raw), 1);
            }
            case ValueType.VehicleType:
            {
                var key = TextNorm.Key(raw);
                if (!VehicleTypeRx().IsMatch(key)) return null;
                return new Parsed(TextNorm.Capitalize(raw), 1);
            }
            case ValueType.Category:
            {
                var c = NormalizeCategory(raw);
                return c is null ? null : new Parsed(c, 1);
            }
            case ValueType.Policy:
            {
                var p = PolicyNormalizer.FindAll(raw).FirstOrDefault();
                return p is null ? null : new Parsed(p.ToString(), 1);
            }
            case ValueType.Power:
            {
                var p = PowerParser.Parse(raw) ?? PowerParser.ParseBare(raw, labelKey ?? "");
                return p is null ? null : new Parsed(p.ToString(), p.Computed ? 0.85 : 1);
            }
            case ValueType.Gearbox:
            {
                var g = GearboxDetector.Detect(raw);
                return g is null ? null : new Parsed(g, 1);
            }
            case ValueType.Place:
            {
                if (raw.Count(IsCyr) < 4 || raw.Length > 200 || IsAbsent(raw)) return null;
                // Хвост метки «(субъект Российской Федерации)» вместо значения.
                if (raw.StartsWith('(') || TextNorm.Key(raw).Contains("российской федерации") && raw.Length < 45) return null;
                if (NotPlaceRx().IsMatch(TextNorm.Key(raw))) return null;
                // Мусор OCR: нужны «настоящие» слова.
                var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var good = tokens.Count(w => w.Count(IsCyr) >= 3 && w.Count(IsCyr) >= w.Count(char.IsLetter) * 0.8);
                if (good < 1 || good < tokens.Length * 0.4) return null;
                if (tokens.Length > 20) return null;
                if (!AddressRx().IsMatch(raw) && RegionDirectory.Default.ByPlace(raw) is null) return null;
                return new Parsed(raw, 1);
            }
            case ValueType.MakeModel:
            {
                if (raw.Count(char.IsLetter) < 2 || raw.Length > 80) return null;
                if (NotMakeRx().IsMatch(TextNorm.Key(raw))) return null;
                var v = Regex.Replace(raw, @"\s*/\s*(19|20)\d{2}\s*$", "");
                return new Parsed(TextNorm.CollapseSpaces(v), 1);
            }
            case ValueType.Text:
                return raw.Count(char.IsLetterOrDigit) >= 2 ? new Parsed(raw, 1) : null;
            default:
                return null;
        }
    }

    /// <summary>«Б/М1», «B», «В / M1» → «B/М1» (как в шаблоне: латинская B, кириллическая М).</summary>
    public static string? NormalizeCategory(string raw)
    {
        var s = TextNorm.ToCyrillicLookalikes(raw.ToUpperInvariant()).Replace(" ", "");
        var m = CategoryRx().Match(s);
        if (!m.Success) return null;
        var cat = m.Groups[1].Value switch { "Б" or "В" => "B", "А" => "A", "С" => "C", "Д" => "D", "Е" => "E", var x => x };
        var sub = m.Groups[2].Success && m.Groups[2].Value.Length > 0 ? m.Groups[2].Value.Replace('M', 'М').Replace('N', 'N') : null;
        return sub is null ? cat : $"{cat}/{sub}";
    }

    /// <summary>Категория по типу ТС, если в документах её нет: «Легковой …» → B/М1.</summary>
    public static string? CategoryFromType(string vehicleType)
    {
        var k = TextNorm.Key(vehicleType);
        if (k.StartsWith("легков")) return "B/М1";
        if (k.Contains("грузов")) return "C/N2";
        if (k.Contains("автобус")) return "D/М3";
        if (k.Contains("мотоцикл")) return "A/L3";
        return null;
    }

    public static bool IsAbsent(string s) => AbsentRx().IsMatch(TextNorm.Key(s));

    private static bool Changed(string raw, string norm) =>
        !new string(raw.Where(char.IsLetterOrDigit).ToArray()).Equals(norm, StringComparison.OrdinalIgnoreCase);

    private static bool IsCyr(char c) => c is >= 'А' and <= 'я' or 'Ё' or 'ё';

    private static readonly HashSet<string> StopWords =
        ["серия", "номер", "дата", "адрес", "область", "улица", "паспорт", "российской", "федерации", "субъект", "владелец", "собственник", "транспортного", "средства", "отсутствует", "дтп", "полис", "страхователь"];

    [GeneratedRegex(@"^(дата|время|номер|серия|сведения|данные|информация|фио|подпись|вид|наименование)|заявлени|возмещени|водител|управлявш|обстоятельств")]
    private static partial Regex NotPlaceRx();

    [GeneratedRegex(@"^(компани|групп|страхов|банк|обществ|ооо$|ао$|пао$|сао$|ип$|филиал)", RegexOptions.IgnoreCase)]
    private static partial Regex OrgRx();

    [GeneratedRegex(@"(^|[\s,.(])(г|гор|город|ул|улица|обл|область|край|респ|республика|р-н|район|с|село|пос|п|пгт|д|деревня|км|трасса|шоссе|пр-т|проспект|пер|переулок|наб|бульвар|автодорога|мкр|а/д)([\s.,]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex AddressRx();

    [GeneratedRegex(@"бел|черн|сер|серебр|красн|син|голуб|зел[её]н|желт|жёлт|оранж|коричн|беж|фиолет|бордо|вишн|золот|бронз|графит|асфальт|хаки|розов|пурпур|сирен|темн|тёмн|светл|бирюз|песоч|слонов|кремов|охр|антрацит|металлик|перламутр", RegexOptions.IgnoreCase)]
    private static partial Regex ColorRx();

    [GeneratedRegex(@"^гос|категори|государствен|регистрац|идентифик|год выпуска|тип тс|номер|наименован|серия|свидетельств")]
    private static partial Regex NotMakeRx();

    [GeneratedRegex(@"[•·■□☐☑✓►▪¤°®©™'`´^~]+")]
    private static partial Regex JunkRx();

    [GeneratedRegex(@"(?<!\d)(19[5-9]\d|20[0-4]\d)(?!\d)")]
    private static partial Regex YearRx();

    [GeneratedRegex(@"^(легков|грузов|автобус|мотоцикл|специальн|прицеп|седельн|фургон)")]
    private static partial Regex VehicleTypeRx();

    [GeneratedRegex(@"^([АВБСДЕABCDE])(?:/?([МMN]\d[G]?))?")]
    private static partial Regex CategoryRx();

    [GeneratedRegex(@"^(отсутств|нет|не установлен|не указан|б н$|\-$)")]
    private static partial Regex AbsentRx();
}

/// <summary>Тип КПП по описанию комплектации: «5-ступ КПП», «АКПП», «Nexia 1.5i МТ 80 л.с.».</summary>
public static partial class GearboxDetector
{
    public static string? Detect(string text)
    {
        var s = TextNorm.ToCyrillicLookalikes(text.ToUpperInvariant());
        if (AutoRx().IsMatch(s)) return "АКПП";
        if (CvtRx().IsMatch(s)) return "Вариатор";
        if (RobotRx().IsMatch(s)) return "РКПП";
        if (ManualRx().IsMatch(s)) return "МКПП";
        // «1.5i МТ 80 л.с.» — короткие обозначения только рядом с описанием двигателя.
        if (EngineContextRx().IsMatch(s))
        {
            if (ShortAutoRx().IsMatch(s)) return "АКПП";
            if (ShortManualRx().IsMatch(s)) return "МКПП";
        }
        return null;
    }

    [GeneratedRegex(@"\bАКПП\b|АВТОМАТ\w*\.?\s*(КПП|КОРОБК|ТРАНСМИС)|\d-?СТУП\w*\.?\s*АКПП|STEPTRONIC|ТИПТРОНИК")]
    private static partial Regex AutoRx();

    [GeneratedRegex(@"ВАРИАТОР|\bСVТ\b|\bCVT\b")]
    private static partial Regex CvtRx();

    [GeneratedRegex(@"\bРКПП\b|РОБОТ\w*\s*(КПП|КОРОБК)|\bDSG\b")]
    private static partial Regex RobotRx();

    [GeneratedRegex(@"\bМКПП\b|МЕХАНИЧ\w*\.?\s*(КПП|КОРОБК|ТРАНСМИС)|\d-?СТУП\w*\.?\s*(МЕХ\w*\s*)?КПП")]
    private static partial Regex ManualRx();

    [GeneratedRegex(@"\d[.,]\d\s*[IТТDД]?\b|Л\.\s?С|СМ3|СМЗ")]
    private static partial Regex EngineContextRx();

    [GeneratedRegex(@"\bАТ\b")]
    private static partial Regex ShortAutoRx();

    [GeneratedRegex(@"\bМТ\b")]
    private static partial Regex ShortManualRx();
}

/// <summary>
/// Тип ЛКП в виде, принятом в заключениях: «Акриловая двухслойная», «Металлик двухслойный», «Перламутровый трехслойный».
/// Понимает сокращения Audatex: «2-СЛ АКРИЛОВ ОКРАСКА», «2-СЛОЙН МЕТАЛЛИК».
/// </summary>
public static partial class PaintTypeDetector
{
    public static string? Detect(string text)
    {
        if (!ContextRx().IsMatch(text)) return null;
        string? baseWord = null; bool feminine = false;
        if (AcrylRx().IsMatch(text)) { baseWord = "Акриловая"; feminine = true; }
        else if (PearlRx().IsMatch(text)) baseWord = "Перламутровый";
        else if (MetallicRx().IsMatch(text)) baseWord = "Металлик";
        else if (MattRx().IsMatch(text)) { baseWord = "Матовая"; feminine = true; }

        string? layers = null;
        if (TwoRx().IsMatch(text)) layers = "двухслойн";
        else if (ThreeRx().IsMatch(text)) layers = "трехслойн";
        else if (OneRx().IsMatch(text)) layers = "однослойн";

        if (baseWord is null && layers is null) return null;
        if (baseWord is null) return char.ToUpperInvariant(layers![0]) + layers[1..] + "ая";
        return layers is null ? baseWord : $"{baseWord} {layers}{(feminine ? "ая" : "ый")}";
    }

    [GeneratedRegex(@"слойн|\d\s*-?\s*х?\s*СЛ\b|акрил|металлик|перламутр|матов|ЛКП|окрас|краск|эмал", RegexOptions.IgnoreCase)]
    private static partial Regex ContextRx();

    [GeneratedRegex(@"акрил", RegexOptions.IgnoreCase)]
    private static partial Regex AcrylRx();

    [GeneratedRegex(@"перламутр", RegexOptions.IgnoreCase)]
    private static partial Regex PearlRx();

    [GeneratedRegex(@"металлик", RegexOptions.IgnoreCase)]
    private static partial Regex MetallicRx();

    [GeneratedRegex(@"\bматов", RegexOptions.IgnoreCase)]
    private static partial Regex MattRx();

    [GeneratedRegex(@"двух\s*-?\s*сл|(?<!\d)2\s*-?\s*х?\s*-?\s*сл", RegexOptions.IgnoreCase)]
    private static partial Regex TwoRx();

    [GeneratedRegex(@"тр[её]х\s*-?\s*сл|(?<!\d)3\s*-?\s*х?\s*-?\s*сл", RegexOptions.IgnoreCase)]
    private static partial Regex ThreeRx();

    [GeneratedRegex(@"одно\s*-?\s*сл|(?<!\d)1\s*-?\s*сл", RegexOptions.IgnoreCase)]
    private static partial Regex OneRx();
}
