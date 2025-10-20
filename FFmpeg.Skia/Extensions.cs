using FFmpeg.Images;
using AVFrame = FFmpeg.Utils.AVFrame;

namespace FFmpeg.Skia;
public static class Extensions
{
    public static bool IsSupportedSkiaColorType(this PixelFormat pixFmt) => pixFmt switch
    {
        PixelFormat.BGRA => true,
        PixelFormat.Gray8 => true,
        PixelFormat.RGBX => true,
        PixelFormat.RGBA => true,
        PixelFormat.RGB565LE => true,
        PixelFormat.RGBAF16LE => true,
        PixelFormat.RGBAF32LE => true,
        _ => false,
    };

    public static bool IsSupportedFFmpegFormat(this SKColorType pixelFormat) => pixelFormat switch
    {
        SkiaSharp.SKColorType.Bgra8888 => true,
        SkiaSharp.SKColorType.Rgb888x => true,
        SkiaSharp.SKColorType.Rgba8888 => true,
        SkiaSharp.SKColorType.Rgb565 => true,
        SkiaSharp.SKColorType.Gray8 => true,
        SkiaSharp.SKColorType.RgbaF16 => true,
        SkiaSharp.SKColorType.RgbaF32 => true,
        _ => false,
    };

    public static PixelFormat ToPixelFormat(this SKColorType pixelFormat) => pixelFormat switch
    {
        SkiaSharp.SKColorType.Bgra8888 => PixelFormat.BGRA,
        SkiaSharp.SKColorType.Rgb888x => PixelFormat.RGBX,
        SkiaSharp.SKColorType.Rgba8888 => PixelFormat.RGBA,
        SkiaSharp.SKColorType.Unknown => PixelFormat.None,
        SkiaSharp.SKColorType.Rgb565 => PixelFormat.RGB565LE,
        SkiaSharp.SKColorType.Gray8 => PixelFormat.Gray8,
        SkiaSharp.SKColorType.RgbaF16 => PixelFormat.RGBAF16LE,
        SkiaSharp.SKColorType.RgbaF32 => PixelFormat.RGBAF32LE,

        SkiaSharp.SKColorType.Alpha8 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Argb4444 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Rgba1010102 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Rgb101010x => throw new NotSupportedException(),

        SkiaSharp.SKColorType.RgbaF16Clamped => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Rg88 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.AlphaF16 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.RgF16 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Alpha16 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Rg1616 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Rgba16161616 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Bgra1010102 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Bgr101010x => throw new NotSupportedException(),
        _ => throw new NotSupportedException(),
    };

    public static SKColorType ToSkiaColorType(this PixelFormat pixelFormat) => pixelFormat switch
    {
        PixelFormat.BGRA => SkiaSharp.SKColorType.Bgra8888,
        PixelFormat.RGBX => SkiaSharp.SKColorType.Rgb888x,
        PixelFormat.RGBA => SkiaSharp.SKColorType.Rgba8888,
        PixelFormat.None => SkiaSharp.SKColorType.Unknown,
        PixelFormat.RGB565LE => SkiaSharp.SKColorType.Rgb565,
        PixelFormat.Gray8 => SkiaSharp.SKColorType.Gray8,
        PixelFormat.RGBAF16LE => SkiaSharp.SKColorType.RgbaF16,
        PixelFormat.RGBAF32LE => SkiaSharp.SKColorType.RgbaF32,
        _ => throw new NotSupportedException(),
    };

    public static SKImage ToSkiaImage(this AVFrame frame)
    {
        PixelFormat pixfmt = frame.PixelFormat;
        int width = frame.Width;
        int height = frame.Height;
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Invalid image size");
        SKColorType colorType = pixfmt.IsSupportedSkiaColorType() ? pixfmt.ToSkiaColorType() : SKImageInfo.PlatformColorType;
        if (!colorType.IsSupportedFFmpegFormat()) colorType = SKColorType.Rgba8888; // make sure that platform color type is correct
        bool justCopy = colorType.ToPixelFormat() == pixfmt;
        SKImageInfo info = new(width, height, colorType);

        using SKImage skImage = SKImage.Create(info);
        using var pixmap = skImage.PeekPixels();

        if (justCopy)
            CopyFrame(frame, pixmap);
        else
            Images.SwsContext.Convert(frame, pixmap.GetPixels(), new(width, height, colorType.ToPixelFormat()), Images.SwsAlgorithm.FastBilinear()).ThrowIfError();
        return skImage.Subset(frame.CroppedRect());
    }

