using AutoParser.Core.Model;
using AutoParser.Core.Text;
using AutoParser.Core.Validate;

namespace AutoParser.Core.Classify;

/// <summary>
/// Определяет тип документа: сначала по имени файла (дёшево, до OCR), затем по ключевым фразам текста.
/// Содержимое важнее имени: «св.jpg» в данных оказался платёжным поручением.
/// </summary>
public static class DocumentClassifier
{
    private sealed record Rule(DocumentType Type, double Weight, params string[] Phrases);

    // Ключевые фразы содержимого (сравнение по TextNorm.Key, с допуском опечаток OCR).
    private static readonly Rule[] ContentRules =
    [
        new(DocumentType.InsurerReport, 3, "экспертное заключение", "калькуляция", "стоимости восстановительного ремонта", "годных остатков", "расчетная стоимость восстановительного ремонта", "эксперт техник"),
        new(DocumentType.Sts, 4, "свидетельство о регистрации тс", "certificat d immatriculation", "особые отметки", "собственник владелец", "одобрение типа тс", "экологический класс"),
        new(DocumentType.Policy, 4, "страховой полис страховая премия", "лица допущенные к управлению транспортным средством", "страховая премия", "срок страхования с"),
        new(DocumentType.InsuranceAct, 4, "акт о страховом случае", "данные о страховом полисе потерпевшего", "решение по заявленному событию", "расчет страхового возмещения"),
        new(DocumentType.InspectionAct, 3, "акт осмотра транспортного средства", "акт осмотра", "при осмотре обнаружены следующие повреждения", "адрес осмотра", "дата осмотра"),
        new(DocumentType.FuLetter, 5, "о направлении заявки о проведении экспертизы", "вопросы подлежащие разрешению экспертом"),
        new(DocumentType.InsurerAnswer, 3, "на запрос от", "сообщает следующее", "в ответ на", "финансовая организация"),
        new(DocumentType.Application, 2, "заявление о страховом возмещении", "заявление о прямом возмещении убытков", "прошу произвести страховое возмещение", "претензия", "обращение", "существо спора", "финансовому уполномоченному"),
        new(DocumentType.AccidentDocs, 2, "постановление по делу об административном правонарушении", "извещение о дорожно транспортном происшествии", "схема дтп", "сведения об участниках", "определение об отказе"),
        new(DocumentType.Payment, 4, "платежное поручение", "назначение платежа", "списано со сч плат"),
        new(DocumentType.PersonalDocs, 2, "паспорт гражданина", "водительское удостоверение", "driving licence", "доверенность"),
    ];

    // Подсказки по имени файла: (подстрока, тип, вес).
    private static readonly (string Part, DocumentType Type, double Weight)[] NameRules =
    [
        ("фото", DocumentType.Photos, 3),
        ("экспертное заключение страховщика", DocumentType.InsurerReport, 5),
        ("расчет ущерба", DocumentType.InsurerReport, 3),
        ("тотал", DocumentType.InsurerReport, 3),
        ("калькуляц", DocumentType.InsurerReport, 3),
        ("заключение", DocumentType.InsurerReport, 1),
        ("стс", DocumentType.Sts, 3),
        ("свидетельства о регистрации", DocumentType.Sts, 4),
        ("свидетельство о регистрации", DocumentType.Sts, 4),
        ("осаго", DocumentType.Policy, 2),
        ("полис", DocumentType.Policy, 3),
        ("акт осмотра", DocumentType.InspectionAct, 3),
        ("о направлении заявки", DocumentType.FuLetter, 5),
        ("заявлени", DocumentType.Application, 2),
        ("обращени", DocumentType.Application, 2),
        ("претензи", DocumentType.Application, 2),
        ("схема дтп", DocumentType.AccidentDocs, 3),
        ("данные водителей", DocumentType.AccidentDocs, 3),
        ("участниках дтп", DocumentType.AccidentDocs, 3),
        ("определение", DocumentType.AccidentDocs, 2),
        ("пп ", DocumentType.Payment, 2),
        ("платеж", DocumentType.Payment, 3),
        ("неустойка", DocumentType.Payment, 1),
        ("паспорт", DocumentType.PersonalDocs, 3),
        ("личные документы", DocumentType.PersonalDocs, 2),
        ("доверенност", DocumentType.PersonalDocs, 3),
        ("водительское", DocumentType.PersonalDocs, 3),
        ("ответ", DocumentType.InsurerAnswer, 2),
        ("договор", DocumentType.Other, 2),
        ("акт + чек", DocumentType.Other, 3),
        ("стоа", DocumentType.Other, 2),
        ("переписка", DocumentType.Other, 2),
        ("экспертиза", DocumentType.ApplicantExpertise, 2),
        ("смета", DocumentType.Other, 1),
    ];

