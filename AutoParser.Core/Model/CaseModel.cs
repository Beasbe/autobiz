using AutoParser.Core.Text;

namespace AutoParser.Core.Model;

public enum DocumentType
{
    Unknown,
    /// <summary>Экспертное заключение страховщика (калькуляция, годные остатки).</summary>
    InsurerReport,
    /// <summary>Свидетельство о регистрации ТС.</summary>
    Sts,
    /// <summary>Полис ОСАГО.</summary>
    Policy,
    /// <summary>Акт о страховом случае.</summary>
    InsuranceAct,
    /// <summary>Акт осмотра ТС.</summary>
    InspectionAct,
    /// <summary>Письмо финансового уполномоченного «О направлении заявки…».</summary>
    FuLetter,
    /// <summary>Заявление о страховом возмещении / обращение / претензия.</summary>
    Application,
    /// <summary>Документы ГИБДД: постановление, извещение, схема, сведения об участниках.</summary>
    AccidentDocs,
    /// <summary>Платёжные поручения.</summary>
    Payment,
    /// <summary>Ответ финансовой организации.</summary>
    InsurerAnswer,
    /// <summary>Паспорт, водительское удостоверение, доверенность.</summary>
    PersonalDocs,
    /// <summary>Фотоматериалы.</summary>
    Photos,
    /// <summary>Готовое заключение эксперта (результат работы, не источник).</summary>
    ExpertOutput,
    /// <summary>Независимая экспертиза заявителя.</summary>
    ApplicantExpertise,
    /// <summary>Договоры, акты выполненных работ, переписка.</summary>
    Other,
}

public static class DocumentTypeInfo
{
    public static string Title(DocumentType t) => t switch
    {
        DocumentType.InsurerReport => "Заключение страховщика",
        DocumentType.Sts => "СТС",
        DocumentType.Policy => "Полис ОСАГО",
        DocumentType.InsuranceAct => "Акт о страховом случае",
        DocumentType.InspectionAct => "Акт осмотра",
        DocumentType.FuLetter => "Письмо ФУ",
        DocumentType.Application => "Заявление / обращение",
        DocumentType.AccidentDocs => "Документы ГИБДД",
        DocumentType.Payment => "Платёжный документ",
        DocumentType.InsurerAnswer => "Ответ страховщика",
        DocumentType.PersonalDocs => "Личные документы",
        DocumentType.Photos => "Фотоматериалы",
        DocumentType.ExpertOutput => "Заключение эксперта (готовое)",
        DocumentType.ApplicantExpertise => "Экспертиза заявителя",
        DocumentType.Other => "Прочее",
        _ => "Не определён",
    };
}

/// <summary>Файл заявки.</summary>
public sealed class CaseDocument
{
    public required string FilePath { get; init; }
    public required string Hash { get; init; }
    public long Size { get; init; }
    public string FileName => Path.GetFileName(FilePath);
    public string RelativePath { get; init; } = "";

    /// <summary>Файл с тем же SHA-256 уже есть в заявке.</summary>
    public CaseDocument? DuplicateOf { get; set; }

    public DocumentType Type { get; set; }
    public double TypeConfidence { get; set; }
    public DocumentText? Text { get; set; }
    public int PageCount => Text?.Pages.Count ?? 0;
    public int OcrPages => Text?.Pages.Count(p => p.Origin == TextOrigin.Ocr) ?? 0;
    public string? Error { get; set; }

    /// <summary>Документ не разбирался (дубликат, фото, неподдерживаемый формат).</summary>
    public string? SkipReason { get; set; }

    public override string ToString() => $"{FileName} [{DocumentTypeInfo.Title(Type)}]";
}

/// <summary>Строка таблицы «Описание поврежденных элементов и видов ремонтных воздействий».</summary>
public sealed class DamageRow
{
    public int No { get; set; }
    public string Element { get; set; } = "";
    public string Character { get; set; } = "";
    public string FoStudy { get; set; } = "";
    public string ApplicantStudy { get; set; } = "";
    public string PhotoPresence { get; set; } = "";
    public string Relation { get; set; } = "";
    public string Conclusion { get; set; } = "";

    /// <summary>Исходная строка документа (например, строка калькуляции Audatex).</summary>
    public string RawText { get; set; } = "";
    public double Confidence { get; set; }
    public SourceRef? Source { get; set; }
}

/// <summary>Результат обработки папки заявки.</summary>
public sealed class CaseReport
{
    public required string Folder { get; init; }
    public string ExpertiseKind { get; set; } = "НТЭ";
    public List<CaseDocument> Documents { get; init; } = [];
    public Dictionary<FieldKey, FieldValue> Fields { get; init; } = [];
    public List<DamageRow> Damages { get; set; } = [];
    public List<string> Log { get; init; } = [];

    public FieldValue this[FieldKey key] => Fields.TryGetValue(key, out var v) ? v : Fields[key] = new FieldValue { Key = key };

    public string Get(FieldKey key) => Fields.TryGetValue(key, out var v) ? v.Value : "";
}