    public static SKRectI CroppedRect(this AVFrame frame) => new SKRectI((int)frame.CropLeft, (int)frame.CropTop, frame.CroppedWidth + (int)frame.CropLeft, frame.CroppedHeight + (int)frame.CropBottom);

    public static SKBitmap ToSkiaBitmap(this AVFrame frame)
    {
        PixelFormat pixfmt = frame.PixelFormat;
        int width = frame.Width;
        int height = frame.Height;
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Invalid image size");
        SKColorType colorType = pixfmt.IsSupportedSkiaColorType() ? pixfmt.ToSkiaColorType() : SKImageInfo.PlatformColorType;
        if (!colorType.IsSupportedFFmpegFormat()) colorType = SKColorType.Rgba8888; // make sure that platform color type is correct
        bool justCopy = colorType.ToPixelFormat() == pixfmt;
        SKImageInfo info = new(width, height, colorType);

        SKBitmap skImage = new(info);
        try
        {
            using var pixmap = skImage.PeekPixels();
            if (justCopy)
                CopyFrame(frame, pixmap);
            else
                Images.SwsContext.Convert(frame, pixmap.GetPixels(), new(width, height, colorType.ToPixelFormat()), Images.SwsAlgorithm.FastBilinear()).ThrowIfError();
            SKBitmap croppedImage = new();
            skImage.ExtractSubset(croppedImage, frame.CroppedRect());
            skImage.Dispose();
            return croppedImage;
        }
        catch
        {
            skImage.Dispose();
            throw;
        }
    }


    public static SKImage ToSkiaImage(this AVFrame frame, SKColorType colorType = SKColorType.Unknown)
    {
        if (colorType == SKColorType.Unknown) return ToSkiaImage(frame);
        var srcFormat = frame.PixelFormat;
        if (srcFormat.IsSupportedSkiaColorType() && srcFormat.ToSkiaColorType() == colorType) return ToSkiaImage(frame);
        if (frame.Width < 0 || frame.Height < 0) throw new ArgumentException();
        SKImageInfo info = new(frame.Width, frame.Height, colorType);
        SKImage skImage = SKImage.Create(info);
        try
        {
            using var pixmap = skImage.PeekPixels();
            Images.SwsContext.Convert(frame, pixmap.GetPixels(), new(pixmap.Width, pixmap.Height, colorType.ToPixelFormat()), Images.SwsAlgorithm.FastBilinear()).ThrowIfError();
            var image = skImage.Subset(frame.CroppedRect());
            skImage.Dispose();
            return image;
        }
        catch
        {
            skImage.Dispose();
            throw;
        }
    }

    public static SKBitmap ToSkiaBitmap(this AVFrame frame, SKColorType colorType = SKColorType.Unknown)
    {
        if (colorType == SKColorType.Unknown) return ToSkiaBitmap(frame);
        var srcFormat = frame.PixelFormat;
        if (srcFormat.IsSupportedSkiaColorType() && srcFormat.ToSkiaColorType() == colorType) return ToSkiaBitmap(frame);
        if (frame.Width < 0 || frame.Height < 0) throw new ArgumentException();
        SKImageInfo info = new(frame.Width, frame.Height, colorType);
        using SKBitmap skImage = new(info);
        using var pixmap = skImage.PeekPixels();
        Images.SwsContext.Convert(frame, pixmap.GetPixels(), new(pixmap.Width, pixmap.Height, colorType.ToPixelFormat()), Images.SwsAlgorithm.FastBilinear()).ThrowIfError();
        SKBitmap bitmap = new();
        skImage.ExtractSubset(bitmap, frame.CroppedRect());
        return bitmap;

    }

