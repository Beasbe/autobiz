using System.Runtime.Versioning;
using AutoParser.Core.Text;
using AutoParser.OcrLinux;

namespace AutoParser.Ocr;

/// <summary>Выбор локального OCR-движка под текущую ОС: Windows.Media.Ocr или Tesseract.</summary>
public static class LocalOcr
{
    public const string Windows = "windows";
    public const string Linux = "linux";
    public const string None = "none";

    /// <summary>
    /// <paramref name="requested"/>: null/"" — движок по умолчанию для платформы, "windows", "linux" или "none" (без OCR).
    /// Неизвестное значение — исключение, чтобы опечатка в ключе CLI не приводила к молчаливой потере OCR.
    /// </summary>
    public static IOcrEngine? Create(string? requested = null, string language = "rus")
    {
        var name = string.IsNullOrWhiteSpace(requested) ? DefaultName : requested.Trim().ToLowerInvariant();
        return name switch
        {
            None => null,
            Windows => CreateWindows(),
            Linux => new TesseractOcrEngine(language),
            _ => throw new ArgumentException($"Неизвестный OCR-движок «{requested}»: ожидается {Windows}, {Linux} или {None}.", nameof(requested)),
        };
    }

    public static string DefaultName => OperatingSystem.IsWindows() ? Windows : Linux;

    /// <summary>
    /// Windows-движок есть только в сборке под Windows (ProjectReference — по условию).
    /// В кроссплатформенной сборке возвращаем заглушку: запрос «windows» на Linux даёт понятную ошибку, а не TypeLoad.
    /// </summary>
    private static IOcrEngine CreateWindows() =>
#if WINDOWS
        new WindowsOcrEngine();
#else
        new UnavailableOcrEngine("Windows.Media.Ocr доступен только на Windows 10/11 (соберите CLI под win-x64).");
#endif
}

/// <summary>Движок, которого нет на текущей платформе: сообщает об этом через <see cref="IsAvailable"/>.</summary>
public sealed class UnavailableOcrEngine(string reason) : IOcrEngine
{
    public string Name => "unavailable";
    public bool IsAvailable => false;
    public string Reason => reason;

    public Task<PageText> RecognizeAsync(byte[] image, int pageNumber, OcrOptions? options = null, CancellationToken ct = default) =>
        throw new PlatformNotSupportedException(reason);
}
