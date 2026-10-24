using System.Text.RegularExpressions;
using AutoParser.Core.Model;
using AutoParser.Core.Parsing;
using AutoParser.Core.Text;
using AutoParser.Core.Validate;

namespace AutoParser.Core.Merge;

/// <summary>
/// Сводит кандидатов в итоговые значения: приоритеты источников, голосование, уверенность, конфликты,
/// затем вычисляемые поля (категория, регион, СТС/ПТС, ЛКП, полис со страховщиком).
/// </summary>
public static partial class FieldMerger
{
    /// <summary>Вес документа как источника поля (0..1).</summary>
    public static double Weight(DocumentType type, FieldKey key)
    {
        switch (key)
        {
            case FieldKey.ClaimNumber:
                return type == DocumentType.FuLetter ? 1 : 0.5;
            case FieldKey.VehicleType or FieldKey.Category or FieldKey.Power or FieldKey.Pts:
                if (type is DocumentType.Sts or DocumentType.PersonalDocs) return 1;
                break;
            case FieldKey.VictimPolicy:
                return type switch
                {
                    DocumentType.InsuranceAct => 1, DocumentType.Policy => 0.95, DocumentType.Payment => 0.8,
                    DocumentType.Application => 0.75, DocumentType.InsurerAnswer => 0.6, _ => 0.4,
                };
            case FieldKey.AccidentPlace:
                return type switch
                {
                    DocumentType.InsuranceAct or DocumentType.AccidentDocs => 1, DocumentType.Application => 0.8,
                    DocumentType.InspectionAct => 0.6, DocumentType.InsurerReport => 0.8, _ => 0.5,
                };
            case FieldKey.Insurer:
                return type == DocumentType.FuLetter ? 1 : 0.7;
        }
        return type switch
        {
            DocumentType.InsurerReport => 1,
            DocumentType.Sts => 0.9,
            DocumentType.InsuranceAct => 0.85,
            DocumentType.InspectionAct => 0.8,
            DocumentType.Policy => 0.8,
            DocumentType.ApplicantExpertise => 0.7,
            DocumentType.PersonalDocs => 0.7,
            DocumentType.FuLetter => 0.7,
            DocumentType.Application => 0.6,
            DocumentType.AccidentDocs => 0.6,
            DocumentType.InsurerAnswer => 0.55,
            DocumentType.Payment => 0.5,
            _ => 0.4,
        };
    }

    /// <summary>Ключ сравнения значений одного поля.</summary>
    public static string CompareKey(FieldKey key, string value) => key switch
    {
        FieldKey.VictimPolicy => new string(value.Where(char.IsDigit).ToArray()),
        FieldKey.Vin or FieldKey.BodyNumber or FieldKey.Plate or FieldKey.Sts or FieldKey.Mileage
            or FieldKey.Year or FieldKey.AccidentDate or FieldKey.ManufactureDate or FieldKey.ClaimNumber
            => new string(TextNorm.Key(value).Where(char.IsLetterOrDigit).ToArray()),
        FieldKey.MakeModel => string.Join(" ", TextNorm.Key(value).Split(' ').Take(2)),
        FieldKey.Owner => string.Join(" ", TextNorm.Key(value).Split(' ').Take(3)),
        FieldKey.AccidentPlace or FieldKey.Insurer => TextNorm.Key(value),
        _ => TextNorm.Key(value),
    };

    public static Dictionary<FieldKey, FieldValue> Merge(IEnumerable<FieldCandidate> candidates)
    {
        var all = candidates.ToList();
        // Значения, замеченные в контексте виновника (его ГРЗ, VIN, полис), — не данные потерпевшего.
        // Отбрасываем, если «виновных» упоминаний (по документам) не меньше, чем обычных.
        var culpritDocs = all.Where(c => c.IsCulprit).GroupBy(c => (c.Key, CompareKey(c.Key, c.Value)))
            .ToDictionary(g => g.Key, g => g.Select(c => c.Source.FilePath).Distinct().Count());
        var victimDocs = all.Where(c => !c.IsCulprit).GroupBy(c => (c.Key, CompareKey(c.Key, c.Value)))
            .ToDictionary(g => g.Key, g => g.Select(c => c.Source.FilePath).Distinct().Count());
        var culprit = culpritDocs.Where(kv => kv.Value >= victimDocs.GetValueOrDefault(kv.Key)).Select(kv => kv.Key).ToHashSet();
        var result = new Dictionary<FieldKey, FieldValue>();
        foreach (var byKey in all.Where(c => !c.IsCulprit).GroupBy(c => c.Key))
        {
            var list = byKey.Where(c => !culprit.Contains((c.Key, CompareKey(c.Key, c.Value)))).ToList();
            if (list.Count == 0) continue;
            result[byKey.Key] = MergeOne(byKey.Key, list);
        }
        foreach (var k in Enum.GetValues<FieldKey>())
            result.TryAdd(k, new FieldValue { Key = k });
        Derive(result);
        return result;
    }

