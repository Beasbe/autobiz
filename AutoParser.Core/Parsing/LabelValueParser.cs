using System.Text.Json;
using AutoParser.Core.Model;
using AutoParser.Core.Text;
using AutoParser.Core.Validate;

namespace AutoParser.Core.Parsing;

/// <summary>
/// Универсальный разбор «метка – значение» по словарю синонимов (Config/labels.json).
/// Значение ищется: в той же строке после метки → правее на той же высоте (колонка таблицы) → в строке ниже.
/// Работает и с текстовым слоем, и с OCR, потому что опирается на координаты, а не на порядок строк.
/// </summary>
public sealed class LabelValueParser : IDocumentParser
{
    public sealed record LabelDef(string Key, ValueType Type, string Label, string LabelKey, int WordCount);

    private sealed record FieldConfig(string Key, string Type, string[] Labels);
    private sealed record Config(FieldConfig[] Fields, string[] CulpritMarkers);

    private readonly List<LabelDef> _labels;
    private readonly List<string> _culpritMarkers;

    public string Name => "LabelValue";

    private static readonly Lazy<LabelValueParser> _default = new(() => new LabelValueParser());
    public static LabelValueParser Default => _default.Value;

    public LabelValueParser(string? configJson = null)
    {
        configJson ??= ReadEmbedded("labels.json");
        var cfg = JsonSerializer.Deserialize<Config>(configJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        _labels = cfg.Fields
            .SelectMany(f => f.Labels.Select(l =>
            {
                var key = TextNorm.Key(l);
                return new LabelDef(f.Key, Enum.Parse<ValueType>(f.Type), l, key, key.Split(' ').Length);
            }))
            .OrderByDescending(l => l.LabelKey.Length)
            .ToList();
        _culpritMarkers = cfg.CulpritMarkers.Select(TextNorm.Key).ToList();
    }

    internal static string ReadEmbedded(string name)
    {
        using var s = typeof(LabelValueParser).Assembly.GetManifestResourceStream($"AutoParser.Core.Config.{name}")
            ?? throw new InvalidOperationException($"{name} не найден");
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    public bool CanParse(CaseDocument doc) => doc.Text is not null && DocumentTypes.IsFieldSource(doc.Type);

    public IEnumerable<FieldCandidate> Parse(CaseDocument doc, ParseContext ctx)
    {
        var text = doc.Text!;
        var cutoff = FindCulpritCutoff(text);
        foreach (var page in text.Pages)
        {
            if (cutoff is { } c && page.PageNumber > c.Page) yield break;
            var lines = page.Lines;
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (cutoff is { } cc && page.PageNumber == cc.Page && line.Box.Y >= cc.Y) continue;

                var match = MatchLabel(line, page.Origin == TextOrigin.Ocr);
                if (match is null || match.Value.Def.Type == ValueType.Stop) continue;
                var (def, wordsUsed, fuzzy) = match.Value;
                if (!Enum.TryParse<FieldKey>(def.Key, out var key)) continue;

                foreach (var (raw, valueBox, how) in ValueCandidates(page, line, wordsUsed))
                {
                    var box = valueBox;
                    var parsed = ValueTypes.Parse(def.Type, raw, TextNorm.Key(line.Text));
                    if (parsed is null) continue;

                    var value = parsed.Value.Value;
                    // Многострочные значения: «РЕЗНИКОВА ГАЛИНА» / «НИКОЛАЕВНА».
                    if (def.Type is ValueType.Person or ValueType.Place && how != Position.SameLine)
                        value = AppendContinuation(page, box, def.Type, value, out box);

                    var conf = BaseConfidence(page, line) * parsed.Value.Factor * (fuzzy ? 0.9 : 1) * (how == Position.Below ? 0.92 : 1);
                    yield return new FieldCandidate(key, value, Math.Round(conf, 3),
                        new SourceRef(doc.FilePath, page.PageNumber, BBox.Union([line.Box, box]), page.Origin, $"{line.Text} → {raw}"),
                        doc.Type, Name, fuzzy ? $"Метка «{def.Label}» найдена нечётко" : null);
                    break;
                }
            }
        }
    }

    private enum Position { SameLine, Right, Below }

    /// <summary>Возможные места значения в порядке приоритета.</summary>
    private IEnumerable<(string Raw, BBox Box, Position How)> ValueCandidates(PageText page, TextLine line, int wordsUsed)
    {
        var rest = line.Words.Skip(wordsUsed).ToList();
        if (rest.Count > 0)
        {
            var restLine = TextLine.FromWords(rest);
            yield return (restLine.Text, restLine.Box, Position.SameLine);
        }

        var h = Math.Max(line.Box.Height, 1);
        // Правее на той же высоте (колонка значений таблицы).
        var right = page.Lines
            .Where(l => l != line && l.Box.X >= line.Box.Right - h * 0.5
                        && Math.Abs(l.Box.CenterY - line.Box.CenterY) < Math.Max(h, l.Box.Height) * 0.6
                        && !IsLabelLike(l, page.Origin == TextOrigin.Ocr))
            .OrderBy(l => l.Box.X)
            .ToList();
        if (right.Count > 0)
        {
            yield return (right[0].Text, right[0].Box, Position.Right);
            if (right.Count > 1) yield return (string.Join(" ", right.Select(r => r.Text)), BBox.Union(right.Select(r => r.Box)), Position.Right);
        }

        // Ниже метки (значение под подписью поля).
        var below = page.Lines
            .Where(l => l != line && l.Box.Y > line.Box.CenterY && l.Box.Y < line.Box.Bottom + h * 2.2
                        && l.Box.X < line.Box.Right && l.Box.Right > line.Box.X - h)
            .OrderBy(l => l.Box.Y)
            .FirstOrDefault();
        if (below is not null && !IsLabelLike(below, page.Origin == TextOrigin.Ocr))
            yield return (below.Text, below.Box, Position.Below);
    }

    private string AppendContinuation(PageText page, BBox valueBox, ValueType type, string value, out BBox box)
    {
        box = valueBox;
        var h = Math.Max(valueBox.Height, 1);
        for (int guard = 0; guard < 2; guard++)
        {
            var current = box;
            var next = page.Lines
                .Where(l => l.Box.Y > current.Bottom - h * 0.3 && l.Box.Y < current.Bottom + h * 1.4
                            && Math.Abs(l.Box.X - current.X) < h * 1.5)
                .OrderBy(l => l.Box.Y).FirstOrDefault();
            if (next is null || MatchLabel(next, page.Origin == TextOrigin.Ocr) is not null) break;
            // Слева от продолжения не должно быть новой метки (новая строка таблицы).
            bool newRow = page.Lines.Any(l => l.Box.Right < next.Box.X && Math.Abs(l.Box.CenterY - next.Box.CenterY) < h * 0.6);
            if (newRow) break;
            var joined = value + " " + next.Text;
            var parsed = ValueTypes.Parse(type, joined);
            if (parsed is null) break;
            value = parsed.Value.Value;
            box = BBox.Union([box, next.Box]);
        }
        return value;
    }

    /// <summary>Строка — метка или её начало («Государственный регистрационный» без «знак»).</summary>
    public bool IsLabelLike(TextLine line, bool ocr)
    {
        if (MatchLabel(line, ocr) is not null) return true;
        var k = TextNorm.Key(line.Text);
        return k.Length >= 10 && _labels.Any(l => l.LabelKey.StartsWith(k));
    }

    /// <summary>Сопоставляет начало строки с меткой. Для OCR допускаются опечатки (расстояние Левенштейна).</summary>
    public (LabelDef Def, int WordsUsed, bool Fuzzy)? MatchLabel(TextLine line, bool ocr)
    {
        var words = line.Words;
        if (words.Count == 0) return null;
        // Нумерация «4.1.», маркеры списков.
        int skip = 0;
        while (skip < words.Count && TextNorm.Key(words[skip].Text) is var k && (k.Length == 0 || k.All(char.IsDigit) && words[skip].Text.Contains('.')))
            skip++;

        var prefixKeys = new string[Math.Min(words.Count - skip, 12) + 1];
        for (int n = 1; n < prefixKeys.Length; n++)
            prefixKeys[n] = TextNorm.Key(string.Join(" ", words.Skip(skip).Take(n).Select(w => w.Text)));

        (LabelDef, int, bool)? best = null;
        foreach (var def in _labels)
        {
            for (int n = Math.Max(1, def.WordCount - 1); n <= Math.Min(def.WordCount + 1, prefixKeys.Length - 1); n++)
            {
                var pk = prefixKeys[n];
                if (pk == def.LabelKey) return (def, skip + n, false);
                if (!ocr || def.LabelKey.Length < 6) continue;
                var tol = Math.Max(1, def.LabelKey.Length / 7);
                if (Math.Abs(pk.Length - def.LabelKey.Length) <= tol && TextNorm.Levenshtein(pk, def.LabelKey) <= tol)
                    best ??= (def, skip + n, true);
            }
        }
        return best;
    }

    /// <summary>Начало раздела о виновнике / втором участнике: ниже него поля потерпевшего не ищем.</summary>
    public (int Page, double Y)? FindCulpritCutoff(DocumentText text)
    {
        foreach (var p in text.Pages)
            foreach (var l in p.Lines)
            {
                var k = TextNorm.Key(l.Text);
                if (_culpritMarkers.Any(m => k.StartsWith(m) || (m.Length > 15 && k.Contains(m))))
                    return (p.PageNumber, l.Box.Y);
            }
        return null;
    }

    internal static double BaseConfidence(PageText page, TextLine line) =>
        page.Origin == TextOrigin.Ocr ? Math.Clamp(0.45 + 0.35 * line.Confidence, 0.3, 0.8) : 0.95;
}
