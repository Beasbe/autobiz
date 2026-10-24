using System.Text;
using AutoParser.Core.Export;
using AutoParser.Core.Model;
using AutoParser.Core.Pipeline;
using AutoParser.Core.Text;
using AutoParser.Ocr;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length < 2)
{
    Console.WriteLine("""
        autoparser process <папка заявки> [--out <папка>] [--template <docx>] [--no-ocr] [--ocr windows|linux|yandex|none] [--ocr-lang rus,eng] [--verbose]
            Разобрать заявку, вывести поля и сформировать отчёт (docx) и таблицу повреждений (xlsx).
        autoparser dump <файл|папка> [--no-ocr] [--ocr ...] [--words]
            Показать текст документов (текстовый слой / OCR).
        """);
    return 1;
}

string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();

var noOcr = args.Contains("--no-ocr");
var ocrChoice = Opt("--ocr");
IOcrEngine? ocr = noOcr ? null
    : ocrChoice == "yandex" ? new YandexVisionOcrEngine()
    : LocalOcr.Create(ocrChoice, Opt("--ocr-lang") ?? "rus");
if (ocr is { IsAvailable: false })
    Console.Error.WriteLine($"{ocr.Name} недоступен (нет русского языка Windows OCR, Tesseract или ключей Yandex) — сканы будут пропущены.");
var service = new DocumentTextService(ocr, new TextCache());

switch (args[0])
{
    case "dump":
    {
        var target = args[1];
        var files = Directory.Exists(target)
            ? Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Order().ToList()
            : [target];
        foreach (var f in files)
        {
            var doc = await service.LoadAsync(f, progress: new Progress<string>(s => Console.Error.WriteLine(s)));
            Console.WriteLine($"######## {f}  pages={doc.Pages.Count} {doc.Error}");
            foreach (var p in doc.Pages)
            {
                Console.WriteLine($"=== page {p.PageNumber} [{p.Origin} rot={p.Rotation} {p.Width:0}x{p.Height:0}]");
                foreach (var l in p.Lines)
                    Console.WriteLine(args.Contains("--words") ? $"[{l.Box}] {l.Text}" : l.Text);
            }
        }
        return 0;
    }
    case "preview":
    {
        // autoparser preview <папка> <поле> <out.png> — страница-источник поля с подсветкой (отладка превью).
        var processor = new CaseProcessor(service);
        var report = await processor.ProcessAsync(Path.GetFullPath(args[1]));
        var field = report.Fields.Values.First(f => f.Key.ToString() == args[2] || f.Title == args[2]);
        var src = field.Source!;
        var page = report.Documents.First(d => d.FilePath == src.FilePath).Text!.Pages.First(p => p.PageNumber == src.Page);
        var png = service.GetPageImage(src.FilePath, page);
        var (w, _) = ImagePreprocessor.GetSize(png);
        if (src.Box is { } box) png = ImagePreprocessor.DrawHighlight(png, box, w / page.Width);
        File.WriteAllBytes(args[3], png);
        Console.WriteLine($"{field.Title}: {field.Value} — {src}");
        return 0;
    }
    case "process":
    {
        var folder = Path.GetFullPath(args[1]);
        var verbose = args.Contains("--verbose");
        var processor = new CaseProcessor(service);
        var report = await processor.ProcessAsync(folder, new Progress<string>(s => { if (verbose) Console.Error.WriteLine(s); }));

        Console.WriteLine($"Заявка: {folder}");
        Console.WriteLine("Документы:");
        foreach (var d in report.Documents)
            Console.WriteLine($"  {DocumentTypeInfo.Title(d.Type),-28} {d.RelativePath}{(d.SkipReason is null ? "" : $"  ({d.SkipReason})")}");
        Console.WriteLine();
        Console.WriteLine("Поля:");
        foreach (var k in FieldInfo.ReportFields)
        {
            var f = report.Fields[k];
            Console.WriteLine($"  {f.Title,-38} {f.Value,-45} {f.Status,-8} {f.Confidence,4:P0}  {f.Source}{(f.Note is null ? "" : $"  // {f.Note}")}");
            if (verbose)
                foreach (var c in f.Candidates.Take(k == FieldKey.Paint ? 30 : 6))
                    Console.WriteLine($"      · {c.Key} {c.Value} ({c.Confidence:0.00}, {DocumentTypeInfo.Title(c.DocumentType)}, {c.Source.FileName} стр.{c.Source.Page}, {c.Parser}) «{c.Source.Snippet}»");
        }
        Console.WriteLine();
        Console.WriteLine("Повреждения:");
        foreach (var r in report.Damages)
            Console.WriteLine($"  {r.No,2}. {r.Element,-50} {r.Conclusion,-30} [{r.RawText}] {r.Source}");

        var outDir = Opt("--out") ?? Path.Combine(folder, "AutoParser");
        Directory.CreateDirectory(outDir);
        var template = Opt("--template") ?? TemplateLocator.Find(report.ExpertiseKind);
        if (template is not null)
        {
            var docx = DocxTemplateFiller.Fill(template, report, outDir);
            Console.WriteLine($"\nОтчёт: {docx}");
        }
        else Console.WriteLine("\nШаблон docx не найден (укажите --template).");
        var xlsx = DamageXlsxExporter.Export(report, Path.Combine(outDir, ReportNaming.BaseName(report) + "_повреждения.xlsx"));
        Console.WriteLine($"Таблица повреждений: {xlsx}");
        return 0;
    }
}
return 1;
