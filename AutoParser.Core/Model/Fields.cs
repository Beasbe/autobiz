using AutoParser.Core.Text;

namespace AutoParser.Core.Model;

/// <summary>Поля «Общего отчёта» из ТЗ — в порядке таблицы «Данные ТС потерпевшего».</summary>
public enum FieldKey
{
    ClaimNumber,
    MakeModel,
    Plate,
    Vin,
    Mileage,
    VehicleType,
    Category,
    Year,
    ManufactureDate,
    BodyNumber,
    Power,
    Gearbox,
    Paint,
    StsPts,
    Owner,
    AccidentDate,
    AccidentPlace,
    RegionalMarket,
    VictimPolicy,

    // Вспомогательные поля: сами в отчёт не идут, но нужны для вычисления основных.
    Color,
    PaintType,
    Sts,
    Pts,
    Insurer,
    Applicant,
}

public static class FieldInfo
{
    /// <summary>Подписи полей так, как они написаны в шаблоне заключения.</summary>
    public static readonly IReadOnlyDictionary<FieldKey, string> Titles = new Dictionary<FieldKey, string>
    {
        [FieldKey.ClaimNumber] = "Номер заявки",
        [FieldKey.MakeModel] = "Марка, модель ТС",
        [FieldKey.Plate] = "Государственный регистрационный знак",
        [FieldKey.Vin] = "Идентификационный номер (VIN)",
        [FieldKey.Mileage] = "Пробег, км",
        [FieldKey.VehicleType] = "Наименование (тип ТС)",
        [FieldKey.Category] = "Категория ТС",
        [FieldKey.Year] = "Год выпуска",
        [FieldKey.ManufactureDate] = "Дата изготовления (выпуска) ТС",
        [FieldKey.BodyNumber] = "Кузов № / Рама №",
        [FieldKey.Power] = "Мощность двигателя, Квт/лс",
        [FieldKey.Gearbox] = "Тип коробки передач",
        [FieldKey.Paint] = "Тип и цвет ЛКП",
        [FieldKey.StsPts] = "СТС/ПТС",
        [FieldKey.Owner] = "Собственник",
        [FieldKey.AccidentDate] = "Дата ДТП",
        [FieldKey.AccidentPlace] = "Место ДТП",
        [FieldKey.RegionalMarket] = "Региональный товарный рынок",
        [FieldKey.VictimPolicy] = "Страховой полис потерпевшего",
        [FieldKey.Color] = "Цвет ЛКП",
        [FieldKey.PaintType] = "Тип ЛКП",
        [FieldKey.Sts] = "СТС",
        [FieldKey.Pts] = "ПТС",
        [FieldKey.Insurer] = "Страховщик потерпевшего",
        [FieldKey.Applicant] = "Заявитель",
    };

    public static readonly IReadOnlyList<FieldKey> ReportFields =
        Enum.GetValues<FieldKey>().Where(k => k <= FieldKey.VictimPolicy).ToList();

    public static bool IsReportField(FieldKey k) => k <= FieldKey.VictimPolicy;
}

/// <summary>Откуда взято значение: файл, страница, прямоугольник на странице.</summary>
public sealed record SourceRef(string FilePath, int Page, BBox? Box, TextOrigin Origin, string? Snippet = null)
{
    public string FileName => Path.GetFileName(FilePath);

    public bool IsDerived => Page == 0 && Origin == TextOrigin.None;

    public override string ToString() =>
        IsDerived ? FilePath : Page > 0 ? $"{FileName}, стр. {Page}{(Origin == TextOrigin.Ocr ? " (OCR)" : "")}" : FileName;

    public static SourceRef Derived(string what) => new(what, 0, null, TextOrigin.None);
}

/// <summary>Одно найденное значение-кандидат.</summary>
public sealed record FieldCandidate(
    FieldKey Key,
    string Value,
    double Confidence,
    SourceRef Source,
    DocumentType DocumentType,
    string Parser,
    string? Note = null,
    bool IsCulprit = false);

public enum FieldStatus
{
    /// <summary>Значение найдено, источники согласны.</summary>
    Ok,
    /// <summary>Найдено в одном источнике или с низкой уверенностью.</summary>
    Check,
    /// <summary>Источники противоречат друг другу.</summary>
    Conflict,
    /// <summary>Вычислено из других полей.</summary>
    Derived,
    /// <summary>Не найдено — нужен ручной ввод.</summary>
    Missing,
    /// <summary>Введено/исправлено вручную.</summary>
    Manual,
}

public sealed class FieldValue
{
    public required FieldKey Key { get; init; }
    public string Title => FieldInfo.Titles[Key];
    public string Value { get; set; } = "";
    public double Confidence { get; set; }
    public FieldStatus Status { get; set; } = FieldStatus.Missing;
    public SourceRef? Source { get; set; }
    public string? Note { get; set; }
    public List<FieldCandidate> Candidates { get; init; } = [];

    public void SetManual(string value)
    {
        Value = value;
        Status = FieldStatus.Manual;
        Confidence = 1;
        Note = "Введено вручную";
    }

    public override string ToString() => $"{Title}: {Value} [{Status}, {Confidence:P0}]";
}
