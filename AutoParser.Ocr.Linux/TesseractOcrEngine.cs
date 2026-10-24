using System.Diagnostics;
using System.Globalization;
using System.Text;
using AutoParser.Core.Text;

namespace AutoParser.OcrLinux;

/// <summary>
/// Локальный OCR для Linux: внешний бинарь Tesseract (пакет <c>tesseract-ocr</c> + <c>tesseract-ocr-rus</c>).
/// Замена Windows.Media.Ocr: тот же интерфейс <see cref="IOcrEngine"/>.
/// Ориентация определяется одним дешёвым проходом OSD (psm 0), распознавание — одним проходом psm 3.
/// </summary>
/// <remarks>
/// Вызывается как процесс на каждый кадр, поэтому работает и в контейнере без прав <c>tesseract</c> в PATH
/// (путь можно задать в <c>TESSERACT_PATH</c>). Данные никуда не уходят.
/// </remarks>
public sealed class TesseractOcrEngine : IOcrEngine
{
    /// <summary>Ниже этой уверенности OSD считаем ненадёжным и сверяем с вертикалью (0°).</summary>
    private const double OsdConfidenceThreshold = 2.0;

    private readonly string _exe;

    /// <param name="language">Язык распознавания (пакет должен быть установлен: <c>tesseract --list-langs</c>).</param>
    /// <param name="exe">Путь к бинарю; по умолчанию <c>TESSERACT_PATH</c> или <c>tesseract</c> в PATH.</param>
    public TesseractOcrEngine(string language = "rus", string? exe = null)
    {
        Language = language;
        _exe = exe ?? Environment.GetEnvironmentVariable("TESSERACT_PATH") ?? "tesseract";
    }

    public string Language { get; }

    public string Name => $"Tesseract-{Language}";

    /// <summary>
    /// Tesseract быстро расходится с рендером мелкого текста: 300 dpi даёт вдвое больше пикселей
    /// при той же точности на сканах А4, поэтому рендерим PDF в 220 dpi.
    /// </summary>
    public int PreferredDpi => 220;

    public bool IsAvailable => IsSupported && InstalledLanguages.Count > 0 && InstalledLanguages.Contains(Language);

    private static bool IsSupported => OperatingSystem.IsLinux() || OperatingSystem.IsMacOS();

    /// <summary>Коды установленных языков (<c>tesseract --list-langs</c>).</summary>
    public IReadOnlyList<string> InstalledLanguages => _installed ??= ProbeLanguages();

    private IReadOnlyList<string>? _installed;