    /// <summary>
    /// Creates a clone arround the given AVFrame as SKBitmap. If the frame is supported skia colortype, it will be cloned.
    /// </summary>
    /// <param name="frame"></param>
    /// <returns></returns>
    public static SKImage AsSKImage(this AVFrame frame)
    {
        if (frame.PixelFormat.IsSupportedSkiaColorType())
        {
            frame = frame.Clone(); // Clone the frame
            var cropRect = frame.CroppedRect();
            var info = new SKImageInfo(cropRect.Width, cropRect.Height, frame.PixelFormat.ToSkiaColorType());
            long byteSkippedLeft = (long)frame.CropLeft * info.BytesPerPixel;
            long byteSkippedTop = (long)frame.CropTop * info.RowBytes;
            SKPixmap pixmap = new(info, new IntPtr(frame.Data[0].ToInt64() + byteSkippedLeft + byteSkippedTop), frame.LineSize[0]);
            SKImage skImage = SKImage.FromPixels(pixmap, (ptr, obj) =>
            {
                (AVFrame frame, SKPixmap pixmap) = ((AVFrame, SKPixmap))obj;
                pixmap.Dispose();
                frame.Dispose();
            }, (frame, pixmap));
            return skImage;
        }
        else return ToSkiaImage(frame);
    }

    /// <summary>
    /// Creates a clone arround the given AVFrame as SKBitmap. If the frame is supported skia colortype, it will be cloned.
    /// </summary>
    /// <param name="frame"></param>
    /// <returns></returns>
    public static SKBitmap AsSKBitmap(this AVFrame frame)
    {
        if (frame.PixelFormat.IsSupportedSkiaColorType())
        {
            frame = frame.Clone(); // Clone the frame
            var cropRect = frame.CroppedRect();
            var info = new SKImageInfo(cropRect.Width, cropRect.Height, frame.PixelFormat.ToSkiaColorType());
            long byteSkippedLeft = (long)frame.CropLeft * info.BytesPerPixel;
            long byteSkippedTop = (long)frame.CropTop * info.RowBytes;
            SKBitmap bmp = new();
            bmp.InstallPixels(info, new IntPtr(frame.Data[0].ToInt64() + byteSkippedLeft + byteSkippedTop), frame.LineSize[0], (ptr, obj) =>
            {
                AVFrame frame = (AVFrame)obj;
                frame.Dispose();
            }, frame);
            return bmp;
        }
        else return frame.ToSkiaBitmap();
    }

