using AutoParser.Core.Model;
using AutoParser.Core.Parsing;
using Xunit.Abstractions;

namespace AutoParser.Tests;

/// <summary>Разбор отдельных заключений страховщиков (текстовый слой).</summary>
public class InsurerReportParsingTests(ITestOutputHelper output)
{
    private List<FieldCandidate> Parse(CaseDocument doc)
    {
        var c = new LabelValueParser().Parse(doc, new ParseContext { CaseFolder = "" }).ToList();
        foreach (var x in c) output.WriteLine($"{x.Key}: {x.Value} ({x.Confidence}) {x.Source.Snippet}");
        return c;
    }

    [OcrDataFact]
    public async Task EkcTotal_TableWithMultilineOwner()
    {
        var doc = await TestData.LoadAsync(TestData.File("Заявка№27742", "тотал.pdf"), DocumentType.InsurerReport);
        var c = Parse(doc);
        string V(FieldKey k) => c.First(x => x.Key == k).Value;
        Assert.Equal("DAEWOO NEXIA", V(FieldKey.MakeModel));
        Assert.Equal("XWB3L32EDBA265960", V(FieldKey.Vin));
        Assert.Equal("221049", V(FieldKey.Mileage));
        Assert.Equal("15.07.2026", V(FieldKey.AccidentDate));
        Assert.Equal("Е410ТУ36", V(FieldKey.Plate));
        Assert.Equal("2011", V(FieldKey.Year));
        Assert.Equal("Черный", V(FieldKey.Color));
        Assert.Equal("Резникова Галина Николаевна", V(FieldKey.Owner));
    }

    [OcrDataFact]
    public async Task RaneM_LabelDashValue()
    {
        var doc = await TestData.LoadAsync(TestData.File("Заявка№10878", "Экспертное заключение Страховщика*.pdf"), DocumentType.InsurerReport);
        var c = Parse(doc);
        string V(FieldKey k) => c.First(x => x.Key == k).Value;
        Assert.Equal("BMW Z4 SDRIVE 30I", V(FieldKey.MakeModel));
        Assert.Equal("Н222ОУ152", V(FieldKey.Plate));
        Assert.Equal("WBAHF31020WX16362", V(FieldKey.Vin));
        Assert.Equal("2021", V(FieldKey.Year));
        Assert.Equal("WBAHF31020WX16362", V(FieldKey.BodyNumber));
        Assert.Equal("Белый", V(FieldKey.Color));
        Assert.Equal("Михайлова Татьяна Владимировна", V(FieldKey.Owner));
        Assert.Equal("28.03.2024", V(FieldKey.AccidentDate));
        Assert.Equal("42157", V(FieldKey.Mileage));
    }
}
