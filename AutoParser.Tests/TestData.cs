using AutoParser.Core.Model;
using AutoParser.Core.Text;
using AutoParser.Ocr;

namespace AutoParser.Tests;

/// <summary>Доступ к папке data/ с реальными заявками (тесты пропускаются, если её нет).</summary>
public static class TestData
{
    public static string? Root { get; } = Find();

    private static string? Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var p = Path.Combine(dir.FullName, "AutoParser", "data");
            if (Directory.Exists(p)) return p;
            dir = dir.Parent;
        }
        return null;
    }

    public static string Case(string startsWith) =>
        Directory.EnumerateDirectories(Root!).First(d => Path.GetFileName(d).StartsWith(startsWith));

    public static string File(string caseStart, string fileName) =>
        Directory.EnumerateFiles(Case(caseStart), fileName, SearchOption.AllDirectories).First();

    /// <summary>
    /// OCR для тестов: AUTO_PARSER_TEST_OCR=windows|linux|none, иначе движок по умолчанию для ОС.
    /// AUTO_PARSER_TEST_OCR_LANG — язык Tesseract (по умолчанию rus).
    /// </summary>
    private static readonly Lazy<DocumentTextService> _service = new(() =>
        new DocumentTextService(
            LocalOcr.Create(Environment.GetEnvironmentVariable("AUTO_PARSER_TEST_OCR"),
                Environment.GetEnvironmentVariable("AUTO_PARSER_TEST_OCR_LANG") ?? "rus"),
            new TextCache()));
    public static DocumentTextService Service => _service.Value;

    /// <summary>Без OCR golden-тесты по сканам заведомо не совпадут — такие тесты пропускаются.</summary>
    public static bool OcrAvailable => Service.OcrEnabled;

    public static async Task<CaseDocument> LoadAsync(string path, DocumentType type)
    {
        var text = await Service.LoadAsync(path);
        return new CaseDocument { FilePath = path, Hash = "", Type = type, Text = text };
    }
}

/// <summary>[Fact], который пропускается без папки data/.</summary>
public sealed class DataFactAttribute : FactAttribute
{
    public DataFactAttribute()
    {
        if (TestData.Root is null) Skip = "Нет папки AutoParser/data с образцами заявок";
    }
}

/// <summary>[Fact] по сканам: пропускается без data/ или без доступного OCR.</summary>
public sealed class OcrDataFactAttribute : FactAttribute
{
    public OcrDataFactAttribute()
    {
        if (TestData.Root is null) Skip = "Нет папки AutoParser/data с образцами заявок";
        else if (!TestData.OcrAvailable) Skip = "OCR недоступен (Tesseract без русского или AUTO_PARSER_TEST_OCR)";
    }
}
