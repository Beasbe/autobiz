namespace AutoParser.Core.Text;

/// <summary>
/// Общая для локальных OCR-движков логика страницы: перебор поворотов (0/90/270/180),
/// выбор самого «осмысленного» результата и возврат координат в пикселях исходного изображения.
/// Движку остаётся распознать один повёрнутый кадр.
/// </summary>
public static class OcrPageRunner
{
    /// <summary>Порог оценки, при котором поворот 0° считается достаточным.</summary>
    public const int NormalPageScore = 150;

    /// <summary>Порог оценки, при котором дальнейшие повороты не пробуются.</summary>
    public const int GoodEnoughScore = 400;

    /// <param name="recognize">Распознавание одного кадра: (options, image, rotation) → кандидат в координатах исходника.</param>
    public static async Task<PageText> RunAsync(string engineName, byte[] image, int pageNumber, OcrOptions? options,
        Func<OcrOptions, byte[], int, Task<OcrPageCandidate>> recognize, CancellationToken ct = default)
    {
        options ??= new OcrOptions();
        var (srcW, srcH) = ImagePreprocessor.GetSize(image);
        int[] rotations = options.DetectOrientation ? [0, 90, 270, 180] : [0];

        OcrPageCandidate? best = null;
        foreach (var rot in rotations)
        {
            ct.ThrowIfCancellationRequested();
            var c = await recognize(options, image, rot);
            if (best is null || c.Score > best.Score) best = c;
            // Нормальная страница с текстом — дальше не крутим.
            if (rot == 0 && c.Score >= NormalPageScore) break;
            if (best.Score >= GoodEnoughScore) break;
        }

        var b = best!;
        bool swap = b.Rotation is 90 or 270;
        return new PageText
        {
            PageNumber = pageNumber,
            Width = swap ? srcH : srcW,
            Height = swap ? srcW : srcH,
            Lines = b.Lines,
            Origin = TextOrigin.Ocr,
            OcrEngine = engineName,
            Rotation = b.Rotation,
        };
    }
}
