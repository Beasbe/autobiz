using AutoParser.Core.Cases;
using AutoParser.Core.Classify;
using AutoParser.Core.Merge;
using AutoParser.Core.Model;
using AutoParser.Core.Parsing;
using AutoParser.Core.Text;

namespace AutoParser.Core.Pipeline;

public sealed record CaseProcessorOptions
{
    /// <summary>Максимум страниц одного документа для OCR (фотоальбомы и т.п. не нужны целиком).</summary>
    public int MaxOcrPages { get; init; } = 40;

    /// <summary>Сколько первых страниц распознавать для определения типа скана.</summary>
    public int ClassifyPages { get; init; } = 2;
}

/// <summary>
/// Полный конвейер обработки папки заявки:
/// сканирование и дедупликация → текстовый слой → классификация → OCR нужных страниц → парсеры → слияние полей → таблица повреждений.
/// </summary>
public sealed class CaseProcessor
{
    private readonly DocumentTextService _text;
    private readonly IReadOnlyList<IDocumentParser> _parsers;
    private readonly DamageExtractor _damages;
    private readonly CaseProcessorOptions _options;

    public CaseProcessor(DocumentTextService text, IEnumerable<IDocumentParser>? parsers = null,
        DamageExtractor? damages = null, CaseProcessorOptions? options = null)
    {
        _text = text;
        _parsers = parsers?.ToList() ?? [new LabelValueParser(), new ContextRegexParser()];
        _damages = damages ?? new DamageExtractor();
        _options = options ?? new CaseProcessorOptions();
    }

    public async Task<CaseReport> ProcessAsync(string folder, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var report = new CaseReport { Folder = folder, ExpertiseKind = CaseFolderScanner.ExpertiseKind(folder) };
        void Log(string s) { report.Log.Add(s); progress?.Report(s); }

        Log($"Сканирование папки {folder}");
        report.Documents.AddRange(CaseFolderScanner.Scan(folder));
        if (!_text.OcrEnabled) Log("OCR недоступен: сканы и фото будут пропущены");

        int n = 0;
        foreach (var doc in report.Documents)
        {
            n++;
            ct.ThrowIfCancellationRequested();
            if (doc.DuplicateOf is not null) { doc.Type = doc.DuplicateOf.Type; doc.TypeConfidence = doc.DuplicateOf.TypeConfidence; }
            if (doc.SkipReason is not null) { Log($"[{n}/{report.Documents.Count}] {doc.RelativePath}: пропущен — {doc.SkipReason}"); continue; }
            await LoadAndClassifyAsync(doc, progress, ct);
            Log($"[{n}/{report.Documents.Count}] {doc.RelativePath}: {DocumentTypeInfo.Title(doc.Type)}, стр. {doc.PageCount}" +
                (doc.OcrPages > 0 ? $", OCR {doc.OcrPages}" : "") + (doc.Error is null ? "" : $" — {doc.Error}"));
        }

        Log("Разбор полей");
        var ctx = new ParseContext { CaseFolder = folder };
        var candidates = new List<FieldCandidate>();
        foreach (var doc in report.Documents.Where(d => d.SkipReason is null && d.Text is not null))
            foreach (var parser in _parsers.Where(p => p.CanParse(doc)))
            {
                try { candidates.AddRange(parser.Parse(doc, ctx)); }
                catch (Exception ex) { Log($"Ошибка {parser.Name} в {doc.FileName}: {ex.Message}"); }
            }

        var claim = CaseFolderScanner.ClaimNumberFromPath(folder);
        if (claim is not null)
            candidates.Add(new FieldCandidate(FieldKey.ClaimNumber, claim, claim.Contains('/') ? 0.9 : 0.5,
                SourceRef.Derived("Имя папки заявки"), DocumentType.FuLetter, "Folder",
                claim.Contains('/') ? null : "Из имени папки (без номера договора)"));

        report.Fields.Clear();
        foreach (var (k, v) in FieldMerger.Merge(candidates)) report.Fields[k] = v;
        report.Log.AddRange(ctx.Log);

        Log("Таблица повреждений");
        report.Damages = _damages.Extract(report.Documents);
        Log($"Готово: найдено полей {FieldInfo.ReportFields.Count(k => report.Fields[k].Status != FieldStatus.Missing)}/{FieldInfo.ReportFields.Count}, повреждений {report.Damages.Count}");
        return report;
    }

    private async Task LoadAndClassifyAsync(CaseDocument doc, IProgress<string>? progress, CancellationToken ct)
    {
        var (preType, _) = DocumentClassifier.ByFileName(doc.FilePath);
        if (preType is DocumentType.Photos or DocumentType.ExpertOutput)
        {
            doc.Type = preType;
            doc.TypeConfidence = 0.9;
            doc.SkipReason = preType == DocumentType.Photos ? "Фотоматериалы — текст не извлекается" : "Готовое заключение эксперта — не источник данных";
            return;
        }

        // 1. Только текстовый слой (быстро).
        var text = await _text.LoadAsync(doc.FilePath, _ => false, progress, ct);
        bool needsOcr = _text.OcrEnabled && (text.Pages.Any(_text.NeedsOcr) || DocumentTextService.ImageExtensions.Contains(Path.GetExtension(doc.FilePath).ToLowerInvariant()));

        // 2. Скан: распознаём первые страницы, чтобы понять тип.
        if (needsOcr && Path.GetExtension(doc.FilePath).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var (t0, _) = DocumentClassifier.Classify(doc.FilePath, text);
            if (DocumentClassifier.WorthFullOcr(t0) || t0 == DocumentType.Unknown)
            {
                text = await _text.LoadAsync(doc.FilePath, p => p <= _options.ClassifyPages, progress, ct);
                var (t1, _) = DocumentClassifier.Classify(doc.FilePath, text);
                if (DocumentClassifier.WorthFullOcr(t1) && text.Pages.Count > _options.ClassifyPages)
                    text = await _text.LoadAsync(doc.FilePath, p => p <= _options.MaxOcrPages, progress, ct);
            }
        }
        else if (needsOcr)
            text = await _text.LoadAsync(doc.FilePath, null, progress, ct);

        doc.Text = text;
        doc.Error = text.Error;
        var (type, conf) = DocumentClassifier.Classify(doc.FilePath, text);
        doc.Type = type;
        doc.TypeConfidence = conf;
    }
}
