using System.Globalization;
using System.Text.RegularExpressions;
using AutoParser.Core.Model;
using AutoParser.Core.Text;
using AutoParser.Core.Validate;

namespace AutoParser.Core.Parsing;

/// <summary>
/// Таблица «Описание поврежденных элементов и видов ремонтных воздействий».
/// Источник 1 — калькуляция Audatex в заключении страховщика: «Запасные части» → Замена,
/// «РЕМОНТ …» → Ремонт N н/ч, «ОКРАСКА …» → окраска / ремонтная окраска; С/У и подготовительные работы пропускаются.
/// Источник 2 (если калькуляции нет) — перечень повреждений в акте осмотра.
/// Колонки «Исследование ФО / заявителя / относимость» остаются эксперту.
/// </summary>
public sealed partial class DamageExtractor
{
    private readonly AudatexNames _names;

    public DamageExtractor(AudatexNames? names = null) => _names = names ?? AudatexNames.Default;

    private enum Section { None, Parts, Works, Paint }

    private sealed class Element
    {
        public required string Name;
        public required string Raw;
        public bool Replace;
        public double RepairHours;
        public string? Paint;
        public double Confidence;
        public SourceRef? Source;
        public int Order;
    }

    public List<DamageRow> Extract(IEnumerable<CaseDocument> docs)
    {
        var list = docs.Where(d => d.Text is not null && d.SkipReason is null).ToList();
        // Калькуляция и акт осмотра часто подшиты внутрь других PDF («док.pdf»), поэтому смотрим все документы.
        var fromCalc = list.Where(d => d.Type is not (DocumentType.ApplicantExpertise or DocumentType.Photos))
            .OrderByDescending(d => d.Type == DocumentType.InsurerReport)
            .Select(FromCalculation).Where(r => r.Count > 0)
            .OrderByDescending(r => r.Sum(x => x.Confidence)).FirstOrDefault();
        var fromAct = list.Where(d => d.Type is not (DocumentType.ApplicantExpertise or DocumentType.Photos))
            .Select(FromInspectionAct).Where(r => r.Count > 0)
            .OrderByDescending(r => r.Count).FirstOrDefault();

        var rows = fromCalc ?? fromAct ?? [];
        // Характер повреждения — из акта осмотра, если он распознан.
        if (fromCalc is not null && fromAct is not null)
            foreach (var r in rows)
            {
                var m = fromAct.FirstOrDefault(a => TextNorm.Key(a.Element).StartsWith(TextNorm.Key(r.Element).Split(' ')[0])
                                                    && SameSide(a.Element, r.Element));
                if (m is not null && m.Character.Length > 0) r.Character = m.Character;
            }
        for (int i = 0; i < rows.Count; i++) rows[i].No = i + 1;
        return rows;
    }

    private static bool SameSide(string a, string b)
    {
        static string Side(string s) => Regex.IsMatch(s, "лев", RegexOptions.IgnoreCase) ? "L" : Regex.IsMatch(s, "прав", RegexOptions.IgnoreCase) ? "R" : "";
        return Side(a) == Side(b);
    }

