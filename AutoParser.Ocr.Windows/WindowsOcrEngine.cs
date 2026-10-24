using System.Runtime.Versioning;
using AutoParser.Core.Text;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using WinOcr = Windows.Media.Ocr.OcrEngine;

namespace AutoParser.Ocr.Windows;

/// <summary>
/// Локальный OCR, встроенный в Windows 10/11 (Windows.Media.Ocr). Работает офлайн и на ARM64.
/// Нужен языковой пакет «Русский» с компонентом распознавания текста.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsOcrEngine : IOcrEngine
{
    public string Name => "Windows.Media.Ocr";

    /// <summary>Движок существует только в Windows: на других ОС не регистрируется и не запускается.</summary>
    public static bool IsSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    public bool IsAvailable
    {
        get
        {
            if (!IsSupported) return false;
            try { return WinOcr.IsLanguageSupported(new Language("ru")); }
            catch { return false; }
        }
    }

    public static IReadOnlyList<string> InstalledLanguages =>
        IsSupported ? WinOcr.AvailableRecognizerLanguages.Select(l => l.LanguageTag).ToList() : [];

    public Task<PageText> RecognizeAsync(byte[] image, int pageNumber, OcrOptions? options = null, CancellationToken ct = default)
    {
        if (!IsSupported) throw new PlatformNotSupportedException("Windows OCR доступен только на Windows 10/11.");
        return OcrPageRunner.RunAsync(Name, image, pageNumber, options, RecognizeRotatedAsync, ct);
    }

    private static async Task<OcrPageCandidate> RecognizeRotatedAsync(OcrOptions options, byte[] image, int rotation)
    {
        var engine = WinOcr.TryCreateFromLanguage(new Language(options.Language))
            ?? throw new InvalidOperationException(
                $"Windows OCR: язык «{options.Language}» не установлен. Параметры → Время и язык → Язык → Русский → Распознавание текста.");

        var (png, k) = ImagePreprocessor.Prepare(image, rotation);
        var result = await RunAsync(engine, png);

        // Перекос: Windows OCR сообщает угол наклона текста — выравниваем и распознаём ещё раз.
        if (result.TextAngle is double angle && Math.Abs(angle) > 0.7 && Math.Abs(angle) < 30)
        {
            var straight = ImagePreprocessor.Deskew(png, angle);
            var second = await RunAsync(engine, straight);
            if (Score(second) >= Score(result)) result = second;
        }

        var lines = new List<TextLine>();
        foreach (var line in result.Lines)
        {
            var words = line.Words
                .Select(w => new TextWord(
                    w.Text,
                    new BBox(w.BoundingRect.X / k, w.BoundingRect.Y / k, w.BoundingRect.Width / k, w.BoundingRect.Height / k),
                    OcrPageCandidate.WordConfidence(w.Text)))
                .ToList();
            if (words.Count > 0) lines.Add(TextLine.FromWords(words));
        }
        return new OcrPageCandidate(rotation, lines, Score(result));
    }

    private static async Task<OcrResult> RunAsync(WinOcr engine, byte[] png)
    {
        using var ms = new MemoryStream(png);
        using var ras = ms.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(ras);
        using var bmp = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        return await engine.RecognizeAsync(bmp);
    }

    /// <summary>Оценка «осмысленности» результата: суммарная длина слов из 3+ букв одного алфавита.</summary>
    private static int Score(OcrResult r) =>
        r.Lines.SelectMany(l => l.Words).Sum(w => OcrPageCandidate.LooksLikeWord(w.Text) ? w.Text.Length : 0);
}
