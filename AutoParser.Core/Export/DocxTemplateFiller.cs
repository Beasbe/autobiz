using System.Text;
using System.Text.RegularExpressions;
using AutoParser.Core.Model;
using AutoParser.Core.Validate;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AutoParser.Core.Export;

/// <summary>
/// Заполняет шаблон заключения: таблицу «Данные ТС потерпевшего», таблицу повреждений,
/// а также подменяет образцовые значения шаблона (номер заявки, марку, ГРЗ, дату ДТП) по всему тексту.
/// Форматирование ячеек сохраняется (берутся свойства первого прогона).
/// </summary>
public static partial class DocxTemplateFiller
{
    /// <summary>Метки строк таблицы шаблона → поле. Сравнение по началу строки (TextNorm.Key).</summary>
    private static readonly (string Label, FieldKey Key)[] RowLabels =
    [
        ("Марка, модель ТС", FieldKey.MakeModel),
        ("Государственный регистрационный знак", FieldKey.Plate),
        ("Идентификационный номер (VIN)", FieldKey.Vin),
        ("Пробег", FieldKey.Mileage),
        ("Наименование (тип ТС)", FieldKey.VehicleType),
        ("Категория ТС", FieldKey.Category),
        ("Год выпуска", FieldKey.Year),
        ("Дата изготовления", FieldKey.ManufactureDate),
        ("Кузов №", FieldKey.BodyNumber),
        ("Мощность двигателя", FieldKey.Power),
        ("Тип коробки передач", FieldKey.Gearbox),
        ("Тип и цвет ЛКП", FieldKey.Paint),
        ("Цвет/ Тип ЛКП", FieldKey.Paint),
        ("Цвет / тип ЛКП", FieldKey.Paint),
        ("Дата начала эксплуатации", FieldKey.ManufactureDate),
        ("Регион расчета", FieldKey.RegionalMarket),
        ("Дата расчета", FieldKey.AccidentDate),
        ("СТС/ПТС", FieldKey.StsPts),
        ("Собственник", FieldKey.Owner),
        ("Дата ДТП", FieldKey.AccidentDate),
        ("Место ДТП", FieldKey.AccidentPlace),
        ("Региональный товарный рынок", FieldKey.RegionalMarket),
        ("Страховой полис потерпевшего", FieldKey.VictimPolicy),
    ];

    public static string Fill(string templatePath, CaseReport report, string outDir, string? fileName = null)
    {
        Directory.CreateDirectory(outDir);
        var output = Path.Combine(outDir, (fileName ?? ReportNaming.BaseName(report)) + ".docx");
        File.Copy(templatePath, output, overwrite: true);

        using var doc = WordprocessingDocument.Open(output, true);
        var body = doc.MainDocumentPart!.Document!.Body!;

        var replacements = new List<(string Old, string New)>();
        var vehicle = body.Descendants<Table>().FirstOrDefault(t => t.Elements<TableRow>().Any(r => RowStarts(r, "Идентификационный номер")));
        if (vehicle is not null)
        {
            foreach (var row in vehicle.Elements<TableRow>())
            {
                var cells = row.Elements<TableCell>().ToList();
                if (cells.Count < 2) continue;
                var label = TextNorm.Key(cells[0].InnerText);
                var map = RowLabels.FirstOrDefault(m => label.StartsWith(TextNorm.Key(m.Label)));
                if (map.Label is null) continue;
                var value = report.Get(map.Key);
                var old = cells[1].InnerText.Trim();
                // Не найдено — очищаем образцовое значение шаблона, чтобы оно не попало в отчёт.
                if (value.Length == 0) { SetCellText(cells[1], ""); continue; }

                // Только полное образцовое значение: одиночная марка («LADA») может относиться к ТС виновника.
                if (map.Key == FieldKey.MakeModel) replacements.Add((old, value));
                if (map.Key == FieldKey.Plate) replacements.Add((old, value));
                if (map.Key == FieldKey.AccidentDate && DateNormalizer.First(old) is { } oldDate)
                    replacements.Add((DateNormalizer.Format(oldDate), value.Replace(" г.", "")));
                SetCellText(cells[1], value);
            }
        }

        // Номер заявки в заголовке и подписке: «№ У-25-000/3020-004».
        var claim = report.Get(FieldKey.ClaimNumber);
        if (claim.Length > 0)
        {
            var sample = ClaimRx().Match(body.InnerText);
            if (sample.Success) replacements.Add((sample.Value, claim));
        }

        foreach (var (o, n) in replacements.Where(r => r.Old.Length >= 3 && r.Old != r.New).DistinctBy(r => r.Old))
            foreach (var p in body.Descendants<Paragraph>().ToList())
                if (!IsInside(p, vehicle)) ReplaceInParagraph(p, o, n);

        FillDamageTable(body, report.Damages);
        FillShortDamageTable(body, report.Damages);

        doc.MainDocumentPart!.Document.Save();
        return output;
    }

    private static bool RowStarts(TableRow r, string label) =>
        r.Elements<TableCell>().FirstOrDefault() is { } c && TextNorm.Key(c.InnerText).StartsWith(TextNorm.Key(label));

    private static bool IsInside(OpenXmlElement e, OpenXmlElement? container) =>
        container is not null && e.Ancestors().Contains(container);