    /// <summary>Строки калькуляции Audatex (OCR-скан или текстовый слой).</summary>
    public List<DamageRow> FromCalculation(CaseDocument doc)
    {
        var elements = new Dictionary<string, Element>();
        int order = 0;
        foreach (var page in doc.Text!.Pages)
        {
            var pageKey = TextNorm.Key(page.FullText);
            if (!(pageKey.Contains("калькуляц") || pageKey.Contains("запасные части")) || !(pageKey.Contains("работы") || pageKey.Contains("окраск")))
                continue;

            // Строки таблицы по координатам слов (Windows OCR отдаёт текст по колонкам).
            var rows = LineBuilder.Build(page.Lines.SelectMany(l => l.Words), overlap: 0.45, columnGap: 1e9)
                .OrderBy(r => r.Box.Y).ToList();
            var section = Section.None;
            foreach (var row in rows)
            {
                var key = TextNorm.Key(row.Text);
                if (key.StartsWith("запасные части") || key == "запасные части") { section = Section.Parts; continue; }
                if (key.StartsWith("работы") || key.EndsWith(" работы") && key.Length < 20) { section = Section.Works; continue; }
                if (key is "окраска" or "лакокраска" || key.StartsWith("окраска ") && key.Length < 25 || key.StartsWith("лакокрасочные")) { section = Section.Paint; continue; }
                if (section == Section.None) continue;
                if (key.StartsWith("итого") || key.StartsWith("всего") || key.StartsWith("стоимость") || key.StartsWith("затраты"))
                { section = Section.None; continue; }

                var rowUpper = row.Text.ToUpperInvariant();
                var conf = LabelValueParser.BaseConfidence(page, row);
                var src = new SourceRef(doc.FilePath, page.PageNumber, row.Box, page.Origin, row.Text);

                // «РЕМ.ОКРАСКА «500,6 (БОКОВАЯ СТЕНКА П Л)» — элемент в скобках.
                if (PaintRx().IsMatch(rowUpper) && !rowUpper.Contains("ПОДГ") && !rowUpper.Contains("ПОДБОР"))
                {
                    var target = ParenRx().Match(rowUpper) is { Success: true } pm ? pm.Groups[1].Value
                        : PaintRx().Replace(ExtractName(row)?.ToUpperInvariant() ?? "", " ");
                    var kind = rowUpper.Contains("РЕМ") ? "ремонтная окраска" : rowUpper.Contains("ПОВЕРХ") ? "окраска поверхностная" : "окраска";
                    var el = Get(elements, target, conf, src, ref order);
                    if (el is not null) el.Paint = kind;
                    continue;
                }

                var name = ExtractName(row);
                if (name is null) continue;
                var upper = name.ToUpperInvariant();
                if (_names.IsSkipWord(upper.Split(' ')[0])) continue;
                if (section == Section.Parts)
                {
                    var el = Get(elements, upper, conf, src, ref order);
                    if (el is not null) el.Replace = true;
                    continue;
                }
                // Работы: «РЕМОНТ …» / «… - РЕМОНТ» с нормативом; С/У («- СМ», «С-У», «СНЯТЬ/УСТАН») — вспомогательные.
                if (RepairRx().IsMatch(upper) && !AuxRx().IsMatch(upper))
                {
                    var target = RepairRx().Replace(upper, " ");
                    var el = Get(elements, target, conf, src, ref order);
                    if (el is not null) el.RepairHours += Hours(row.Text);
                    continue;
                }
                if (ReplaceRx().IsMatch(upper) && !AuxRx().IsMatch(upper))
                {
                    var el = Get(elements, ReplaceRx().Replace(upper, " "), conf, src, ref order);
                    if (el is not null) el.Replace = true;
                }
            }
        }

        return elements.Values.Where(e => (e.Replace || e.RepairHours > 0 || e.Paint is not null) && _names.HasKnownNoun(e.Raw))
            .OrderBy(e => e.Order)
            .Select(e => new DamageRow
            {
                Element = e.Name,
                Conclusion = Conclusion(e),
                RawText = e.Raw,
                Confidence = Math.Round(e.Confidence, 2),
                Source = e.Source,
            }).ToList();
    }