    private sealed class Group
    {
        public required string Key;
        public List<FieldCandidate> Items = [];
        public double Score;
        public int Docs;
        public FieldCandidate Best = null!;
    }

    private static FieldValue MergeOne(FieldKey key, List<FieldCandidate> cands)
    {
        var groups = new List<Group>();
        foreach (var c in cands.OrderByDescending(c => c.Confidence * Weight(c.DocumentType, key)))
        {
            var ck = CompareKey(key, c.Value);
            var g = groups.FirstOrDefault(g => Same(key, g.Key, ck));
            if (g is null) groups.Add(g = new Group { Key = ck });
            g.Items.Add(c);
        }

        foreach (var g in groups)
        {
            // Один документ голосует один раз (лучшим своим кандидатом).
            var perDoc = g.Items.GroupBy(i => i.Source.FilePath)
                .Select(d => d.Max(i => i.Confidence * Weight(i.DocumentType, key))).ToList();
            g.Docs = perDoc.Count;
            g.Score = 1 - perDoc.Aggregate(1.0, (acc, p) => acc * (1 - Math.Min(p, 0.97)));
            // Представитель группы: из текстового слоя, затем по уверенности.
            g.Best = g.Items.OrderByDescending(i => i.Source.Origin == TextOrigin.TextLayer)
                .ThenByDescending(i => i.Confidence * Weight(i.DocumentType, key)).First();
        }

        var ordered = groups.OrderByDescending(g => g.Score).ToList();
        var best = ordered[0];
        // «Легковой» и «Легковой купе» — берём более подробное.
        if (key is FieldKey.VehicleType or FieldKey.Category)
            best.Best = best.Items.OrderByDescending(i => i.Value.Length).ThenByDescending(i => i.Confidence).First();
        var rival = ordered.Skip(1).FirstOrDefault();
        var fv = new FieldValue
        {
            Key = key,
            Value = best.Best.Value,
            Source = best.Best.Source,
            Candidates = cands.OrderByDescending(c => c.Confidence).ToList(),
            Confidence = Math.Round(Math.Min(best.Score, 0.99), 3),
        };

        // Одиночное OCR-значение против подтверждённого — не конфликт, а шум распознавания.
        bool bestHasText = best.Items.Any(i => i.Source.Origin == TextOrigin.TextLayer);
        bool rivalWeighty = rival is not null && (rival.Docs >= 2 || (best.Docs == 1 && !bestHasText)
            || rival.Items.Any(i => i.Source.Origin == TextOrigin.TextLayer && i.DocumentType != DocumentType.Unknown));
        if (rival is not null && rivalWeighty && rival.Score >= best.Score * 0.6 && rival.Score > 0.35)
        {
            fv.Status = FieldStatus.Conflict;
            fv.Confidence = Math.Round(fv.Confidence * 0.7, 3);
            fv.Note = $"Расхождение: «{rival.Best.Value}» ({rival.Best.Source})";
        }
        else if (best.Docs >= 2 && fv.Confidence >= 0.8) fv.Status = FieldStatus.Ok;
        else if (fv.Confidence >= 0.9) fv.Status = FieldStatus.Ok;
        else
        {
            fv.Status = FieldStatus.Check;
            fv.Note = best.Docs == 1 ? "Найдено в одном источнике" : "Низкая уверенность";
        }
        if (best.Best.Note is not null) fv.Note = fv.Note is null ? best.Best.Note : $"{fv.Note}; {best.Best.Note}";
        return fv;
    }