    /// <summary>Предварительный тип по имени файла: нужен, чтобы не гонять OCR по фотографиям и договорам.</summary>
    public static (DocumentType Type, double Weight) ByFileName(string path)
    {
        var name = TextNorm.Key(Path.GetFileNameWithoutExtension(path)) + " ";
        var dir = TextNorm.Key(Path.GetFileName(Path.GetDirectoryName(path) ?? ""));
        // Готовое заключение эксперта: «У-26-130531_3020-005_Михайлова Т.В._НТЭ.docx».
        if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"^[УY]-\d{2}-\d{6}_\d{4}-\d{3}_.*_(НТЭ|ИРУ)\.(docx|pdf)$"))
            return (DocumentType.ExpertOutput, 10);
        if (dir == "фото") return (DocumentType.Photos, 5);
        var best = NameRules.Where(r => name.Contains(TextNorm.Key(r.Part) + (r.Part.EndsWith(' ') ? " " : "")))
            .OrderByDescending(r => r.Weight).FirstOrDefault();
        return best.Part is null ? (DocumentType.Unknown, 0) : (best.Type, best.Weight);
    }

    /// <summary>Итоговый тип: имя файла + содержимое первых страниц.</summary>
    public static (DocumentType Type, double Confidence) Classify(string path, DocumentText? text)
    {
        var (nameType, nameWeight) = ByFileName(path);
        if (nameType is DocumentType.ExpertOutput) return (nameType, 1);

        var scores = new Dictionary<DocumentType, double>();
        if (nameType != DocumentType.Unknown) scores[nameType] = nameWeight;

        if (text is not null && text.Pages.Count > 0)
        {
            // Первые 3 страницы — заголовки; для длинных заключений этого достаточно.
            var head = TextNorm.Key(string.Join(" ", text.Pages.Take(3).Select(p => p.FullText)));
            foreach (var rule in ContentRules)
            {
                int hits = rule.Phrases.Count(p => head.Contains(TextNorm.Key(p)));
                if (hits > 0) scores[rule.Type] = scores.GetValueOrDefault(rule.Type) + rule.Weight * (1 + 0.5 * (hits - 1));
            }
            // Многостраничное заключение с формулировками Единой методики — точно отчёт страховщика.
            if (text.Pages.Count >= 8 && head.Contains("единой методик")) scores[DocumentType.InsurerReport] = scores.GetValueOrDefault(DocumentType.InsurerReport) + 3;
            // Письмо ФУ упоминает экспертное заключение — не путаем.
            if (scores.ContainsKey(DocumentType.FuLetter) && head.Contains("о направлении заявки")) scores[DocumentType.FuLetter] += 10;
        }

        if (scores.Count == 0) return (DocumentType.Unknown, 0);
        var ordered = scores.OrderByDescending(kv => kv.Value).ToList();
        var top = ordered[0];
        var second = ordered.Count > 1 ? ordered[1].Value : 0;
        var conf = Math.Clamp(0.5 + (top.Value - second) / 10, 0.3, 0.99);
        return (top.Key, conf);
    }

    /// <summary>Нужно ли извлекать поля из документа этого типа.</summary>
    public static bool IsFieldSource(DocumentType t) => t is not (DocumentType.Photos or DocumentType.ExpertOutput or DocumentType.Other);

    /// <summary>Стоит ли распознавать все страницы (OCR) документа этого предварительного типа.</summary>
    public static bool WorthFullOcr(DocumentType t) => t is not (DocumentType.Photos or DocumentType.ExpertOutput or DocumentType.Other or DocumentType.PersonalDocs);
}
