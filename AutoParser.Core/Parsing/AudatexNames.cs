using System.Text.Json;
using System.Text.RegularExpressions;
using AutoParser.Core.Validate;

namespace AutoParser.Core.Parsing;

/// <summary>
/// Разворачивает сокращённые названия Audatex в нормальные:
/// «НАКЛ Н ЗЕРКАЛО НАР Л» → «Накладка нижняя зеркала наружного левого», «ДВЕРЬ П Л» → «Дверь передняя левая».
/// Правило: первое существительное — в именительном падеже, следующие — в родительном;
/// прилагательные согласуются с ближайшим предшествующим существительным.
/// </summary>
public sealed partial class AudatexNames
{
    /// <param name="Central">У детали нет стороны (бампер, капот): «Л/ПР» после неё относятся к главному слову.</param>
    private sealed record Noun(string Nom, string Gen, char Gender, bool Central = false);
    private sealed record Config(Dictionary<string, string> Phrases, Dictionary<string, string[]> Nouns,
        Dictionary<string, string> Adjectives, Dictionary<string, string> Fixed, string[] Skip);

    private static readonly Lazy<AudatexNames> _default = new(() => new AudatexNames(LabelValueParser.ReadEmbedded("audatex.json")));
    public static AudatexNames Default => _default.Value;

    private readonly List<KeyValuePair<string, string>> _phrases;
    private readonly Dictionary<string, Noun> _nouns;
    private readonly Dictionary<string, string> _adjectives;
    private readonly Dictionary<string, string> _fixed;
    private readonly HashSet<string> _skip;

    public AudatexNames(string json)
    {
        var c = JsonSerializer.Deserialize<Config>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        _phrases = c.Phrases.OrderByDescending(p => p.Key.Length).ToList();
        _nouns = c.Nouns.ToDictionary(kv => kv.Key, kv => new Noun(kv.Value[0], kv.Value[1], kv.Value[2][0], kv.Value.Length > 3 && kv.Value[3] == "c"));
        _adjectives = c.Adjectives;
        _fixed = c.Fixed;
        _skip = [.. c.Skip];
    }

    public bool IsSkipWord(string word) => _skip.Contains(word.ToUpperInvariant());

    /// <summary>В названии есть известная деталь (отсекает мусор OCR и служебные строки).</summary>
    public bool HasKnownNoun(string audatex)
    {
        var s = " " + Normalize(audatex) + " ";
        foreach (var (from, to) in _phrases) s = s.Replace(" " + from.Replace('Ё', 'Е') + " ", " " + to + " ");
        return s.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(_nouns.ContainsKey);
    }

    private static string Normalize(string s)
    {
        s = TextNorm.CollapseSpaces(Regex.Replace(s.ToUpperInvariant().Replace('Ё', 'Е'), @"[^\w\-\s]", " "));
        // Частые ошибки OCR: «ЬОКОВАЯ» → «БОКОВАЯ», слитные «ПЛ» → «П Л».
        s = Regex.Replace(s, @"\bЬ", "Б");
        s = Regex.Replace(s, @"\b(П|З)(Л|ПР)\b", "$1 $2");
        return s;
    }

    public string Expand(string audatex)
    {
        var s = " " + Normalize(audatex) + " ";
        foreach (var (from, to) in _phrases)
            s = s.Replace(" " + from.Replace('Ё', 'Е') + " ", " " + to + " ");

        var parts = new List<string>();
        Noun? first = null;    // главное слово
        Noun? head = null;     // существительное, с которым согласуем прилагательные
        bool genitive = false; // head уже в родительном падеже
        int nounCount = 0;
        string? lastAdj = null;
        foreach (var tok in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (_fixed.TryGetValue(tok, out var fixedText)) { parts.Add(fixedText); continue; }
            if (_nouns.TryGetValue(tok, out var noun))
            {
                nounCount++;
                lastAdj = null;
                genitive = nounCount > 1;
                head = noun;
                first ??= noun;
                parts.Add(genitive ? noun.Gen : noun.Nom);
                continue;
            }
            if (_adjectives.TryGetValue(tok, out var adj))
            {
                // «ДВЕРЬ П П» — повтор признака (обычно ошибка OCR), второй не пишем.
                if (parts.Count > 0 && lastAdj == adj) continue;
                lastAdj = adj;
                // Сторона (левый/правый) у «центральной» детали относится к главному слову: «Молдинг бампера левый».
                bool side = adj is "левый" or "правый";
                if (side && head is { Central: true } && first is not null && first != head)
                    parts.Add(Decline(adj, first.Gender, false));
                else
                    parts.Add(head is null ? adj : Decline(adj, head.Gender, genitive));
                continue;
            }
            if (tok.Any(char.IsDigit) || tok.Length == 1) continue; // артикулы, «К3», одиночные неизвестные буквы
            parts.Add(tok.ToLowerInvariant());
        }
        // Прилагательные перед первым существительным («НИЖН ДВЕРЬ») — согласуем задним числом не будем: редкий случай.
        var text = string.Join(" ", parts);
        return text.Length == 0 ? audatex : char.ToUpperInvariant(text[0]) + text[1..];
    }

    /// <summary>Склонение прилагательного (полная форма м.р. им.п.) по роду и падежу.</summary>
    internal static string Decline(string adj, char gender, bool genitive)
    {
        bool soft = adj.EndsWith("ний");
        bool stressed = adj.EndsWith("ой");
        var stem = adj[..^2];
        if (soft)
            return (gender, genitive) switch
            {
                ('f', false) => stem + "яя", ('n', false) => stem + "ее", ('m', false) => adj,
                ('f', true) => stem + "ей", (_, true) => stem + "его",
                _ => adj,
            };
        return (gender, genitive) switch
        {
            ('f', false) => stem + "ая", ('n', false) => stem + "ое", ('m', false) => stressed ? adj : adj,
            ('f', true) => stem + "ой", (_, true) => stem + "ого",
            _ => adj,
        };
    }
}