    private static bool Same(FieldKey key, string a, string b)
    {
        if (a == b) return true;
        // VIN/кузов из OCR СТС часто отличаются на 1–2 символа — считаем подтверждением.
        if (key is FieldKey.Vin or FieldKey.BodyNumber && a.Length == 17 && b.Length == 17)
            return VinValidator.Distance(a.ToUpperInvariant(), b.ToUpperInvariant()) <= 2;
        if (key is FieldKey.Category or FieldKey.VehicleType) return a.StartsWith(b) || b.StartsWith(a);
        // «DAEWOO NEXIA» и «CHEVROLET DAEWOO, Nexia II (N150)» — одна машина: общие слова марки/модели.
        if (key == FieldKey.MakeModel)
        {
            var wa = a.Split(' ').Where(w => w.Length >= 2).ToHashSet();
            if (b.Split(' ').Any(w => w.Length >= 2 && (wa.Contains(w) || wa.Any(x => x.StartsWith(w) || w.StartsWith(x))))) return true;
        }
        if (key == FieldKey.AccidentPlace && RegionDirectory.Default.ByPlace(a) is { } ra && RegionDirectory.Default.ByPlace(b) is { } rb)
            return ra == rb;
        if (key is FieldKey.MakeModel or FieldKey.Owner or FieldKey.Insurer or FieldKey.AccidentPlace)
            return a.StartsWith(b) || b.StartsWith(a) || TextNorm.Levenshtein(a, b) <= Math.Max(1, Math.Min(a.Length, b.Length) / 8);
        return false;
    }

