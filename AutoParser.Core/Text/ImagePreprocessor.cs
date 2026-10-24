using SkiaSharp;

namespace AutoParser.Core.Text;

/// <summary>Подготовка изображений к OCR: ориентация по EXIF, масштаб, оттенки серого, контраст, повороты.</summary>
public static class ImagePreprocessor
{
    /// <summary>Максимальная сторона изображения для OCR.</summary>
    public const int MaxSide = 4000;

    public static byte[] EncodePng(SKBitmap bmp)
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public static SKBitmap Decode(byte[] bytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes))
            ?? throw new InvalidDataException("Неподдерживаемый формат изображения");
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(info);
        codec.GetPixels(bmp.Info, bmp.GetPixels());
        return ApplyOrientation(bmp, codec.EncodedOrigin);
    }

    public static (int Width, int Height) GetSize(byte[] bytes)
    {
        using var codec = SKCodec.Create(new SKMemoryStream(bytes));
        if (codec is null) return (0, 0);
        bool swap = codec.EncodedOrigin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom
            or SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightBottom;
        return swap ? (codec.Info.Height, codec.Info.Width) : (codec.Info.Width, codec.Info.Height);
    }

    /// <summary>
    /// Нормализует изображение для OCR: EXIF-ориентация, поворот, масштаб, серый, растяжение контраста.
    /// Возвращает PNG и коэффициент масштаба относительно исходника (после поворота).
    /// </summary>
    public static (byte[] Png, double Scale) Prepare(byte[] bytes, int rotation = 0)
    {
        using var src = Decode(bytes);
        using var rotated = Rotate(src, rotation);
        var (scaled, k) = ScaleForOcr(rotated);
        using (scaled)
        using (var gray = ToGrayStretched(scaled))
            return (EncodePng(gray), k);
    }

    public static SKBitmap Rotate(SKBitmap src, int degrees)
    {
        degrees = ((degrees % 360) + 360) % 360;
        if (degrees == 0) return src.Copy();
        bool swap = degrees is 90 or 270;
        var dst = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height);
        using var c = new SKCanvas(dst);
        c.Clear(SKColors.White);
        c.Translate(dst.Width / 2f, dst.Height / 2f);
        c.RotateDegrees(degrees);
        c.Translate(-src.Width / 2f, -src.Height / 2f);
        c.DrawImage(SKImage.FromBitmap(src), 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        return dst;
    }

    /// <summary>Уменьшает кадр, если его длинная сторона больше <paramref name="maxSide"/> (PNG без изменений — возвращается как есть).</summary>
    public static byte[] LimitLongSide(byte[] bytes, int maxSide)
    {
        var (w, h) = GetSize(bytes);
        int longSide = Math.Max(w, h);
        if (longSide <= maxSide || longSide == 0) return bytes;
        using var src = Decode(bytes);
        double k = (double)maxSide / longSide;
        using var scaled = src.Resize(new SKImageInfo(Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k))), new SKSamplingOptions(SKCubicResampler.Mitchell));
        return EncodePng(scaled);
    }

    /// <summary>Поворот на произвольный угол против перекоса (angle — найденный наклон текста).</summary>
    public static byte[] Deskew(byte[] png, double angle)
    {
        using var src = Decode(png);
        using var dst = new SKBitmap(src.Width, src.Height);
        using (var c = new SKCanvas(dst))
        {
            c.Clear(SKColors.White);
            c.Translate(src.Width / 2f, src.Height / 2f);
            c.RotateDegrees((float)-angle);
            c.Translate(-src.Width / 2f, -src.Height / 2f);
            c.DrawImage(SKImage.FromBitmap(src), 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        }
        return EncodePng(dst);
    }

    /// <summary>Рисует рамку вокруг найденного фрагмента (координаты страницы × scale = пиксели изображения).</summary>
    public static byte[] DrawHighlight(byte[] png, BBox box, double scale)
    {
        using var bmp = Decode(png);
        using (var c = new SKCanvas(bmp))
        {
            var r = SKRect.Create((float)(box.X * scale) - 6, (float)(box.Y * scale) - 6,
                (float)(box.Width * scale) + 12, (float)(box.Height * scale) + 12);
            using var fill = new SKPaint { Color = new SKColor(255, 200, 0, 60), Style = SKPaintStyle.Fill };
            using var stroke = new SKPaint { Color = new SKColor(230, 60, 0), Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(3, bmp.Width / 400f), IsAntialias = true };
            c.DrawRect(r, fill);
            c.DrawRect(r, stroke);
        }
        return EncodePng(bmp);
    }

    /// <summary>Вырезает фрагмент изображения (для превью найденного значения).</summary>
    public static byte[] Crop(byte[] bytes, BBox box, int padding = 10)
    {
        using var src = Decode(bytes);
        var rect = SKRectI.Create(
            Math.Max(0, (int)box.X - padding), Math.Max(0, (int)box.Y - padding),
            (int)box.Width + padding * 2, (int)box.Height + padding * 2);
        rect.Intersect(SKRectI.Create(0, 0, src.Width, src.Height));
        using var dst = new SKBitmap(rect.Width, rect.Height);
        src.ExtractSubset(dst, rect);
        return EncodePng(dst);
    }

    private static (SKBitmap Bitmap, double Scale) ScaleForOcr(SKBitmap src)
    {
        var maxSide = Math.Max(src.Width, src.Height);
        double k = 1;
        if (maxSide > MaxSide) k = (double)MaxSide / maxSide;
        else if (maxSide < 1800) k = 1800.0 / maxSide;
        if (Math.Abs(k - 1) < 0.01) return (src.Copy(), 1);
        var info = new SKImageInfo(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)));
        return (src.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell)), k);
    }

    private static SKBitmap ToGrayStretched(SKBitmap src)
    {
        var w = src.Width; var h = src.Height;
        var gray = new byte[w * h];
        var hist = new int[256];
        var pixels = src.Pixels;
        for (int i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            var a = p.Alpha / 255.0;
            var v = (byte)(255 - a * (255 - (0.299 * p.Red + 0.587 * p.Green + 0.114 * p.Blue)));
            gray[i] = v;
            hist[v]++;
        }
        // Растяжение контраста по 1% / 99% перцентилям.
        int lo = Percentile(hist, gray.Length, 0.01), hi = Percentile(hist, gray.Length, 0.99);
        if (hi - lo < 30) { lo = 0; hi = 255; }
        var lut = new byte[256];
        for (int v = 0; v < 256; v++)
            lut[v] = (byte)Math.Clamp((v - lo) * 255.0 / (hi - lo), 0, 255);

        var dst = new SKBitmap(new SKImageInfo(w, h, SKColorType.Gray8, SKAlphaType.Opaque));
        unsafe
        {
            var ptr = (byte*)dst.GetPixels();
            var stride = dst.RowBytes;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    ptr[y * stride + x] = lut[gray[y * w + x]];
        }
        return dst;
    }

    private static int Percentile(int[] hist, int total, double q)
    {
        long acc = 0; var target = total * q;
        for (int i = 0; i < 256; i++) { acc += hist[i]; if (acc >= target) return i; }
        return 255;
    }

    private static SKBitmap ApplyOrientation(SKBitmap bmp, SKEncodedOrigin origin)
    {
        int deg = origin switch
        {
            SKEncodedOrigin.RightTop => 90,
            SKEncodedOrigin.BottomRight => 180,
            SKEncodedOrigin.LeftBottom => 270,
            _ => 0,
        };
        if (deg == 0) return bmp;
        var r = Rotate(bmp, deg);
        bmp.Dispose();
        return r;
    }
}
