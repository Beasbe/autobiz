namespace AutoParser.Core.Text;

/// <summary>Собирает слова в строки по вертикальному перекрытию.</summary>
public static class LineBuilder
{
    /// <param name="columnGap">Разрыв строки, если промежуток между словами больше columnGap × высота (колонки таблиц).</param>
    public static List<TextLine> Build(IEnumerable<TextWord> words, double overlap = 0.5, double columnGap = 6)
    {
        var rows = new List<List<TextWord>>();
        foreach (var w in words.Where(w => !string.IsNullOrWhiteSpace(w.Text)).OrderBy(w => w.Box.CenterY))
        {
            List<TextWord>? target = null;
            for (int i = rows.Count - 1; i >= Math.Max(0, rows.Count - 3); i--)
            {
                var row = rows[i];
                var top = row.Min(x => x.Box.Y);
                var bottom = row.Max(x => x.Box.Bottom);
                var inter = Math.Min(bottom, w.Box.Bottom) - Math.Max(top, w.Box.Y);
                var minH = Math.Min(bottom - top, w.Box.Height);
                if (minH > 0 && inter / minH >= overlap) { target = row; break; }
            }
            if (target is null) rows.Add(target = new List<TextWord>());
            target.Add(w);
        }

        var lines = new List<TextLine>();
        foreach (var row in rows)
        {
            var sorted = row.OrderBy(w => w.Box.X).ToList();
            var segment = new List<TextWord> { sorted[0] };
            for (int i = 1; i < sorted.Count; i++)
            {
                var prev = sorted[i - 1];
                var gap = sorted[i].Box.X - prev.Box.Right;
                var h = Math.Max(prev.Box.Height, sorted[i].Box.Height);
                if (gap > h * columnGap)
                {
                    lines.Add(TextLine.FromWords(segment));
                    segment = new List<TextWord>();
                }
                segment.Add(sorted[i]);
            }
            lines.Add(TextLine.FromWords(segment));
        }
        return lines;
    }
}