    public unsafe static AVFrame ToAVFrame(this SKImage image)
    {
        if (!image.ColorType.IsSupportedFFmpegFormat()) throw new NotSupportedException();
        AVFrame frame = AVFrame.Allocate();
        try
        {
            frame.Width = image.Width;
            frame.Height = image.Height;
            frame.Format = (int)image.ColorType.ToPixelFormat();
            frame.GetBuffer().ThrowIfError();
            using var pixmap = image.PeekPixels();
            Buffer.MemoryCopy(pixmap.GetPixels().ToPointer(), (void*)frame.Data[0], pixmap.BytesSize, pixmap.BytesSize);
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    public unsafe static AVFrame ToAVFrame(this SKBitmap image)
    {
        if (!image.ColorType.IsSupportedFFmpegFormat()) throw new NotSupportedException();
        AVFrame frame = AVFrame.Allocate();
        try
        {
            frame.Width = image.Width;
            frame.Height = image.Height;
            frame.Format = (int)image.ColorType.ToPixelFormat();
            frame.GetBuffer().ThrowIfError();
            Buffer.MemoryCopy(image.GetPixels().ToPointer(), (void*)frame.Data[0], image.Info.BytesSize, image.Info.BytesSize);
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    public static AVFrame ToAVFrame(this SKImage image, PixelFormat targetFormat = PixelFormat.None)
    {
        if (!image.ColorType.IsSupportedFFmpegFormat()) throw new NotSupportedException();
        if (targetFormat == PixelFormat.None || targetFormat == image.ColorType.ToPixelFormat()) return ToAVFrame(image);
        AVFrame frame = AVFrame.Allocate();
        try
        {
            frame.Width = image.Width;
            frame.Height = image.Height;
            frame.Format = (int)targetFormat;
            using var pixmap = image.PeekPixels();
            Images.SwsContext.Convert(pixmap.GetPixels(), new Images.ImageInfo(image.Width, image.Height, image.Info.ColorType.ToPixelFormat()), frame, Images.SwsAlgorithm.FastBilinear()).ThrowIfError();

            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    public static AVFrame ToAVFrame(this SKBitmap image, PixelFormat targetFormat = PixelFormat.None)
    {
        if (!image.ColorType.IsSupportedFFmpegFormat()) throw new NotSupportedException();
        if (targetFormat == PixelFormat.None || targetFormat == image.ColorType.ToPixelFormat()) return ToAVFrame(image);
        AVFrame frame = AVFrame.Allocate();
        try
        {
            frame.Width = image.Width;
            frame.Height = image.Height;
            frame.Format = (int)targetFormat;
            Images.SwsContext.Convert(image.GetPixels(), new Images.ImageInfo(image.Width, image.Height, image.Info.ColorType.ToPixelFormat()), frame, Images.SwsAlgorithm.FastBilinear()).ThrowIfError();

            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    private static Images.SwsContext GetSwsContext(AVFrame frame, SKImageInfo info)
    {

        if (frame.Width == info.Width && frame.Height == info.Height)
            return new Images.SwsContext(frame.Width, frame.Height, frame.PixelFormat, info.Width, info.Height, info.ColorType.ToPixelFormat(), SwsAlgorithm.FastBilinear());
        else
            return new Images.SwsContext(frame.Width, frame.Height, frame.PixelFormat, info.Width, info.Height, info.ColorType.ToPixelFormat(), SwsAlgorithm.Bicubic());

    }
    public static void CopyTo(this AVFrame frame, SKBitmap bitmap)
    {
        if (CheckCopy(frame, bitmap.Info))
            Extensions.CopyFrame(frame, bitmap);
        else
        {
            using var swsContext = GetSwsContext(frame, bitmap.Info);
            swsContext.Convert(frame, bitmap.GetPixels()).ThrowIfError();
        }
        bitmap.NotifyPixelsChanged();
    }

    internal unsafe static void CopyFrame(AVFrame frame, SKPixmap pixmap)
    {
        if (pixmap.RowBytes == frame.LineSize[0])
            Buffer.MemoryCopy((void*)frame.Data[0], pixmap.GetPixels().ToPointer(), pixmap.BytesSize, pixmap.BytesSize);
        else
        {
            var imagePtr = (byte*)pixmap.GetPixels().ToPointer();
            for (int y = 0; y < pixmap.Height; y++)
            {
                var framePtr = frame.Data[0] + y * frame.LineSize[0];
                var pixPtr = imagePtr + y * pixmap.RowBytes;
                Buffer.MemoryCopy((void*)framePtr, pixPtr, pixmap.RowBytes, pixmap.BytesSize);
            }
        }
    }

    internal unsafe static void CopyFrame(AVFrame frame, SKBitmap bitmap)
    {
        if (bitmap.RowBytes == frame.LineSize[0])
            Buffer.MemoryCopy((void*)frame.Data[0], bitmap.GetPixels().ToPointer(), bitmap.Info.BytesSize, bitmap.Info.BytesSize);
        else
        {
            var imagePtr = (byte*)bitmap.GetPixels().ToPointer();
            for (int y = 0; y < bitmap.Height; y++)
            {
                var framePtr = frame.Data[0] + y * frame.LineSize[0];
                var pixPtr = imagePtr + y * bitmap.RowBytes;
                Buffer.MemoryCopy((void*)framePtr, pixPtr, bitmap.RowBytes, bitmap.Info.BytesSize);
            }
        }
    }

    private static bool CheckCopy(AVFrame frame, SKImageInfo info)
    {
        return frame.CroppedWidth == info.Width
            && frame.CroppedHeight == info.Height
            && frame.PixelFormat == info.ColorType.ToPixelFormat();
    }
}

