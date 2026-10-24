using PDFtoImage;

namespace AutoParser.Core.Text;

/// <summary>Рендер страниц PDF в PNG через PDFium.</summary>
public static class PdfRenderer
{
    public static byte[] RenderPng(string path, int pageNumber, int dpi = 300)
    {
        using var fs = File.OpenRead(path);
        using var bmp = Conversion.ToImage(fs, page: pageNumber - 1, options: new RenderOptions(Dpi: dpi, WithAnnotations: true));
        return ImagePreprocessor.EncodePng(bmp);
    }

    public static int GetPageCount(string path)
    {
        using var fs = File.OpenRead(path);
        return Conversion.GetPageCount(fs);
    }
}
