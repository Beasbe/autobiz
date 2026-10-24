using System.Security.Cryptography;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace AutoParser.Core.Text;

/// <summary>
/// «Сначала текстовый слой, OCR — только когда его нет».
/// PDF: PdfPig; страницы почти без букв рендерятся (PDFium) и уходят в OCR. Картинки — сразу в OCR.
/// </summary>
public sealed class DocumentTextService
{
    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff", ".webp"];

    /// <summary>Порог: если на странице меньше букв, считаем её сканом.</summary>
    public int MinLettersForTextLayer { get; init; } = 50;

    public int RenderDpi { get; init; } = 300;

    /// <summary>Чем меньше, тем быстрее OCR; 200–250 dpi обычно достаточно для сканов А4.</summary>
    public int OcrDpi { get; init; } = 300;

    /// <summary>
    /// Максимальная длинная сторона кадра для OCR. Скан А4: 220 dpi ≈ 1800 px, 300 dpi ≈ 2480 px;
    /// выше 2000 px Tesseract заметно медленнее, а точность почти не растёт.
    /// </summary>
    public int MaxOcrSide { get; init; } = 2000;

    private readonly IOcrEngine? _ocr;
    private readonly TextCache? _cache;

    /// <summary>Фактический dpi рендера: предпочтение движка (или <see cref="OcrDpi"/>), если не задано явно.</summary>
    public int EffectiveOcrDpi => _ocr?.PreferredDpi ?? OcrDpi;

    public DocumentTextService(IOcrEngine? ocr, TextCache? cache = null)
    {
        _ocr = ocr is { IsAvailable: true } ? ocr : null;
        _cache = cache;
    }

    public bool OcrEnabled => _ocr is not null;
    public string? OcrName => _ocr?.Name;

    public static string ComputeHash(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    /// <param name="pageFilter">Какие страницы распознавать OCR (null — все). Текстовый слой читается всегда.</param>
    public async Task<DocumentText> LoadAsync(string path, Func<int, bool>? pageFilter = null,
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        try
        {
            if (ext == ".pdf") return await LoadPdfAsync(path, pageFilter, progress, ct);
            if (ImageExtensions.Contains(ext)) return await LoadImageAsync(path, progress, ct);
            if (ext == ".docx") return LoadDocx(path);
            if (ext == ".txt") return Plain(path, File.ReadAllText(path));
            return new DocumentText { FilePath = path, Pages = [], Error = "Формат не поддерживается" };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new DocumentText { FilePath = path, Pages = [], Error = $"{ex.GetType().Name}: {ex.Message}" };
        }
    }

    /// <summary>Изображение страницы для превью: рендер PDF или сам файл картинки (с учётом поворота OCR).</summary>
    public byte[] GetPageImage(string path, PageText page)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        byte[] png;
        if (ext == ".pdf")
        {
            var dpi = page.Origin == TextOrigin.Ocr ? RenderDpi : 150;
            png = PdfRenderer.RenderPng(path, page.PageNumber, dpi);
        }
        else png = File.ReadAllBytes(path);

        using var bmp = ImagePreprocessor.Decode(png);
        using var rot = ImagePreprocessor.Rotate(bmp, page.Rotation);
        return ImagePreprocessor.EncodePng(rot);
    }

    private async Task<DocumentText> LoadPdfAsync(string path, Func<int, bool>? pageFilter,
        IProgress<string>? progress, CancellationToken ct)
    {
        List<PageText> pages;
        string? error = null;
        try { pages = PdfTextExtractor.Extract(path); }
        catch (Exception ex)
        {
            // PdfPig не смог — пробуем хотя бы отрендерить через PDFium.
            error = $"Текстовый слой не прочитан: {ex.Message}";
            var count = PdfRenderer.GetPageCount(path);
            pages = Enumerable.Range(1, count).Select(PageText.Empty).ToList();
        }

        if (_ocr is null) return new DocumentText { FilePath = path, Pages = pages, Error = error };

        string? hash = null;
        for (int i = 0; i < pages.Count; i++)
        {
            var p = pages[i];
            if (!NeedsOcr(p)) continue;
            if (pageFilter is not null && !pageFilter(p.PageNumber)) continue;
            ct.ThrowIfCancellationRequested();

            hash ??= ComputeHash(path);
            var cached = _cache?.TryGet(hash, p.PageNumber, _ocr.Name);
            if (cached is not null) { pages[i] = cached; continue; }

            progress?.Report($"OCR: {Path.GetFileName(path)}, стр. {p.PageNumber}/{pages.Count}");
            var png = PdfRenderer.RenderPng(path, p.PageNumber, EffectiveOcrDpi);
            png = ImagePreprocessor.LimitLongSide(png, MaxOcrSide);
            var ocr = await _ocr.RecognizeAsync(png, p.PageNumber, ct: ct);
            if (ocr.LetterCount > p.LetterCount)
            {
                pages[i] = ocr;
                _cache?.Put(hash, ocr, _ocr.Name);
            }
        }
        return new DocumentText { FilePath = path, Pages = pages, Error = error };
    }

    /// <summary>Скан: почти нет букв, либо страница — большая картинка с подписью в пару строк.</summary>
    public bool NeedsOcr(PageText p) =>
        p.LetterCount < MinLettersForTextLayer || (p.ImageCoverage > 0.4 && p.LetterCount < 400);

    private async Task<DocumentText> LoadImageAsync(string path, IProgress<string>? progress, CancellationToken ct)
    {
        if (_ocr is null)
            return new DocumentText { FilePath = path, Pages = [PageText.Empty(1)], Error = "OCR недоступен" };

        var hash = ComputeHash(path);
        var page = _cache?.TryGet(hash, 1, _ocr.Name);
        if (page is null)
        {
            progress?.Report($"OCR: {Path.GetFileName(path)}");
            page = await _ocr.RecognizeAsync(await File.ReadAllBytesAsync(path, ct), 1, ct: ct);
            _cache?.Put(hash, page, _ocr.Name);
        }
        return new DocumentText { FilePath = path, Pages = [page] };
    }

    private static DocumentText LoadDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return Plain(path, "");
        var lines = new List<string>();
        foreach (var el in body.Descendants().Where(e => e is W.Paragraph && e.Parent is not W.TableCell || e is W.TableRow))
        {
            if (el is W.TableRow row)
                lines.Add(string.Join(" | ", row.Elements<W.TableCell>().Select(c => c.InnerText.Trim())));
            else lines.Add(el.InnerText);
        }
        return Plain(path, string.Join("\n", lines));
    }

    private static DocumentText Plain(string path, string text)
    {
        double y = 0;
        var lines = text.Split('\n').Where(l => l.Trim().Length > 0)
            .Select(l => new TextLine(l.Trim(), new BBox(0, y += 12, 500, 10), [new TextWord(l.Trim(), new BBox(0, y, 500, 10))]))
            .ToList();
        return new DocumentText
        {
            FilePath = path,
            Pages = [new PageText { PageNumber = 1, Width = 595, Height = Math.Max(842, y + 20), Lines = lines, Origin = TextOrigin.TextLayer }],
        };
    }
}
