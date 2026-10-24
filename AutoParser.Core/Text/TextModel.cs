namespace AutoParser.Core.Text;

/// <summary>Прямоугольник в координатах страницы: начало — левый верхний угол.</summary>
public readonly record struct BBox(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterY => Y + Height / 2;
    public double CenterX => X + Width / 2;

    public static BBox Union(IEnumerable<BBox> boxes)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var x in boxes)
        {
            l = Math.Min(l, x.X); t = Math.Min(t, x.Y);
            r = Math.Max(r, x.Right); b = Math.Max(b, x.Bottom);
        }
        return l == double.MaxValue ? default : new BBox(l, t, r - l, b - t);
    }

    public BBox Scale(double k) => new(X * k, Y * k, Width * k, Height * k);

    public override string ToString() => $"{X:0},{Y:0} {Width:0}x{Height:0}";
}

public sealed record TextWord(string Text, BBox Box, double Confidence = 1.0);

public sealed record TextLine(string Text, BBox Box, IReadOnlyList<TextWord> Words)
{
    public double Confidence => Words.Count == 0 ? 0 : Words.Average(w => w.Confidence);

    public static TextLine FromWords(IReadOnlyList<TextWord> words) =>
        new(string.Join(" ", words.Select(w => w.Text)), BBox.Union(words.Select(w => w.Box)), words);
}

public enum TextOrigin { TextLayer, Ocr, None }

public sealed class PageText
{
    public required int PageNumber { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required IReadOnlyList<TextLine> Lines { get; init; }
    public TextOrigin Origin { get; init; }
    public string? OcrEngine { get; init; }

    /// <summary>Доля площади страницы, занятая растровыми изображениями (для текстового слоя PDF).</summary>
    public double ImageCoverage { get; init; }

    /// <summary>Поворот (градусы по часовой), с которым страница была распознана.</summary>
    public int Rotation { get; init; }

    private string? _fullText;
    public string FullText => _fullText ??= string.Join("\n", Lines.Select(l => l.Text));

    public int LetterCount => FullText.Count(char.IsLetter);

    public static PageText Empty(int pageNumber) =>
        new() { PageNumber = pageNumber, Width = 0, Height = 0, Lines = [], Origin = TextOrigin.None };
}

public sealed class DocumentText
{
    public required string FilePath { get; init; }
    public required IReadOnlyList<PageText> Pages { get; init; }
    public string? Error { get; init; }

    public string FullText => string.Join("\n\f\n", Pages.Select(p => p.FullText));

    public IEnumerable<(PageText Page, TextLine Line)> AllLines =>
        Pages.SelectMany(p => p.Lines.Select(l => (p, l)));
}
