using System.Text.RegularExpressions;
using AutoParser.Core.Model;
using AutoParser.Core.Text;

namespace AutoParser.Core.Cases;

/// <summary>Обходит папку заявки, считает SHA-256 и помечает дубликаты («тотал.pdf» / «тотал (1).pdf»).</summary>
public static partial class CaseFolderScanner
{
    public static readonly string[] Supported = [".pdf", ".docx", ".txt", .. DocumentTextService.ImageExtensions];

    public static List<CaseDocument> Scan(string folder)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith("~$"))
            // Короткие имена первыми: «тотал.pdf» — оригинал, «тотал (1).pdf» — копия.
            .OrderBy(f => Path.GetDirectoryName(f)!.Length)
            .ThenBy(f => Path.GetFileNameWithoutExtension(f).Length)
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var byHash = new Dictionary<string, CaseDocument>();
        var result = new List<CaseDocument>();
        foreach (var f in files)
        {
            var info = new FileInfo(f);
            var doc = new CaseDocument
            {
                FilePath = f,
                Hash = DocumentTextService.ComputeHash(f),
                Size = info.Length,
                RelativePath = Path.GetRelativePath(folder, f),
            };
            if (byHash.TryGetValue(doc.Hash, out var original))
            {
                doc.DuplicateOf = original;
                doc.SkipReason = $"Дубликат файла «{original.RelativePath}»";
            }
            else byHash[doc.Hash] = doc;

            if (doc.SkipReason is null && !Supported.Contains(info.Extension.ToLowerInvariant()))
                doc.SkipReason = $"Формат {info.Extension} не поддерживается";
            result.Add(doc);
        }
        return result.OrderBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Номер заявки из пути: «У-26-154015-3020-004» → «У-26-154015/3020-004»; «Заявка№10878 26-130531 НТЭ» → «У-26-130531».</summary>
    public static string? ClaimNumberFromPath(string path)
    {
        foreach (var part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Reverse())
        {
            var m = FullNumber().Match(part);
            if (m.Success) return $"У-{m.Groups[1].Value}-{m.Groups[2].Value}/{m.Groups[3].Value}-{m.Groups[4].Value}";
        }
        foreach (var part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Reverse())
        {
            var m = ShortNumber().Match(part);
            if (m.Success) return $"У-{m.Groups[1].Value}-{m.Groups[2].Value}";
        }
        return null;
    }

    /// <summary>Вид экспертизы по имени папки: НТЭ / ИРУ.</summary>
    public static string ExpertiseKind(string folder)
    {
        foreach (var part in folder.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Reverse())
        {
            if (Regex.IsMatch(part, @"\bИРУ\b")) return "ИРУ";
            if (Regex.IsMatch(part, @"\bНТЭ\b")) return "НТЭ";
        }
        return "НТЭ";
    }

    [GeneratedRegex(@"[УY]-(\d{2})-(\d{6})[-/_ ](\d{4})-(\d{3})")]
    private static partial Regex FullNumber();

    [GeneratedRegex(@"(?:[УY]-)?(\d{2})-(\d{6})")]
    private static partial Regex ShortNumber();
}
