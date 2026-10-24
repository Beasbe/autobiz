using System.Text.Json;

namespace AutoParser.Core.Validate;

/// <summary>Справочник субъектов РФ: коды регионов ГРЗ, крупные города, варианты написания.</summary>
public sealed class RegionDirectory
{
    public sealed record Region(string Name, int[] Codes, string[] Cities, string[] Aliases);

    private static readonly Lazy<RegionDirectory> _default = new(Load);
    public static RegionDirectory Default => _default.Value;

    public IReadOnlyList<Region> Regions { get; }

    public RegionDirectory(IReadOnlyList<Region> regions) => Regions = regions;

    private static RegionDirectory Load()
    {
        using var s = typeof(RegionDirectory).Assembly.GetManifestResourceStream("AutoParser.Core.Config.regions.json")
            ?? throw new InvalidOperationException("regions.json не найден");
        var list = JsonSerializer.Deserialize<List<Region>>(s, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        return new RegionDirectory(list);
    }

    public Region? ByCode(int code) => Regions.FirstOrDefault(r => r.Codes.Contains(code));

    public Region? ByPlate(string plate) =>
        PlateNormalizer.RegionCode(plate) is int c ? ByCode(c) : null;

    /// <summary>Определяет субъект по адресу/месту ДТП: сначала явное название субъекта, затем город.</summary>
    public Region? ByPlace(string place)
    {
        var key = TextNorm.Key(place);
        foreach (var r in Regions)
            if (r.Aliases.Append(r.Name).Any(a => key.Contains(TextNorm.Key(a)))) return r;
        foreach (var r in Regions)
            if (r.Cities.Any(c => ContainsWord(key, TextNorm.Key(c)))) return r;
        return null;
    }

    /// <summary>Город из списка, встречающийся в тексте (для «г. Нижний Новгород»).</summary>
    public string? FindCity(string place)
    {
        var key = TextNorm.Key(place);
        foreach (var r in Regions)
            foreach (var c in r.Cities.OrderByDescending(c => c.Length))
                if (ContainsWord(key, TextNorm.Key(c))) return c == "Н. Новгород" ? "Нижний Новгород" : c;
        return null;
    }

    /// <summary>Последовательность слов с допуском падежных окончаний: «в Нижнем Новгороде», «г. Воронеже».</summary>
    private static bool ContainsWord(string text, string phrase)
    {
        var t = text.Split(' ');
        var p = phrase.Split(' ');
        for (int i = 0; i + p.Length <= t.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < p.Length && ok; j++) ok = StemMatch(t[i + j], p[j]);
            if (ok) return true;
        }
        return false;
    }

    private static bool StemMatch(string word, string pattern)
    {
        if (word == pattern) return true;
        var stem = pattern.Length > 4 ? pattern[..^2] : pattern;
        return word.StartsWith(stem, StringComparison.Ordinal) && word.Length - pattern.Length <= 3 && word.Length >= stem.Length;
    }
}
