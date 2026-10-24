using System.Text.RegularExpressions;
using AutoParser.Core.Model;
using AutoParser.Core.Text;
using AutoParser.Core.Validate;

namespace AutoParser.Core.Parsing;

/// <summary>
/// Поиск значений регулярками по всему тексту с учётом контекста строки:
/// номер заявки ФУ, VIN, ГРЗ рядом с «г/н», полисы (потерпевшего / виновника), мощность, КПП, тип ЛКП,
/// «Серия – 9965 / Номер – 680398», дата и место ДТП из заявлений, страховщик.
/// </summary>
public sealed partial class ContextRegexParser : IDocumentParser
{
    public string Name => "Regex";

    public bool CanParse(CaseDocument doc) => doc.Text is not null && DocumentTypes.IsFieldSource(doc.Type);

    public IEnumerable<FieldCandidate> Parse(CaseDocument doc, ParseContext ctx)
    {
        var list = new List<FieldCandidate>();
        var cutoff = LabelValueParser.Default.FindCulpritCutoff(doc.Text!);
        foreach (var page in doc.Text!.Pages)
        {
            var lines = page.Lines.OrderBy(l => l.Box.Y).ThenBy(l => l.Box.X).ToList();
            bool prevCulprit = false;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var t = line.Text;
                var prev = string.Join(" ", lines.Skip(Math.Max(0, i - 3)).Take(Math.Min(3, i)).Select(l => l.Text));
                var conf = LabelValueParser.BaseConfidence(page, line);
                bool culpritLine = CulpritRx().IsMatch(t);
                bool afterCutoff = cutoff is { } cut && (page.PageNumber > cut.Page || page.PageNumber == cut.Page && line.Box.Y >= cut.Y);
                // Строка о виновнике и следующая за ней (номер часто переносится): ГРЗ/VIN/полис оттуда — не потерпевшего.
                // Контекст предложения: «Виновный … г/н …, страховая компания …, полис ХХХ …» может занимать несколько строк.
                if (culpritLine) prevCulprit = true;
                bool culprit = prevCulprit || afterCutoff;
                if (t.TrimEnd().EndsWith('.') && !culpritLine) prevCulprit = false;
                else if (t.TrimEnd().EndsWith('.')) prevCulprit = false;

                void Add(FieldKey k, string v, double c, string? note = null) =>
                    list.Add(new FieldCandidate(k, v, Math.Round(c, 3),
                        new SourceRef(doc.FilePath, page.PageNumber, line.Box, page.Origin, t), doc.Type, Name, note));

                if (culprit)
                {
                    foreach (var plate in PlateNormalizer.FindAll(t).Distinct())
                        list.Add(new FieldCandidate(FieldKey.Plate, plate, conf, new SourceRef(doc.FilePath, page.PageNumber, line.Box, page.Origin, t), doc.Type, Name, "Виновник", true));
                    foreach (var vin in VinValidator.FindAll(t).Distinct())
                        list.Add(new FieldCandidate(FieldKey.Vin, vin, conf, new SourceRef(doc.FilePath, page.PageNumber, line.Box, page.Origin, t), doc.Type, Name, "Виновник", true));
                    foreach (var pol in PolicyNormalizer.FindAll(t))
                        list.Add(new FieldCandidate(FieldKey.VictimPolicy, pol.ToString(), conf, new SourceRef(doc.FilePath, page.PageNumber, line.Box, page.Origin, t), doc.Type, Name, "Виновник", true));
                }

                // Номер заявки ФУ: «№ У-26-130531/3020-005».
                foreach (Match m in ClaimRx().Matches(t))
                    Add(FieldKey.ClaimNumber, $"У-{m.Groups[1].Value}-{m.Groups[2].Value}/{m.Groups[3].Value}-{m.Groups[4].Value}",
                        doc.Type == DocumentType.FuLetter ? conf : conf * 0.6);

                // Финансовая организация (страховщик потерпевшего).
                var fo = FinOrgRx().Match(t);
                if (fo.Success) Add(FieldKey.Insurer, NormalizeOrg(fo.Groups[1].Value), conf);
                if (doc.Type == DocumentType.InsurerReport)
                {
                    var cust = CustomerRx().Match(t);
                    if (cust.Success) Add(FieldKey.Insurer, NormalizeOrg(cust.Groups[1].Value), conf * 0.9);
                }

                // VIN в любом месте (кроме строк о виновнике).
                if (!culprit)
                    foreach (var vin in VinValidator.FindAll(t).Distinct())
                        Add(FieldKey.Vin, vin, conf * (VinContextRx().IsMatch(t + " " + prev) ? 0.95 : 0.7));

                // ГРЗ рядом с «г/н», «госномер», «регистрационный знак».
                if (!culprit && PlateContextRx().IsMatch(t))
                    foreach (var plate in PlateNormalizer.FindAll(t).Distinct())
                        Add(FieldKey.Plate, plate, conf * 0.85);

                // Полисы ОСАГО.
                foreach (var p in PolicyNormalizer.FindAll(t))
                {
                    var around = prev + " " + t;
                    if (culprit) continue;
                    double k = VictimRx().IsMatch(around) ? 0.95
                        : PvuRx().IsMatch(t) ? 0.85
                        : doc.Type == DocumentType.Policy && page.PageNumber == 1 ? 0.8
                        : 0.45;
                    Add(FieldKey.VictimPolicy, p.ToString(), conf * k);
                }

                // Мощность.
                var power = PowerParser.Parse(t);
                if (power is not null)
                    Add(FieldKey.Power, power.ToString(), conf * (power.Computed ? 0.75 : 0.9),
                        power.Computed ? "Пересчитано кВт↔л.с." : null);

                // КПП.
                var gear = GearboxDetector.Detect(t);
                if (gear is not null && !culprit) Add(FieldKey.Gearbox, gear, conf * 0.8);

                // Тип ЛКП.
                var paint = PaintTypeDetector.Detect(t);
                if (paint is not null && !culprit) Add(FieldKey.PaintType, paint, conf * 0.8);

                // «Серия - 9965» + «Номер - 680398» (РАНЭ-М), «серия: 9965 № 680398».
                var ser = SeriesRx().Match(t);
                if (ser.Success)
                {
                    var next = i + 1 < lines.Count ? lines[i + 1].Text : "";
                    var num = NumberRx().Match(t.Length > ser.Index + ser.Length ? t[(ser.Index + ser.Length)..] + " " + next : next);
                    if (num.Success && StsNormalizer.FromSeriesNumber(ser.Groups[1].Value, num.Groups[1].Value) is { } sts
                        && StsContextRx().IsMatch(prev + " " + t))
                        Add(FieldKey.Sts, sts, conf * 0.95);
                }
                if (StsContextRx().IsMatch(t) && !PassportRx().IsMatch(t))
                    foreach (var s in StsNormalizer.FindAll(t))
                        Add(FieldKey.Sts, s, conf * 0.85);

                // Дата ДТП в тексте заявлений: «15» Июля 2026г., произошло ДТП…; «ДТП от 28.03.2024».
                if (AccidentRx().IsMatch(t))
                {
                    var d = DateNormalizer.First(t);
                    if (d is not null) Add(FieldKey.AccidentDate, DateNormalizer.Format(d.Value), conf * 0.75);
                }

                // Место ДТП: «произошло ДТП по адресу: …», «ДТП, произошедшего … по адресу».
                var place = PlaceRx().Match(t);
                if (place.Success && (AccidentRx().IsMatch(prev + " " + t) || PlaceContextRx().IsMatch(prev + " " + t))
                    && !InspectionRx().IsMatch(prev + " " + t)
                    && ValueTypes.Parse(ValueType.Place, place.Groups[1].Value) is { } placeValue)
                    Add(FieldKey.AccidentPlace, placeValue.Value, conf * 0.7);
            }
        }
        return list;
    }

    internal static string NormalizeOrg(string s)
    {
        s = TextNorm.CollapseSpaces(s.Replace('"', '«').Replace('“', '«').Replace('”', '»'));
        s = Regex.Replace(s, @"[,.;]+$", "");
        // Закрываем кавычки: «АО «ГСК «Югория»» пишут как «АО «ГСК «Югория»».
        return s;
    }

    [GeneratedRegex(@"[УY]-(\d{2})-(\d{6})/(\d{4})-(\d{3})")]
    private static partial Regex ClaimRx();

    [GeneratedRegex(@"финансовой организаци\w+\s+(.+?)(?:\s+в целях|,|$)", RegexOptions.IgnoreCase)]
    private static partial Regex FinOrgRx();

    [GeneratedRegex(@"^Заказчик(?: экспертизы)?\s*[:\-–]\s*((?:АО|ПАО|САО|ООО|СПАО|АО СК)\s*[«""“][^,]+?)(?:,|$)", RegexOptions.IgnoreCase)]
    private static partial Regex CustomerRx();

    [GeneratedRegex(@"виновн|причинител|второго участника|виновник|вследствие нарушения|нарушени\w* (ПДД|Правил)", RegexOptions.IgnoreCase)]
    private static partial Regex CulpritRx();

    [GeneratedRegex(@"потерпевш|заявител|прямое возмещение|ПВУ", RegexOptions.IgnoreCase)]
    private static partial Regex VictimRx();

    [GeneratedRegex(@"прямо\w* возмещени\w* убытк", RegexOptions.IgnoreCase)]
    private static partial Regex PvuRx();

    [GeneratedRegex(@"VIN|ВИН|идентификац|кузов", RegexOptions.IgnoreCase)]
    private static partial Regex VinContextRx();

    [GeneratedRegex(@"г/н|г\.н\.|гос\.?\s*(номер|знак|рег)|регистрационн\w* (знак|номер)|ГРЗ|г\.р\.з", RegexOptions.IgnoreCase)]
    private static partial Regex PlateContextRx();

    [GeneratedRegex(@"Серия\s*[-–:]?\s*(\d{2}\s?[0-9А-ЯA-Z]{2})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesRx();

    [GeneratedRegex(@"(?:Номер|№)\s*[-–:]?\s*(\d{6})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex NumberRx();

    [GeneratedRegex(@"свидетельств\w* о регистрац|СТС|св-во о рег", RegexOptions.IgnoreCase)]
    private static partial Regex StsContextRx();

    [GeneratedRegex(@"паспорт\w* (гражданина|серия)|водительск", RegexOptions.IgnoreCase)]
    private static partial Regex PassportRx();

    [GeneratedRegex(@"произошл\w* (ДТП|дорожно)|ДТП от|дорожно-транспортно\w+ происшестви\w+ от|в результате ДТП|дата ДТП", RegexOptions.IgnoreCase)]
    private static partial Regex AccidentRx();

    [GeneratedRegex(@"происшестви\w*,? имевш\w* место|имевш\w* место|место совершения|происшествие произошло", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceContextRx();

    [GeneratedRegex(@"осмотр", RegexOptions.IgnoreCase)]
    private static partial Regex InspectionRx();

    [GeneratedRegex(@"(?:(?:по\s*)?[„""]?адресу\s*:|по\s*[„""]?адресу|место ДТП|на участке)\s*[:\-–]?\s*(.{6,150})", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceRx();
}
