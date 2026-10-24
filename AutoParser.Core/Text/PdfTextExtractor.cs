using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace AutoParser.Core.Text;

/// <summary>Текстовый слой PDF через PdfPig.</summary>
public static class PdfTextExtractor
{
    public static List<PageText> Extract(string path)
    {
        using var doc = PdfDocument.Open(path);
        var result = new List<PageText>();
        for (int i = 1; i <= doc.NumberOfPages; i++)
        {
            try { result.Add(FromPage(doc.GetPage(i))); }
            catch { result.Add(PageText.Empty(i)); }
        }
        return result;
    }

    private static PageText FromPage(Page page)
    {
        var h = page.Height;
        var words = page.GetWords()
            .Select(w => new TextWord(
                w.Text,
                new BBox(w.BoundingBox.Left, h - w.BoundingBox.Top, w.BoundingBox.Width, w.BoundingBox.Height)))
            .ToList();
        double coverage = 0;
        try
        {
            var area = page.Width * page.Height;
            coverage = page.GetImages().Sum(i => Math.Abs(i.BoundingBox.Width * i.BoundingBox.Height)) / area;
        }
        catch { }
        return new PageText
        {
            ImageCoverage = Math.Min(1, coverage),
            PageNumber = page.Number,
            Width = page.Width,
            Height = h,
            Lines = LineBuilder.Build(words),
            Origin = TextOrigin.TextLayer,
        };
    }
}