    /// <summary>Вычисляемые и составные поля.</summary>
    private static void Derive(Dictionary<FieldKey, FieldValue> f)
    {
        FieldValue F(FieldKey k) => f[k];
        bool Has(FieldKey k) => F(k).Value.Length > 0;

        void SetDerived(FieldKey k, string value, string note, double conf, FieldStatus status = FieldStatus.Derived)
        {
            var v = F(k);
            v.Value = value; v.Note = note; v.Confidence = conf; v.Status = status;
            v.Source ??= SourceRef.Derived(note);
        }

        // Год ↔ дата изготовления.
        if (!Has(FieldKey.Year) && Has(FieldKey.ManufactureDate) && F(FieldKey.ManufactureDate).Value.Length >= 4)
            SetDerived(FieldKey.Year, F(FieldKey.ManufactureDate).Value[^4..], "Из даты изготовления", F(FieldKey.ManufactureDate).Confidence);
        if (!Has(FieldKey.ManufactureDate) && Has(FieldKey.Year))
            SetDerived(FieldKey.ManufactureDate, F(FieldKey.Year).Value, "Нет в документах: указан год выпуска — проверить по ПТС/калькуляции", 0.4, FieldStatus.Check);

        // Категория из типа ТС.
        if (!Has(FieldKey.Category) && Has(FieldKey.VehicleType) && ValueTypes.CategoryFromType(F(FieldKey.VehicleType).Value) is { } cat)
            SetDerived(FieldKey.Category, cat, "Из типа ТС", F(FieldKey.VehicleType).Confidence * 0.95);
        else if (Has(FieldKey.Category) && F(FieldKey.Category).Value.Length == 1 && Has(FieldKey.VehicleType)
                 && ValueTypes.CategoryFromType(F(FieldKey.VehicleType).Value) is { } full && full.StartsWith(F(FieldKey.Category).Value))
            F(FieldKey.Category).Value = full;

        // Номер кузова из OCR, отличающийся от VIN парой символов, — это тот же VIN, распознанный с ошибками.
        if (Has(FieldKey.BodyNumber) && Has(FieldKey.Vin) && F(FieldKey.BodyNumber).Value != F(FieldKey.Vin).Value
            && F(FieldKey.BodyNumber).Value.Length is >= 15 and <= 18
            && TextNorm.Levenshtein(F(FieldKey.BodyNumber).Value, F(FieldKey.Vin).Value) <= 5
            && F(FieldKey.BodyNumber).Confidence < F(FieldKey.Vin).Confidence)
        {
            var ocrBody = F(FieldKey.BodyNumber).Value;
            SetDerived(FieldKey.BodyNumber, F(FieldKey.Vin).Value, $"Номер кузова в OCR («{ocrBody}») совпадает с VIN с точностью до ошибок распознавания",
                F(FieldKey.Vin).Confidence * 0.8, FieldStatus.Check);
        }

        // Кузов = VIN, если номер кузова не указан отдельно.
        if (!Has(FieldKey.BodyNumber) && Has(FieldKey.Vin))
            SetDerived(FieldKey.BodyNumber, F(FieldKey.Vin).Value, "Номер кузова не найден: совпадает с VIN — проверить", F(FieldKey.Vin).Confidence * 0.6, FieldStatus.Check);

        // Тип и цвет ЛКП.
        if (Has(FieldKey.Color) || Has(FieldKey.PaintType))
        {
            var color = F(FieldKey.Color).Value;
            var type = F(FieldKey.PaintType).Value;
            var paint = F(FieldKey.Paint);
            if (!Has(FieldKey.Paint))
            {
                paint.Value = type.Length > 0 && color.Length > 0 ? $"{color} / {type}" : color.Length > 0 ? color : type;
                paint.Confidence = Math.Min(F(FieldKey.Color).Confidence > 0 ? F(FieldKey.Color).Confidence : 1, type.Length > 0 ? F(FieldKey.PaintType).Confidence : 0.6);
                paint.Source = F(FieldKey.Color).Source ?? F(FieldKey.PaintType).Source;
                paint.Status = type.Length > 0 && color.Length > 0 ? FieldStatus.Derived : FieldStatus.Check;
                paint.Note = type.Length == 0 ? "Тип покрытия не найден — ручной ввод" : color.Length == 0 ? "Цвет не найден" : "Цвет + тип покрытия";
                paint.Candidates.AddRange(F(FieldKey.Color).Candidates.Concat(F(FieldKey.PaintType).Candidates));
            }
        }

        // СТС/ПТС.
        if (Has(FieldKey.Sts) || Has(FieldKey.Pts))
        {
            var sts = F(FieldKey.Sts);
            var v = F(FieldKey.StsPts);
            v.Value = $"{(sts.Value.Length > 0 ? sts.Value : "-")} / {(Has(FieldKey.Pts) ? F(FieldKey.Pts).Value : "-")}";
            v.Confidence = sts.Value.Length > 0 ? sts.Confidence : F(FieldKey.Pts).Confidence * 0.8;
            v.Status = sts.Status == FieldStatus.Missing ? FieldStatus.Check : sts.Status;
            v.Source = sts.Source ?? F(FieldKey.Pts).Source;
            v.Note = sts.Note;
            v.Candidates.AddRange(sts.Candidates.Concat(F(FieldKey.Pts).Candidates));
        }

        // Дата ДТП в формате шаблона: «28.03.2024 г.».
        if (Has(FieldKey.AccidentDate) && !F(FieldKey.AccidentDate).Value.EndsWith("г."))
            F(FieldKey.AccidentDate).Value += " г.";

        // Место ДТП → «г. Нижний Новгород», региональный рынок по справочнику субъектов.
        var regions = RegionDirectory.Default;
        if (Has(FieldKey.AccidentPlace))
        {
            var place = F(FieldKey.AccidentPlace);
            var city = regions.FindCity(place.Value);
            var region = regions.ByPlace(place.Value);
            if (city is not null && CityOnlyRx().IsMatch(place.Value)) place.Value = $"г. {city}";
            if (region is not null && !Has(FieldKey.RegionalMarket))
                SetDerived(FieldKey.RegionalMarket, region.Name, "По месту ДТП (справочник субъектов РФ)", Math.Min(0.95, place.Confidence));
        }
        if (!Has(FieldKey.RegionalMarket) && Has(FieldKey.Plate) && regions.ByPlate(F(FieldKey.Plate).Value) is { } byPlate)
            SetDerived(FieldKey.RegionalMarket, byPlate.Name, $"По коду региона ГРЗ ({PlateNormalizer.RegionCode(F(FieldKey.Plate).Value)}) — место ДТП не найдено, проверить", 0.5, FieldStatus.Check);

        // Полис потерпевшего + страховщик.
        var pol = F(FieldKey.VictimPolicy);
        if (pol.Value.Length > 0)
        {
            if (Has(FieldKey.Insurer) && !pol.Value.Contains('«')) pol.Value = $"{pol.Value} {F(FieldKey.Insurer).Value}";
        }
        else
        {
            pol.Value = "Не предоставлено";
            pol.Status = FieldStatus.Missing;
            pol.Note = "Полис не найден в документах";
        }

        // Номер кузова «отсутствует» при раме и т.п. — оставляем как есть.
        foreach (var k in FieldInfo.ReportFields)
            if (F(k).Value.Length == 0) { F(k).Status = FieldStatus.Missing; F(k).Note ??= "Не найдено — ручной ввод"; }
    }

    // Место уровня населённого пункта без улицы: «г. Н. Новгород», «Нижний Новгород».
    [GeneratedRegex(@"^\s*(г\.?\s*|город\s+)?[А-ЯЁ][\w\.\-\s]{2,30}$")]
    private static partial Regex CityOnlyRx();
}
