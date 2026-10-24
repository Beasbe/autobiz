using AutoParser.Core.Export;
using AutoParser.Core.Model;
using AutoParser.Core.Pipeline;
using DocumentFormat.OpenXml.Packaging;
using Xunit.Abstractions;

namespace AutoParser.Tests;

/// <summary>
/// «Золотые» тесты: полный прогон папки заявки и сравнение с эталоном.
/// Эталон по 10878 взят из готового заключения эксперта (У-26-130531_3020-005_Михайлова Т.В._НТЭ.docx).
/// Первый прогон медленный (OCR), дальше результаты берутся из кэша.
/// </summary>
public class GoldenCaseTests(ITestOutputHelper output)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Dictionary<string, CaseReport> Cache = [];

    private async Task<CaseReport> ProcessAsync(string caseStart)
    {
        await Gate.WaitAsync();
        try
        {
            if (!Cache.TryGetValue(caseStart, out var report))
                Cache[caseStart] = report = await new CaseProcessor(TestData.Service).ProcessAsync(TestData.Case(caseStart));
            foreach (var k in FieldInfo.ReportFields)
                output.WriteLine($"{report[k].Title}: {report[k].Value} [{report[k].Status} {report[k].Confidence:P0}] {report[k].Source}");
            return report;
        }
        finally { Gate.Release(); }
    }

    [OcrDataFact]
    public async Task Case10878_BmwZ4_MatchesExpertReport()
    {
        var r = await ProcessAsync("Заявка№10878");
        Assert.Equal("У-26-130531/3020-005", r.Get(FieldKey.ClaimNumber));
        Assert.StartsWith("BMW Z4", r.Get(FieldKey.MakeModel));
        Assert.Equal("Н222ОУ152", r.Get(FieldKey.Plate));
        Assert.Equal("WBAHF31020WX16362", r.Get(FieldKey.Vin));
        Assert.Equal("42157", r.Get(FieldKey.Mileage));
        Assert.Equal("Легковой купе", r.Get(FieldKey.VehicleType));
        Assert.Equal("B/М1", r.Get(FieldKey.Category));
        Assert.Equal("2021", r.Get(FieldKey.Year));
        Assert.Equal("WBAHF31020WX16362", r.Get(FieldKey.BodyNumber));
        Assert.Equal("190/258,3", r.Get(FieldKey.Power));
        Assert.Equal("АКПП", r.Get(FieldKey.Gearbox));
        Assert.Equal("Белый / Акриловая двухслойная", r.Get(FieldKey.Paint));
        Assert.StartsWith("99 65 № 680398", r.Get(FieldKey.StsPts));
        Assert.Equal("Михайлова Татьяна Владимировна", r.Get(FieldKey.Owner));
        Assert.Equal("28.03.2024 г.", r.Get(FieldKey.AccidentDate));
        Assert.Equal("г. Нижний Новгород", r.Get(FieldKey.AccidentPlace));
        Assert.Equal("Нижегородская область", r.Get(FieldKey.RegionalMarket));
        Assert.Equal("ХХХ №0390237711 АО «ГСК «Югория»", r.Get(FieldKey.VictimPolicy));

        // Калькуляция: замена накладки зеркала, окраска крыла (в Audatex — «боковая стенка»).
        Assert.Contains(r.Damages, d => d.Element.StartsWith("Накладка нижняя зеркала") && d.Conclusion == "Замена");
        Assert.Contains(r.Damages, d => d.Element == "Крыло переднее левое" && d.Conclusion.Contains("окраска", StringComparison.OrdinalIgnoreCase));
    }

    [OcrDataFact]
    public async Task Case27742_DaewooNexia()
    {
        var r = await ProcessAsync("Заявка№27742");
        Assert.Equal("У-26-154015/3020-004", r.Get(FieldKey.ClaimNumber));
        Assert.Equal("DAEWOO NEXIA", r.Get(FieldKey.MakeModel));
        Assert.Equal("Е410ТУ36", r.Get(FieldKey.Plate));
        Assert.Equal("XWB3L32EDBA265960", r.Get(FieldKey.Vin));
        Assert.Equal("221049", r.Get(FieldKey.Mileage));
        Assert.Equal("Легковой седан", r.Get(FieldKey.VehicleType));
        Assert.Equal("B/М1", r.Get(FieldKey.Category));
        Assert.Equal("2011", r.Get(FieldKey.Year));
        Assert.Equal("XWB3L32EDBA265960", r.Get(FieldKey.BodyNumber));
        Assert.Equal("58,8/80", r.Get(FieldKey.Power));
        Assert.Equal("МКПП", r.Get(FieldKey.Gearbox));
        Assert.StartsWith("Черный", r.Get(FieldKey.Paint));
        Assert.StartsWith("99 62 № 435851", r.Get(FieldKey.StsPts));
        Assert.Equal("Резникова Галина Николаевна", r.Get(FieldKey.Owner));
        Assert.Equal("15.07.2026 г.", r.Get(FieldKey.AccidentDate));
        Assert.Equal("Воронежская область", r.Get(FieldKey.RegionalMarket));
        Assert.Equal("ХХХ №0622257614 АО «МАКС»", r.Get(FieldKey.VictimPolicy));

        // Дубликаты «(1)» не обрабатываются повторно.
        Assert.All(r.Documents.Where(d => d.FileName.Contains("(1)")), d => Assert.NotNull(d.DuplicateOf));
        // Перечень повреждений из акта осмотра «МАКС» (подшит внутрь «док.pdf»).
        Assert.Contains(r.Damages, d => d.Element == "Капот");
        Assert.Contains(r.Damages, d => d.Element == "Крыло переднее левое");
    }

    [OcrDataFact]
    public async Task Export_FillsTemplateAndXlsx()
    {
        var r = await ProcessAsync("Заявка№10878");
        var outDir = Path.Combine(Path.GetTempPath(), "AutoParserTests", Guid.NewGuid().ToString("N"));
        try
        {
            var template = TemplateLocator.Find("НТЭ");
            Assert.NotNull(template);
            var docx = DocxTemplateFiller.Fill(template!, r, outDir);
            Assert.EndsWith("У-26-130531_3020-005_Михайлова Т.В._НТЭ.docx", docx);

            using (var doc = WordprocessingDocument.Open(docx, false))
            {
                var text = doc.MainDocumentPart!.Document!.Body!.InnerText;
                Assert.DoesNotContain("SUBARU", text);           // образцовые значения шаблона заменены
                Assert.DoesNotContain("Б225ББ111", text);
                Assert.DoesNotContain("У-25-000/3020-004", text);
                Assert.Contains("BMW Z4", text);
                Assert.Contains("Н222ОУ152", text);
                Assert.Contains("У-26-130531/3020-005", text);
                Assert.Contains("Нижегородская область", text);
                Assert.Contains("Крыло переднее левое", text);
            }

            var xlsx = DamageXlsxExporter.Export(r, Path.Combine(outDir, "damages.xlsx"));
            using var wb = new ClosedXML.Excel.XLWorkbook(xlsx);
            var ws = wb.Worksheet("Повреждения");
            Assert.Equal("Поврежденный элемент", ws.Cell(4, 2).GetString());
            Assert.Equal(r.Damages[0].Element, ws.Cell(5, 2).GetString());
            Assert.Equal("Н222ОУ152", wb.Worksheet("Данные ТС").Cell(4, 2).GetString());
        }
        finally
        {
            try { Directory.Delete(outDir, true); } catch { }
        }
    }
}
