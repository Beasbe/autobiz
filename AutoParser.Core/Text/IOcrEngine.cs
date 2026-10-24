namespace AutoParser.Core.Text;

public sealed record OcrOptions
{
    /// <summary>Пробовать повороты 90/180/270, если текст почти не находится.</summary>
    public bool DetectOrientation { get; init; } = true;

    public string Language { get; init; } = "ru";
}

/// <summary>Распознаёт текст на изображении. Координаты результата — в пикселях переданного изображения.</summary>
public interface IOcrEngine
{
    string Name { get; }
    bool IsAvailable { get; }
    Task<PageText> RecognizeAsync(byte[] image, int pageNumber, OcrOptions? options = null, CancellationToken ct = default);

    /// <summary>С какого dpi рендерить PDF-страницы (null — значение по умолчанию сервиса).</summary>
    int? PreferredDpi => null;
}
