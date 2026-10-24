using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AutoParser.Core.Text;

/// <summary>
/// Облачный OCR Yandex Vision (необязательный). Умеет рукописный текст (модель handwritten) и таблицы (table).
/// Включается только при заданных ключах: YANDEX_VISION_API_KEY (или YANDEX_IAM_TOKEN) и YANDEX_FOLDER_ID.
/// Серверы в РФ (152-ФЗ), но персональные данные уходят из локальной сети — по умолчанию используется локальный OCR.
/// </summary>
public sealed class YandexVisionOcrEngine : IOcrEngine, IDisposable
{
    public const string Endpoint = "https://ocr.api.cloud.yandex.net/ocr/v1/recognizeText";

    private readonly HttpClient _http;
    private readonly string? _apiKey;
    private readonly string? _iamToken;
    private readonly string? _folderId;

    /// <param name="model">page — печатный текст, handwritten — рукопись, table — таблицы.</param>
    public YandexVisionOcrEngine(string? apiKey = null, string? folderId = null, string? iamToken = null, string model = "page", HttpClient? http = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("YANDEX_VISION_API_KEY");
        _iamToken = iamToken ?? Environment.GetEnvironmentVariable("YANDEX_IAM_TOKEN");
        _folderId = folderId ?? Environment.GetEnvironmentVariable("YANDEX_FOLDER_ID");
        Model = model;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public string Model { get; }
    public string Name => $"YandexVision-{Model}";
    public bool IsAvailable => (_apiKey is not null || _iamToken is not null) && _folderId is not null;

    public async Task<PageText> RecognizeAsync(byte[] image, int pageNumber, OcrOptions? options = null, CancellationToken ct = default)
    {
        if (!IsAvailable) throw new InvalidOperationException("Yandex Vision: не заданы YANDEX_VISION_API_KEY/YANDEX_IAM_TOKEN и YANDEX_FOLDER_ID");
        var (srcW, srcH) = ImagePreprocessor.GetSize(image);
        // EXIF-ориентация и масштаб; поворот страницы Vision определяет сам.
        var (png, k) = ImagePreprocessor.Prepare(image);

        var body = JsonSerializer.Serialize(new
        {
            mimeType = "PNG",
            languageCodes = new[] { "ru", "en" },
            model = Model,
            content = Convert.ToBase64String(png),
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        req.Headers.Authorization = _apiKey is not null
            ? new AuthenticationHeaderValue("Api-Key", _apiKey)
            : new AuthenticationHeaderValue("Bearer", _iamToken);
        req.Headers.Add("x-folder-id", _folderId);
        req.Headers.Add("x-data-logging-enabled", "false");

        using var resp = await _http.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Yandex Vision {(int)resp.StatusCode}: {json}");

        return Parse(json, pageNumber, k, srcW, srcH, Name);
    }

    /// <summary>Разбор ответа recognizeText (вынесен для тестов).</summary>
    public static PageText Parse(string json, int pageNumber, double scale, double width, double height, string engine)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("result", out var result)) root = result;
        var lines = new List<TextLine>();
        if (root.TryGetProperty("textAnnotation", out var ta) && ta.TryGetProperty("blocks", out var blocks))
        {
            foreach (var block in blocks.EnumerateArray())
            {
                if (!block.TryGetProperty("lines", out var ls)) continue;
                foreach (var line in ls.EnumerateArray())
                {
                    var words = new List<TextWord>();
                    if (line.TryGetProperty("words", out var ws))
                        foreach (var w in ws.EnumerateArray())
                        {
                            var text = w.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                            if (text.Length == 0) continue;
                            words.Add(new TextWord(text, Box(w).Scale(1 / scale), 0.9));
                        }
                    if (words.Count > 0) lines.Add(TextLine.FromWords(words));
                }
            }
        }
        return new PageText
        {
            PageNumber = pageNumber, Width = width, Height = height, Lines = lines,
            Origin = TextOrigin.Ocr, OcrEngine = engine,
        };
    }

    private static BBox Box(JsonElement e)
    {
        if (!e.TryGetProperty("boundingBox", out var bb) || !bb.TryGetProperty("vertices", out var vs)) return default;
        double l = double.MaxValue, t = double.MaxValue, r = 0, b = 0;
        foreach (var v in vs.EnumerateArray())
        {
            // Координаты приходят строками, нулевые могут отсутствовать.
            double x = Num(v, "x"), y = Num(v, "y");
            l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y);
        }
        return l == double.MaxValue ? default : new BBox(l, t, r - l, b - t);
    }

    private static double Num(JsonElement v, string name) =>
        v.TryGetProperty(name, out var p)
            ? p.ValueKind == JsonValueKind.String ? double.Parse(p.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : p.GetDouble()
            : 0;

    public void Dispose() => _http.Dispose();
}
