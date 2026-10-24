using System.Text.Json;

namespace AutoParser.Core.Text;

/// <summary>Кэш результатов OCR на диске: OCR медленный, а документы заявки открываются многократно.</summary>
public sealed class TextCache
{
    private const int Version = 2;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private readonly string _dir;

    public TextCache(string? dir = null)
    {
        _dir = dir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AutoParser", "ocr-cache");
        Directory.CreateDirectory(_dir);
    }

    public PageText? TryGet(string fileHash, int page, string engine)
    {
        var path = PathFor(fileHash, page, engine);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<CachedPage>(File.ReadAllText(path), Json)?.ToPage(); }
        catch { return null; }
    }

    public void Put(string fileHash, PageText page, string engine)
    {
        try { File.WriteAllText(PathFor(fileHash, page.PageNumber, engine), JsonSerializer.Serialize(CachedPage.From(page), Json)); }
        catch (IOException) { }
    }

    public void Clear()
    {
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json")) File.Delete(f);
    }

    private string PathFor(string hash, int page, string engine) =>
        Path.Combine(_dir, $"{hash[..32]}_{page}_{Sanitize(engine)}_v{Version}.json");

    private static string Sanitize(string s) => new(s.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());

    private sealed record CachedWord(string T, double X, double Y, double W, double H, double C);
    private sealed record CachedLine(List<CachedWord> Words);

    private sealed record CachedPage(int N, double W, double H, int R, string? E, List<CachedLine> L)
    {
        public static CachedPage From(PageText p) => new(p.PageNumber, p.Width, p.Height, p.Rotation, p.OcrEngine,
            p.Lines.Select(l => new CachedLine(l.Words.Select(w =>
                new CachedWord(w.Text, w.Box.X, w.Box.Y, w.Box.Width, w.Box.Height, w.Confidence)).ToList())).ToList());

        public PageText ToPage() => new()
        {
            PageNumber = N, Width = W, Height = H, Rotation = R, OcrEngine = E, Origin = TextOrigin.Ocr,
            Lines = L.Select(l => TextLine.FromWords(
                l.Words.Select(w => new TextWord(w.T, new BBox(w.X, w.Y, w.W, w.H), w.C)).ToList())).ToList(),
        };
    }
}