    private IReadOnlyList<string> ProbeLanguages()
    {
        try
        {
            var (code, stdout, _) = Run(["--list-langs"]);
            if (code != 0) return [];
            return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => !l.EndsWith(':') && !l.Contains(' '))
                .ToList();
        }
        catch { return []; }
    }

    public Task<PageText> RecognizeAsync(byte[] image, int pageNumber, OcrOptions? options = null, CancellationToken ct = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException(
                $"Tesseract: язык «{Language}» не установлен (нужен пакет tesseract-ocr-rus или задайте TESSERACT_PATH).");
        options ??= new OcrOptions();
        return Task.Run(() => Recognize(image, pageNumber, options), ct);
    }

    private PageText Recognize(byte[] image, int pageNumber, OcrOptions options)
    {
        var (srcW, srcH) = ImagePreprocessor.GetSize(image);

        int rotation = 0;
        if (options.DetectOrientation)
        {
            var (detected, confidence) = DetectOrientation(image);
            rotation = detected;

            // Низкая уверенность OSD (бывает на смазанных сканах): не доверяем слепо,
            // сравниваем распознавание в 0° и в найденном повороте по количеству текста.
            if (detected != 0 && confidence < OsdConfidenceThreshold)
            {
                var upright = RecognizeAt(image, 0);
                var rotated = RecognizeAt(image, detected);
                if (OcrPageCandidate.ScoreOf(upright) > OcrPageCandidate.ScoreOf(rotated))
                    return BuildPage(pageNumber, srcW, srcH, 0, upright);
                return BuildPage(pageNumber, srcW, srcH, detected, rotated);
            }
        }

        var lines = RecognizeAt(image, rotation);
        return BuildPage(pageNumber, srcW, srcH, rotation, lines);
    }

    private static PageText BuildPage(int pageNumber, int srcW, int srcH, int rotation, List<TextLine> lines)
    {
        bool swap = rotation is 90 or 270;
        return new PageText
        {
            PageNumber = pageNumber,
            Width = swap ? srcH : srcW,
            Height = swap ? srcW : srcH,
            Lines = lines,
            Origin = TextOrigin.Ocr,
            OcrEngine = "Tesseract",
            Rotation = rotation,
        };
    }

    private List<TextLine> RecognizeAt(byte[] image, int rotation)
    {
        var (png, k) = ImagePreprocessor.Prepare(image, rotation);
        return LookaheadLines(PngToTsv(png), k).ToList();
    }

    /// <summary>Определяет угол поворота по часовой (0/90/180/270) и уверенность через psm 0 (OSD).</summary>
    private (int Rotation, double Confidence) DetectOrientation(byte[] image)
    {
        try
        {
            var (code, stdout, _) = Run(["-", "stdout", "-l", "osd", "--psm", "0"], image);
            if (code != 0) return (0, 0);
            int rotation = 0;
            double confidence = 0;
            foreach (var line in stdout.Split('\n'))
            {
                if (line.StartsWith("Rotate:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = line.AsSpan(line.IndexOf(':') + 1).Trim();
                    if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var deg))
                        rotation = ((deg % 360) + 360) % 360;
                }
                else if (line.StartsWith("Orientation confidence:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = line.AsSpan(line.IndexOf(':') + 1).Trim();
                    double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out confidence);
                }
            }
            return (rotation, confidence);
        }
        catch { }
        return (0, 0);
    }

    /// <summary>
    /// Разбор TSV Tesseract (уровень 5 — слово): группировка по блок/абзац/строка,
    /// координаты делятся на масштаб, чтобы вернуть пиксели исходного изображения.
    /// </summary>
    public static List<TextLine> LookaheadLines(IReadOnlyList<TsvWord> words, double scale = 1)
    {
        var result = new List<TextLine>();
        foreach (var group in words.GroupBy(w => (w.Block, w.Paragraph, w.Line)).OrderBy(g => g.Min(w => w.Top)).ThenBy(g => g.Min(w => w.Left)))
        {
            var lineWords = group.OrderBy(w => w.Left)
                .Select(w => new TextWord(w.Text, new BBox(w.Left / scale, w.Top / scale, w.Width / scale, w.Height / scale), w.Confidence / 100.0))
                .ToList();
            if (lineWords.Count == 0) continue;
            result.Add(TextLine.FromWords(lineWords));
        }
        return result;
    }

    private List<TsvWord> PngToTsv(byte[] png)
    {
        // psm 3 (авто) для обычных документов и таблиц; на формах с разреженным текстом (СТС, справки)
        // он иногда даёт пусто — тогда psm 6 (единый блок). Возвращаем лучший (по числу слов) результат.
        List<TsvWord> best = [];
        foreach (var psm in new[] { "3", "6" })
        {
            var (code, stdout, _) = Run(["-", "stdout", "-l", Language, "--psm", psm, "tsv"], png);
            if (code != 0) continue;
            var words = ParseTsv(stdout);
            if (words.Count > best.Count) best = words;
            if (psm == "3" && words.Count >= 5) return words;   // достаточно — не гоним второй проход
        }
        return best;
    }

    /// <summary>Разбор TSV: колонки level, page_num, block_num, par_num, line_num, word_num, left, top, width, height, conf, text.</summary>
    public static List<TsvWord> ParseTsv(string tsv)
    {
        var words = new List<TsvWord>();
        var lines = tsv.Split('\n');
        for (int i = 1; i < lines.Length; i++)   // первая строка — заголовок
        {
            var f = lines[i].TrimEnd('\r').Split('\t');
            if (f.Length < 12 || f[0] != "5") continue;   // 5 — уровень слова
            var text = f[11].Trim();
            if (text.Length == 0) continue;
            if (!double.TryParse(f[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var conf) || conf < 0) continue;
            words.Add(new TsvWord(
                int.Parse(f[2], CultureInfo.InvariantCulture), int.Parse(f[3], CultureInfo.InvariantCulture), int.Parse(f[4], CultureInfo.InvariantCulture),
                double.Parse(f[6], CultureInfo.InvariantCulture), double.Parse(f[7], CultureInfo.InvariantCulture),
                double.Parse(f[8], CultureInfo.InvariantCulture), double.Parse(f[9], CultureInfo.InvariantCulture),
                conf, text));
        }
        return words;
    }

    private (int Code, string Stdout, string Stderr) Run(string[] args, byte[]? stdin = null)
    {
        var psi = new ProcessStartInfo(_exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["OMP_THREAD_LIMIT"] = "1";   // предсказуемое потребление CPU в контейнере

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Не удалось запустить {_exe}");
        if (stdin is not null)
        {
            p.StandardInput.BaseStream.Write(stdin);
            p.StandardInput.BaseStream.Flush();
            p.StandardInput.Close();
        }
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }
}

public sealed record TsvWord(int Block, int Paragraph, int Line, double Left, double Top, double Width, double Height, double Confidence, string Text);
