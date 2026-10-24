using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media.Imaging;
using AutoParser.Core.Export;
using AutoParser.Core.Model;
using AutoParser.Core.Pipeline;
using AutoParser.Core.Text;
using AutoParser.Ocr.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace AutoParser.App.ViewModels;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class MainViewModel : ObservableObject
{
    private readonly WindowsOcrEngine _ocr = new();
    private readonly YandexVisionOcrEngine _yandex = new();
    private readonly TextCache _cache = new();
    private DocumentTextService? _textService;
    private CancellationTokenSource? _cts;
    private CaseReport? _report;

    public MainViewModel()
    {
        OcrAvailable = _ocr.IsAvailable;
        UseOcr = OcrAvailable;
        YandexAvailable = _yandex.IsAvailable;
        OcrInfo = OcrAvailable
            ? $"Локальный OCR: Windows.Media.Ocr ({string.Join(", ", WindowsOcrEngine.InstalledLanguages)})"
            : "Windows OCR без русского языка: установите «Русский» → «Распознавание текста» в параметрах языка";
    }

    public ObservableCollection<DocumentItem> Documents { get; } = [];
    public ObservableCollection<FieldItem> Fields { get; } = [];
    public ObservableCollection<CandidateItem> Candidates { get; } = [];
    public ObservableCollection<DamageRow> Damages { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private string _templatePath = "";
    [ObservableProperty] private string _outputFolder = "";
    [ObservableProperty] private bool _useOcr;
    [ObservableProperty] private bool _ocrAvailable;
    [ObservableProperty] private string _ocrInfo = "";

    /// <summary>Облачный Yandex Vision вместо локального OCR (только при заданных ключах YANDEX_*).</summary>
    [ObservableProperty] private bool _useYandex;
    [ObservableProperty] private bool _yandexAvailable;
    [ObservableProperty] private string _status = "Выберите папку заявки";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _summary = "";

    [ObservableProperty] private FieldItem? _selectedField;
    [ObservableProperty] private CandidateItem? _selectedCandidate;
    [ObservableProperty] private DamageRow? _selectedDamage;
    [ObservableProperty] private DocumentItem? _selectedDocument;

    [ObservableProperty] private BitmapSource? _previewImage;
    [ObservableProperty] private string _previewCaption = "";

    /// <summary>Положение подсветки в долях изображения (для прокрутки к найденному фрагменту).</summary>
    [ObservableProperty] private Rect _previewFocus;

    public bool HasReport => _report is not null;

    [RelayCommand]
    private void Browse()
    {
        var dlg = new OpenFolderDialog { Title = "Папка заявки", Multiselect = false };
        if (Directory.Exists(Folder)) dlg.InitialDirectory = Folder;
        if (dlg.ShowDialog() != true) return;
        OpenFolder(dlg.FolderName);
    }

    /// <summary>Открыть папку заявки и сразу обработать её.</summary>
    public void OpenFolder(string folder)
    {
        Folder = folder;
        OutputFolder = Path.Combine(Folder, "AutoParser");
        TemplatePath = TemplateLocator.Find(Core.Cases.CaseFolderScanner.ExpertiseKind(Folder)) ?? "";
        ProcessCommand.Execute(null);
    }

    [RelayCommand]
    private void BrowseTemplate()
    {
        var dlg = new OpenFileDialog { Filter = "Шаблон Word (*.docx)|*.docx", Title = "Шаблон заключения" };
        if (dlg.ShowDialog() == true) TemplatePath = dlg.FileName;
    }

    [RelayCommand]
    private async Task ProcessAsync()
    {
        if (!Directory.Exists(Folder)) { Status = "Папка не найдена"; return; }
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Log.Clear(); Documents.Clear(); Fields.Clear(); Candidates.Clear(); Damages.Clear();
        PreviewImage = null; PreviewCaption = "";
        IOcrEngine engine = UseYandex && YandexAvailable ? _yandex : _ocr;
        _textService = new DocumentTextService(UseOcr || UseYandex ? engine : null, _cache);
        var progress = new Progress<string>(s => { Status = s; Log.Add(s); });
        try
        {
            var processor = new CaseProcessor(_textService);
            var folder = Folder;
            var ct = _cts.Token;
            _report = await Task.Run(() => processor.ProcessAsync(folder, progress, ct), ct);
            Show(_report);
            if (string.IsNullOrEmpty(TemplatePath)) TemplatePath = TemplateLocator.Find(_report.ExpertiseKind) ?? "";
        }
        catch (OperationCanceledException) { Status = "Остановлено"; }
        catch (Exception ex) { Status = "Ошибка: " + ex.Message; Log.Add(ex.ToString()); }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasReport));
            ExportDocxCommand.NotifyCanExecuteChanged();
            ExportXlsxCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private void Show(CaseReport report)
    {
        foreach (var d in report.Documents) Documents.Add(new DocumentItem(d));
        foreach (var k in FieldInfo.ReportFields) Fields.Add(new FieldItem(report[k]));
        foreach (var d in report.Damages) Damages.Add(d);
        var found = Fields.Count(f => f.Field.Status != FieldStatus.Missing);
        var check = Fields.Count(f => f.Field.Status is FieldStatus.Check or FieldStatus.Conflict);
        Summary = $"Поля: найдено {found}/{Fields.Count}, требуют проверки {check} · Повреждения: {Damages.Count} · Документы: {report.Documents.Count(d => d.SkipReason is null)}/{report.Documents.Count}";
        Status = "Готово. Проверьте жёлтые и красные строки, при необходимости исправьте значения.";
    }

    partial void OnSelectedFieldChanged(FieldItem? value)
    {
        Candidates.Clear();
        if (value is null) return;
        foreach (var c in value.Field.Candidates) Candidates.Add(new CandidateItem(c));
        ShowSource(value.Field.Source, value.Title);
    }

    partial void OnSelectedCandidateChanged(CandidateItem? value)
    {
        if (value is not null) ShowSource(value.Candidate.Source, $"Вариант «{value.Value}»");
    }

    partial void OnSelectedDamageChanged(DamageRow? value)
    {
        if (value is not null) ShowSource(value.Source, value.Element);
    }

    partial void OnSelectedDocumentChanged(DocumentItem? value)
    {
        if (value?.Document.Text is { Pages.Count: > 0 } t)
            ShowSource(new SourceRef(value.Document.FilePath, t.Pages[0].PageNumber, null, t.Pages[0].Origin), value.Name);
    }

    [RelayCommand]
    private void ApplyCandidate()
    {
        if (SelectedField is null || SelectedCandidate is null) return;
        SelectedField.ApplyCandidate(SelectedCandidate.Candidate);
    }

    /// <summary>Рендер страницы-источника с рамкой вокруг найденного значения.</summary>
    private void ShowSource(SourceRef? src, string caption)
    {
        if (src is null || src.Page <= 0 || _report is null || _textService is null)
        {
            PreviewImage = null;
            PreviewCaption = src is null ? "" : $"{caption}: {src}";
            return;
        }
        var doc = _report.Documents.FirstOrDefault(d => d.FilePath == src.FilePath);
        var page = doc?.Text?.Pages.FirstOrDefault(p => p.PageNumber == src.Page);
        if (page is null) return;
        try
        {
            var png = _textService.GetPageImage(src.FilePath, page);
            var (w, h) = ImagePreprocessor.GetSize(png);
            var scale = page.Width > 0 ? w / page.Width : 1;
            if (src.Box is { } box)
            {
                png = ImagePreprocessor.DrawHighlight(png, box, scale);
                PreviewFocus = new Rect(box.X * scale / w, box.Y * scale / h, box.Width * scale / w, box.Height * scale / h);
            }
            else PreviewFocus = Rect.Empty;
            PreviewImage = ToBitmap(png);
            PreviewCaption = $"{caption} — {src}";
        }
        catch (Exception ex)
        {
            PreviewImage = null;
            PreviewCaption = $"Не удалось показать страницу: {ex.Message}";
        }
    }

    private static BitmapSource ToBitmap(byte[] png)
    {
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(png);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    [RelayCommand]
    private void AddDamage()
    {
        var row = new DamageRow { No = Damages.Count + 1, Confidence = 1 };
        Damages.Add(row);
        SelectedDamage = row;
    }

    [RelayCommand]
    private void RemoveDamage()
    {
        if (SelectedDamage is null) return;
        Damages.Remove(SelectedDamage);
        Renumber();
    }

    private void Renumber()
    {
        for (int i = 0; i < Damages.Count; i++) Damages[i].No = i + 1;
        var copy = Damages.ToList();
        Damages.Clear();
        foreach (var d in copy) Damages.Add(d);
    }

    private bool CanExport() => _report is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportDocx()
    {
        if (_report is null) return;
        if (!File.Exists(TemplatePath)) { MessageBox.Show("Укажите шаблон заключения (docx).", "AutoParser"); return; }
        SyncDamages();
        var path = DocxTemplateFiller.Fill(TemplatePath, _report, OutputFolder);
        Status = $"Отчёт сохранён: {path}";
        Open(path);
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private void ExportXlsx()
    {
        if (_report is null) return;
        SyncDamages();
        var path = DamageXlsxExporter.Export(_report, Path.Combine(OutputFolder, ReportNaming.BaseName(_report) + "_повреждения.xlsx"));
        Status = $"Таблица повреждений сохранена: {path}";
        Open(path);
    }

    [RelayCommand]
    private void OpenOutput()
    {
        if (Directory.Exists(OutputFolder)) Open(OutputFolder);
    }

    [RelayCommand]
    private void ClearCache()
    {
        _cache.Clear();
        Status = "Кэш OCR очищен";
    }

    private void SyncDamages()
    {
        if (_report is null) return;
        for (int i = 0; i < Damages.Count; i++) Damages[i].No = i + 1;
        _report.Damages = Damages.ToList();
    }

    private static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { /* нет ассоциации — не критично */ }
    }

    partial void OnIsBusyChanged(bool value)
    {
        ExportDocxCommand.NotifyCanExecuteChanged();
        ExportXlsxCommand.NotifyCanExecuteChanged();
    }
}
