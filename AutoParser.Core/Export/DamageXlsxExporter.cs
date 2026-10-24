using AutoParser.Core.Model;
using ClosedXML.Excel;

namespace AutoParser.Core.Export;

/// <summary>
/// Таблица «Описание поврежденных элементов и видов ремонтных воздействий» в xlsx
/// (+ лист «Данные ТС» со всеми полями, уверенностью и источниками).
/// </summary>
public static class DamageXlsxExporter
{
    public static readonly string[] DamageHeaders =
    [
        "№", "Поврежденный элемент", "Характер повреждения", "Исследование ФО", "Исследование заявителя",
        "Наличие повреждений в представленных фотоматериалах",
        "Выводы об относимости или неотносимости к последствиям рассматриваемого ДТП в отношении каждого повреждения",
        "Вывод, вид ремонтного воздействия",
    ];

    public static string Export(CaseReport report, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var wb = new XLWorkbook();

        var ws = wb.AddWorksheet("Повреждения");
        ws.Cell(1, 1).Value = "Описание поврежденных элементов и видов ремонтных воздействий";
        ws.Range(1, 1, 1, DamageHeaders.Length).Merge().Style.Font.SetBold().Font.SetFontSize(13)
            .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
        ws.Cell(2, 1).Value = $"Заявка {report.Get(FieldKey.ClaimNumber)}, {report.Get(FieldKey.MakeModel)} {report.Get(FieldKey.Plate)}";
        ws.Range(2, 1, 2, DamageHeaders.Length).Merge().Style.Font.SetItalic();

        var extra = new[] { "Уверенность", "Источник", "Строка документа" };
        var headers = DamageHeaders.Concat(extra).ToArray();
        for (int c = 0; c < headers.Length; c++) ws.Cell(4, c + 1).Value = headers[c];
        var head = ws.Range(4, 1, 4, headers.Length);
        head.Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#DDE7F3"))
            .Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Center);
        ws.Range(4, DamageHeaders.Length + 1, 4, headers.Length).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#EEEEEE"));

        int r = 5;
        foreach (var d in report.Damages)
        {
            ws.Cell(r, 1).Value = d.No;
            ws.Cell(r, 2).Value = d.Element;
            ws.Cell(r, 3).Value = d.Character;
            ws.Cell(r, 4).Value = d.FoStudy;
            ws.Cell(r, 5).Value = d.ApplicantStudy;
            ws.Cell(r, 6).Value = d.PhotoPresence;
            ws.Cell(r, 7).Value = d.Relation;
            ws.Cell(r, 8).Value = d.Conclusion;
            ws.Cell(r, 9).Value = d.Confidence;
            ws.Cell(r, 9).Style.NumberFormat.Format = "0%";
            ws.Cell(r, 10).Value = d.Source?.ToString() ?? "";
            ws.Cell(r, 11).Value = d.RawText;
            if (d.Confidence < 0.6) ws.Cell(r, 9).Style.Fill.SetBackgroundColor(XLColor.FromHtml("#FCE4D6"));
            r++;
        }
        if (report.Damages.Count == 0)
        {
            ws.Cell(r, 2).Value = "Повреждения не извлечены — заполните вручную";
            ws.Cell(r, 2).Style.Font.SetItalic();
            r++;
        }

        var table = ws.Range(4, 1, Math.Max(5, r - 1), headers.Length);
        table.Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin).Border.SetInsideBorder(XLBorderStyleValues.Thin)
            .Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Top);
        double[] widths = [5, 34, 40, 16, 16, 16, 22, 26, 12, 30, 40];
        for (int c = 0; c < widths.Length; c++) ws.Column(c + 1).Width = widths[c];
        ws.SheetView.FreezeRows(4);

        var fs = wb.AddWorksheet("Данные ТС");
        string[] fh = ["Поле", "Значение", "Статус", "Уверенность", "Источник", "Примечание"];
        for (int c = 0; c < fh.Length; c++) fs.Cell(1, c + 1).Value = fh[c];
        fs.Range(1, 1, 1, fh.Length).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#DDE7F3"));
        int fr = 2;
        foreach (var k in FieldInfo.ReportFields)
        {
            var f = report.Fields.TryGetValue(k, out var v) ? v : new FieldValue { Key = k };
            fs.Cell(fr, 1).Value = f.Title;
            fs.Cell(fr, 2).Value = f.Value;
            fs.Cell(fr, 3).Value = StatusTitle(f.Status);
            fs.Cell(fr, 4).Value = f.Confidence;
            fs.Cell(fr, 4).Style.NumberFormat.Format = "0%";
            fs.Cell(fr, 5).Value = f.Source?.ToString() ?? "";
            fs.Cell(fr, 6).Value = f.Note ?? "";
            var color = f.Status switch
            {
                FieldStatus.Ok or FieldStatus.Manual => "#E2EFDA",
                FieldStatus.Derived => "#EDEDED",
                FieldStatus.Check => "#FFF2CC",
                _ => "#FCE4D6",
            };
            fs.Cell(fr, 3).Style.Fill.SetBackgroundColor(XLColor.FromHtml(color));
            fr++;
        }
        fs.Range(1, 1, fr - 1, fh.Length).Style.Border.SetOutsideBorder(XLBorderStyleValues.Thin)
            .Border.SetInsideBorder(XLBorderStyleValues.Thin).Alignment.SetWrapText().Alignment.SetVertical(XLAlignmentVerticalValues.Top);
        double[] fw = [36, 50, 14, 12, 40, 50];
        for (int c = 0; c < fw.Length; c++) fs.Column(c + 1).Width = fw[c];

        wb.SaveAs(path);
        return path;
    }

    public static string StatusTitle(FieldStatus s) => s switch
    {
        FieldStatus.Ok => "OK",
        FieldStatus.Check => "Проверить",
        FieldStatus.Conflict => "Расхождение",
        FieldStatus.Derived => "Вычислено",
        FieldStatus.Missing => "Нет данных",
        FieldStatus.Manual => "Вручную",
        _ => s.ToString(),
    };
}