    private static string Conclusion(Element e)
    {
        var parts = new List<string>();
        if (e.Replace) parts.Add("Замена");
        else if (e.RepairHours > 0) parts.Add($"Ремонт {e.RepairHours.ToString("0.0", CultureInfo.GetCultureInfo("ru-RU"))} н/ч");
        if (e.Paint is not null && !e.Replace) parts.Add(e.Paint);
        else if (e.Paint is not null) parts.Add(e.Paint);
        var s = string.Join("/", parts);
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private Element? Get(Dictionary<string, Element> map, string audatex, double conf, SourceRef src, ref int order)
    {
        var cleaned = TextNorm.CollapseSpaces(Regex.Replace(audatex, @"\s-\s.*$|ВКЛ:.*$|НЕ ВКЛ.*$|\(.*$", " "));
        if (cleaned.Count(char.IsLetter) < 3) return null;
        var name = _names.Expand(cleaned);
        var key = TextNorm.Key(name);
        var similar = map.Values.FirstOrDefault(e => TextNorm.Levenshtein(e.Raw, cleaned) <= Math.Max(2, cleaned.Length / 5));
        if (similar is not null && !map.ContainsKey(key)) key = TextNorm.Key(similar.Name);
        if (!map.TryGetValue(key, out var el))
            map[key] = el = new Element { Name = name, Raw = cleaned, Confidence = conf, Source = src, Order = order++ };
        else el.Confidence = Math.Max(el.Confidence, conf);
        return el;
    }

    /// <summary>Название в строке: самая длинная последовательность «слов» из заглавных букв.</summary>
    private static string? ExtractName(TextLine row)
    {
        var words = row.Words.Select(w => w.Text).ToList();
        int bestStart = -1, bestLen = 0;
        for (int i = 0; i < words.Count; i++)
        {
            int j = i;
            while (j < words.Count && IsNameWord(words[j])) j++;
            var len = words.Skip(i).Take(j - i).Sum(w => w.Count(char.IsLetter));
            if (len > bestLen) { bestLen = len; bestStart = i; }
            if (j > i) i = j - 1;
        }
        if (bestStart < 0 || bestLen < 3) return null;
        var take = words.Skip(bestStart).TakeWhile(IsNameWord).ToList();
        return string.Join(" ", take);
    }

    private static bool IsNameWord(string w)
    {
        if (w is "-" or "–" or "/" or "С/У" or "С-У") return true;
        var letters = w.Count(c => char.IsLetter(c));
        if (letters == 0) return false;
        var cyr = w.Count(c => c is >= 'А' and <= 'Я' or 'Ё' or >= 'а' and <= 'я');
        return cyr >= letters * 0.6 && w.Count(char.IsUpper) >= letters * 0.6 || w.StartsWith('(') || w.EndsWith(')') || w.Contains(':');
    }

    /// <summary>Норматив, н/ч: число вида 0.4166 / 1,5 в строке работ.</summary>
    private static double Hours(string row)
    {
        foreach (Match m in HoursRx().Matches(TextNorm.FixDigits(row)))
        {
            var v = double.Parse(m.Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            if (v is > 0 and < 50) return Math.Round(v, 1);
        }
        return 0;
    }

    /// <summary>Перечень повреждений из акта осмотра (печатный бланк «Наименование поврежденных деталей…»).</summary>
    public List<DamageRow> FromInspectionAct(CaseDocument doc)
    {
        var result = new List<DamageRow>();
        foreach (var page in doc.Text!.Pages)
        {
            var header = page.Lines.FirstOrDefault(l => TextNorm.Key(l.Text) is var k
                && (k.Contains("наименование поврежд") || k.Contains("при осмотре обнаружены следующие повреждения")));
            if (header is null) continue;
            var h = header.Box.Height;
            var rows = page.Lines
                .Where(l => l.Box.Y > header.Box.Bottom - 1 && l.Box.X < header.Box.X + header.Box.Width * 0.8 && l.Box.X > header.Box.X - h * 4)
                .OrderBy(l => l.Box.Y).ToList();
            foreach (var row in rows)
            {
                var key = TextNorm.Key(row.Text);
                if (key.StartsWith("акт осмотра") || key.StartsWith("эксперт") || key.StartsWith("участник") || key.StartsWith("итого") || key.StartsWith("подпис"))
                    break;
                if (key.Contains("наименование") || row.Text.Count(char.IsLetter) < 3) continue;
                var upperShare = row.Text.Count(char.IsUpper) / (double)Math.Max(1, row.Text.Count(char.IsLetter));
                if (upperShare < 0.6) continue;
                if (!_names.HasKnownNoun(row.Text)) continue;
                var name = _names.Expand(row.Text);
                // Справа на той же высоте — вид/характер повреждения.
                var right = page.Lines.Where(l => l.Box.X > row.Box.Right && Math.Abs(l.Box.CenterY - row.Box.CenterY) < h * 0.6)
                    .OrderBy(l => l.Box.X).Select(l => l.Text).ToList();
                var rightText = string.Join(" ", right);
                result.Add(new DamageRow
                {
                    Element = name,
                    Character = CharacterRx().IsMatch(rightText) ? TextNorm.Capitalize(rightText) : "",
                    Conclusion = ConclusionFromText(rightText),
                    RawText = row.Text,
                    Confidence = Math.Round(LabelValueParser.BaseConfidence(page, row) * 0.8, 2),
                    Source = new SourceRef(doc.FilePath, page.PageNumber, row.Box, page.Origin, row.Text),
                });
            }
        }
        return result;
    }

    private static string ConclusionFromText(string s)
    {
        var u = s.ToUpperInvariant();
        if (u.Contains("ЗАМЕН")) return "Замена";
        var parts = new List<string>();
        if (u.Contains("РЕМОНТ")) parts.Add("Ремонт");
        if (u.Contains("ОКРАС")) parts.Add(parts.Count == 0 ? "Окраска" : "окраска");
        return string.Join("/", parts);
    }

    [GeneratedRegex(@"(РЕМ\.?\s*ОКРАСКА|РЕМОНТНАЯ ОКРАСКА|ПОВЕРХ\w*\.?\s*ОКРАСКА|ОКРАСКА|ОКРАСИТЬ)\S*")]
    private static partial Regex PaintRx();

    [GeneratedRegex(@"\(([^)]+)\)?")]
    private static partial Regex ParenRx();

    [GeneratedRegex(@"\bРЕМОНТ\w*|\s-\s*РЕМ\w*")]
    private static partial Regex RepairRx();

    [GeneratedRegex(@"\s-\s*(ЗАМЕН\w*|З)$|^ЗАМЕН\w*")]
    private static partial Regex ReplaceRx();

    [GeneratedRegex(@"\s-\s*(СМ|С/У|С-У|СУ|СГУ|СРУ|СНЯТ\w*)|\bС/У\b|\bС-У\b|СНЯТЬ|УСТАН|НАДБАВК|ПОДГОТОВК|РАЗБОРК")]
    private static partial Regex AuxRx();

    [GeneratedRegex(@"(?<![\d.,])\d{1,2}[.,]\d{1,4}(?![\d.,])")]
    private static partial Regex HoursRx();

    [GeneratedRegex(@"деформ|разрушен|излом|задир|царапин|скол|вмятин|трещин|нарушени|разрыв|залом|срез|отрыв", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterRx();
}
