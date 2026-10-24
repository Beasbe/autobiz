using System.Runtime.Versioning;
using System.Windows.Media;
using AutoParser.Core.Export;
using AutoParser.Core.Model;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AutoParser.App.ViewModels;

/// <summary>Строка таблицы полей: значение редактируется, правка помечает поле как «Вручную».</summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class FieldItem : ObservableObject
{
    public FieldValue Field { get; }

    public FieldItem(FieldValue field) => Field = field;

    public string Title => Field.Title;

    public string Value
    {
        get => Field.Value;
        set
        {
            if (value == Field.Value) return;
            Field.SetManual(value.Trim());
            OnPropertyChanged();
            Refresh();
        }
    }

    public string Confidence => Field.Status is FieldStatus.Missing ? "" : $"{Field.Confidence:P0}";
    public string Status => DamageXlsxExporter.StatusTitle(Field.Status);
    public string Source => Field.Source?.ToString() ?? "";
    public string Note => Field.Note ?? "";
    public int CandidateCount => Field.Candidates.Count;

    public Brush StatusBrush => Field.Status switch
    {
        FieldStatus.Ok => Brushes.ForestGreen,
        FieldStatus.Manual => Brushes.SteelBlue,
        FieldStatus.Derived => Brushes.Gray,
        FieldStatus.Check => Brushes.DarkGoldenrod,
        FieldStatus.Conflict => Brushes.OrangeRed,
        _ => Brushes.Firebrick,
    };

    public Brush RowBrush => Field.Status switch
    {
        FieldStatus.Conflict => new SolidColorBrush(Color.FromRgb(0xFD, 0xE8, 0xE0)),
        FieldStatus.Missing => new SolidColorBrush(Color.FromRgb(0xFC, 0xEE, 0xEE)),
        FieldStatus.Check => new SolidColorBrush(Color.FromRgb(0xFF, 0xF8, 0xE1)),
        _ => Brushes.Transparent,
    };

    public void ApplyCandidate(FieldCandidate c)
    {
        Field.Value = c.Value;
        Field.Source = c.Source;
        Field.Confidence = c.Confidence;
        Field.Status = FieldStatus.Manual;
        Field.Note = "Выбран вариант вручную";
        OnPropertyChanged(nameof(Value));
        Refresh();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(Confidence));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(RowBrush));
        OnPropertyChanged(nameof(Source));
        OnPropertyChanged(nameof(Note));
    }
}

public sealed class CandidateItem(FieldCandidate c)
{
    public FieldCandidate Candidate => c;
    public string Value => c.Value;
    public string Confidence => $"{c.Confidence:P0}";
    public string Document => $"{DocumentTypeInfo.Title(c.DocumentType)}: {c.Source}";
    public string Snippet => c.Source.Snippet ?? "";
    public string Parser => c.Parser + (c.Note is null ? "" : $" ({c.Note})");
}

public sealed class DocumentItem(CaseDocument d)
{
    public CaseDocument Document => d;
    public string Name => d.RelativePath;
    public string Type => DocumentTypeInfo.Title(d.Type);
    public string Pages => d.PageCount == 0 ? "" : d.OcrPages > 0 ? $"{d.PageCount} (OCR {d.OcrPages})" : d.PageCount.ToString();
    public string State => d.SkipReason ?? d.Error ?? "";
    public bool IsSkipped => d.SkipReason is not null;
}
