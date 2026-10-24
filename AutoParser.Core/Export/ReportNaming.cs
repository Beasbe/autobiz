using AutoParser.Core.Model;

namespace AutoParser.Core.Export;

public static class ReportNaming
{
    /// <summary>«У-26-130531_3020-005_Михайлова Т.В._НТЭ» — как называют готовые заключения.</summary>
    public static string BaseName(CaseReport report)
    {
        var claim = report.Get(FieldKey.ClaimNumber);
        var parts = new List<string> { claim.Length > 0 ? claim.Replace('/', '_') : "Заявка" };
        var owner = ShortName(report.Get(FieldKey.Owner));
        if (owner.Length > 0) parts.Add(owner);
        parts.Add(report.ExpertiseKind);
        return Sanitize(string.Join("_", parts));
    }

    /// <summary>«Михайлова Татьяна Владимировна» → «Михайлова Т.В.».</summary>
    public static string ShortName(string full)
    {
        var w = full.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (w.Length < 2) return full;
        return w[0] + " " + string.Concat(w.Skip(1).Take(2).Select(x => char.ToUpperInvariant(x[0]) + "."));
    }

    private static string Sanitize(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}

public static class TemplateLocator
{
    /// <summary>Шаблон заключения для вида экспертизы (НТЭ/ИРУ) из папки templates рядом с программой.</summary>
    public static string? Find(string kind)
    {
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "templates"), Path.Combine(Environment.CurrentDirectory, "templates") })
        {
            var p = Path.Combine(dir, kind + ".docx");
            if (File.Exists(p)) return p;
            var any = Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.docx").FirstOrDefault() : null;
            if (any is not null) return any;
        }
        return null;
    }
}