    /// <summary>Таблица «№ | Поврежденный элемент | Характер | … | Вывод» (8 колонок).</summary>
    private static void FillDamageTable(Body body, List<DamageRow> damages)
    {
        var table = body.Descendants<Table>().FirstOrDefault(t =>
            t.Elements<TableRow>().FirstOrDefault() is { } h && TextNorm.Key(h.InnerText) is var k
            && k.Contains("поврежденный элемент") && k.Contains("вывод"));
        if (table is null) return;
        FillRows(table, damages, d => [d.No.ToString(), d.Element, d.Character, d.FoStudy, d.ApplicantStudy, d.PhotoPresence, d.Relation, d.Conclusion]);
    }

    /// <summary>Таблица «Повреждения рассматриваемого ТС» (№ п/п | Наименование элемента | Повреждение).</summary>
    private static void FillShortDamageTable(Body body, List<DamageRow> damages)
    {
        var table = body.Descendants<Table>().FirstOrDefault(t =>
            t.Elements<TableRow>().FirstOrDefault() is { } h && h.Elements<TableCell>().Count() == 3
            && TextNorm.Key(h.InnerText) is var k && k.Contains("наименование элемента") && k.Contains("повреждение"));
        if (table is null) return;
        FillRows(table, damages, d => [d.No.ToString(), d.Element, d.Character]);
    }

    private static void FillRows(Table table, List<DamageRow> damages, Func<DamageRow, string[]> cellsOf)
    {
        var rows = table.Elements<TableRow>().Skip(1).ToList();
        if (rows.Count == 0) return;
        var templateRow = rows.Last();
        for (int i = 0; i < damages.Count; i++)
        {
            TableRow row;
            if (i < rows.Count) row = rows[i];
            else
            {
                row = (TableRow)templateRow.CloneNode(true);
                table.AppendChild(row);
            }
            var cells = row.Elements<TableCell>().ToList();
            var values = cellsOf(damages[i]);
            for (int c = 0; c < cells.Count && c < values.Length; c++) SetCellText(cells[c], values[c]);
        }
        // Лишние строки шаблона: удаляем, если данные есть; иначе очищаем образцовые значения.
        for (int i = damages.Count; i < rows.Count; i++)
        {
            if (damages.Count > 0) rows[i].Remove();
            else
                foreach (var (cell, c) in rows[i].Elements<TableCell>().Select((x, c) => (x, c)))
                    SetCellText(cell, c == 0 ? (i + 1).ToString() : "");
        }
    }

    /// <summary>Заменяет текст ячейки, сохраняя свойства абзаца и первого прогона.</summary>
    public static void SetCellText(TableCell cell, string text)
    {
        var paragraphs = cell.Elements<Paragraph>().ToList();
        var p = paragraphs.FirstOrDefault() ?? cell.AppendChild(new Paragraph());
        foreach (var extra in paragraphs.Skip(1)) extra.Remove();

        var firstRun = p.Descendants<Run>().FirstOrDefault();
        var rPr = firstRun?.RunProperties?.CloneNode(true) as RunProperties;
        foreach (var child in p.ChildElements.Where(c => c is not ParagraphProperties).ToList()) child.Remove();

        var lines = text.Split('\n');
        var run = new Run();
        if (rPr is not null) run.AppendChild(rPr);
        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) run.AppendChild(new Break());
            run.AppendChild(new DocumentFormat.OpenXml.Wordprocessing.Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve });
        }
        p.AppendChild(run);
    }

    /// <summary>Замена подстроки в абзаце, даже если она разбита Word'ом на несколько прогонов.</summary>
    public static void ReplaceInParagraph(Paragraph p, string oldText, string newText)
    {
        var texts = p.Descendants<DocumentFormat.OpenXml.Wordprocessing.Text>().ToList();
        if (texts.Count == 0) return;
        var full = new StringBuilder();
        foreach (var t in texts) full.Append(t.Text);
        var s = full.ToString();
        var idx = s.IndexOf(oldText, StringComparison.Ordinal);
        int guard = 0;
        while (idx >= 0 && guard++ < 20)
        {
            // Позиция → (элемент, смещение).
            int pos = 0, startEl = -1, startOff = 0, endEl = -1, endOff = 0;
            for (int i = 0; i < texts.Count; i++)
            {
                var len = texts[i].Text.Length;
                if (startEl < 0 && idx < pos + len) { startEl = i; startOff = idx - pos; }
                if (startEl >= 0 && idx + oldText.Length <= pos + len) { endEl = i; endOff = idx + oldText.Length - pos; break; }
                pos += len;
            }
            if (startEl < 0 || endEl < 0) break;
            if (startEl == endEl)
            {
                var t = texts[startEl].Text;
                texts[startEl].Text = t[..startOff] + newText + t[endOff..];
            }
            else
            {
                texts[startEl].Text = texts[startEl].Text[..startOff] + newText;
                for (int i = startEl + 1; i < endEl; i++) texts[i].Text = "";
                texts[endEl].Text = texts[endEl].Text[endOff..];
            }
            foreach (var t in texts) t.Space = SpaceProcessingModeValues.Preserve;
            s = string.Concat(texts.Select(t => t.Text));
            idx = s.IndexOf(oldText, idx + newText.Length, StringComparison.Ordinal);
        }
    }

    [GeneratedRegex(@"У-\d{2}-\d{3,6}/\d{4}-\d{3}")]
    private static partial Regex ClaimRx();
}
