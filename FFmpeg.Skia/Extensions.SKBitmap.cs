using FFmpeg.Images;
using FFmpeg.Utils;
using static System.Net.Mime.MediaTypeNames;

namespace FFmpeg.Skia;


/// <summary>
/// Provides extension methods for interoperating between FFmpeg bmp types and
/// SkiaSharp.
/// </summary>
/// <remarks>
/// <para>
/// This class contains helper methods for converting between FFmpeg
/// <see cref="PixelFormat"/> values and SkiaSharp <see cref="SKColorType"/>
/// values, as well as methods for converting bmp data between
/// <see cref="AVFrame"/>, <see cref="SKImage"/>, and <see cref="SKBitmap"/>.
/// </para>
/// <para>
/// When the source and destination pixel formats are compatible, bmp data is
/// copied directly without conversion. Otherwise, pixel format conversion and
/// bmp scaling are performed using <see cref="Images.SwsContext"/>.
/// </para>
/// <para>
/// Methods prefixed with <c>To</c> create independent copies of the bmp data,
/// while methods prefixed with <c>As</c> create Skia objects that share the
/// underlying pixel buffer with a cloned <see cref="AVFrame"/>, avoiding an
/// additional memory copy whenever possible.
/// </para>
/// <para>
/// Helper methods are also provided to determine whether a
/// <see cref="PixelFormat"/> or <see cref="SKColorType"/> has an equivalent
/// representation in the other library and to select the closest compatible
/// pixel format when no direct mapping exists.
/// </para>
/// </remarks>
public static partial class Extensions
{
    /// <summary>
    /// Creates a new <see cref="SKBitmap"/> from an <see cref="AVFrame"/>.
    /// </summary>
    /// <param name="frame">
    /// The source video frame.
    /// </param>
    /// <returns>
    /// A new <see cref="SKBitmap"/> containing the frame data.
    /// </returns>
    /// <remarks>
    /// <para>
    /// If the frame's pixel format is directly supported by Skia, the pixel data is
    /// copied without conversion.
    /// </para>
    /// <para>
    /// Otherwise, the frame is converted to a compatible Skia color type using
    /// <see cref="SwsContext"/>.
    /// </para>
    /// <para>
    /// If the frame contains cropping information, the returned bmp contains only
    /// the visible region.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The frame does not contain valid bmp dimensions.
    /// </exception>
    public static SKBitmap ToSKBitmap(this AVFrame frame)
    {
        PixelFormat pixfmt = frame.PixelFormat;
        int width = frame.Width;
        int height = frame.Height;
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Invalid image size");
        SKColorType colorType = pixfmt.HasSkiaEquivalent() ? pixfmt.ToSkiaColorType() : SKImageInfo.PlatformColorType;
        if (!colorType.HasFFmpegEquivalent())
            colorType = SKColorType.Rgba8888; // make sure that platform color type is correct
        bool justCopy = colorType.ToPixelFormat() == pixfmt;
        SKImageInfo info = new(width, height, colorType, SKAlphaType.Unpremul);

        SKBitmap skImage = new(info);
        try
        {
            using SKPixmap pixmap = skImage.PeekPixels();
            if (justCopy)
                CopyFrame(frame, pixmap);
            else
                Images.SwsContext.Convert(frame, pixmap.GetPixels(), new(width, height, colorType.ToPixelFormat()), Images.SwsAlgorithm.FastBilinear()).ThrowIfError();
            SKBitmap croppedImage;
            if (!frame.HasCrop())
                return skImage;
            croppedImage = new();
            _ = skImage.ExtractSubset(croppedImage, frame.CroppedRect());
            skImage.Dispose();
            return croppedImage;
        }
        catch
        {
            skImage.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a new <see cref="SKBitmap"/> from an <see cref="AVFrame"/> using the
    /// specified Skia color type.
    /// </summary>
    /// <param name="frame">
    /// The source video frame.
    /// </param>
    /// <param name="colorType">
    /// The desired color type of the resulting bmp. If
    /// <see cref="SKColorType.Unknown"/> is specified, the most appropriate Skia
    /// color type is selected automatically.
    /// </param>
    /// <returns>
    /// A new <see cref="SKBitmap"/> containing the converted frame.
    /// </returns>
    /// <remarks>
    /// If the requested color type already matches the frame's pixel format, no
    /// pixel format conversion is performed.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The frame does not contain valid bmp dimensions.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The specified color type has no equivalent FFmpeg pixel format.
    /// </exception>
    public static SKBitmap ToSKBitmap(this AVFrame frame, SKColorType colorType = SKColorType.Unknown)
    {
        if (colorType == SKColorType.Unknown)
            return ToSKBitmap(frame);
        PixelFormat srcFormat = frame.PixelFormat;
        if (srcFormat.HasSkiaEquivalent() && srcFormat.ToSkiaColorType() == colorType)
            return ToSKBitmap(frame);
        if (frame.Width < 0 || frame.Height < 0)
            throw new ArgumentException();
        SKImageInfo info = new(frame.Width, frame.Height, colorType, SKAlphaType.Unpremul);
        SKBitmap skImage = new(info);
        try
        {
            using SKPixmap pixmap = skImage.PeekPixels();
            Images.SwsContext.Convert(frame, pixmap.GetPixels(), new(pixmap.Width, pixmap.Height, colorType.ToPixelFormat()), Images.SwsAlgorithm.FastBilinear()).ThrowIfError();
            if (!frame.HasCrop())
                return skImage;
            SKBitmap bitmap = new();
            _ = skImage.ExtractSubset(bitmap, frame.CroppedRect());
            skImage.Dispose();
            return bitmap;
        }
        catch
        {
            skImage.Dispose();
            throw;
        }

    }

    /// <summary>
    /// Creates an <see cref="SKBitmap"/> that shares the pixel buffer of the supplied
    /// <see cref="AVFrame"/> whenever possible.
    /// </summary>
    /// <param name="frame">
    /// The source video frame.
    /// </param>
    /// <returns>
    /// An <see cref="SKBitmap"/> representing the frame.
    /// </returns>
    /// <remarks>
    /// <para>
    /// If the frame uses a pixel format that is directly supported by Skia, no pixel
    /// data is copied. Instead, the frame is cloned and the returned bmp
    /// references the cloned frame's buffer.
    /// </para>
    /// <para>
    /// The cloned frame remains alive until the returned <see cref="SKBitmap"/> is
    /// disposed.
    /// </para>
    /// <para>
    /// If the frame's pixel format is not supported by Skia, this method falls back
    /// to <see cref="ToSKBitmap(AVFrame)"/>, which performs a pixel format
    /// conversion and copies the bmp data.
    /// </para>
    /// </remarks>
    public static SKBitmap AsSKBitmap(this AVFrame frame)
    {
        if (frame.PixelFormat.HasSkiaEquivalent())
        {
            frame = frame.Clone(); // Clone the frame
            SKRectI cropRect = frame.CroppedRect();
            SKImageInfo info = new(cropRect.Width, cropRect.Height, frame.PixelFormat.ToSkiaColorType());
            long byteSkippedLeft = (long)frame.CropLeft * info.BytesPerPixel;
            long byteSkippedTop = (long)frame.CropTop * info.RowBytes;
            SKBitmap bmp = new();
            _ = bmp.InstallPixels(info, new IntPtr(frame.Data[0].ToInt64() + byteSkippedLeft + byteSkippedTop), frame.LineSize[0], (ptr, obj) =>
            {
                AVFrame frame = (AVFrame)obj;
                frame.Dispose();
            }, frame);
            return bmp;
        }
        else
            return frame.ToSKBitmap();
    }

    /// <summary>
    /// Replaces the pixel data of this <see cref="SKBitmap"/> with the supplied
    /// <see cref="AVFrame"/>, sharing the frame's pixel buffer whenever possible.
    /// </summary>
    /// <param name="bmp">
    /// The destination bitmap.
    /// </param>
    /// <param name="frame">
    /// The source video frame.
    /// </param>
    /// <remarks>
    /// <para>
    /// If the frame uses a pixel format that is directly supported by Skia, no pixel
    /// data is copied. Instead, the frame is cloned and the bitmap references the
    /// cloned frame's buffer.
    /// </para>
    /// <para>
    /// The cloned frame remains alive until the bitmap is reset or disposed.
    /// </para>
    /// <para>
    /// If the frame's pixel format is not supported by Skia, this method falls back
    /// to <see cref="CopyTo(AVFrame,SKBitmap)"/>, which performs a pixel format
    /// conversion and copies the converted pixel data into the bitmap.
    /// </para>
    /// </remarks>
    public static void Reference(this SKBitmap bmp, AVFrame frame)
    {
        if (frame.PixelFormat.HasSkiaEquivalent())
        {
            bmp.Reset();
            frame = frame.Clone(); // Clone the frame
            SKRectI cropRect = frame.CroppedRect();
            SKImageInfo info = new(cropRect.Width, cropRect.Height, frame.PixelFormat.ToSkiaColorType());
            long byteSkippedLeft = (long)frame.CropLeft * info.BytesPerPixel;
            long byteSkippedTop = (long)frame.CropTop * info.RowBytes;
            _ = bmp.InstallPixels(info, new IntPtr(frame.Data[0].ToInt64() + byteSkippedLeft + byteSkippedTop), frame.LineSize[0], (ptr, obj) =>
            {
                AVFrame frame = (AVFrame)obj;
                frame.Dispose();
            }, frame);
        }
        else
            frame.CopyTo(bmp);
    }

    /// <summary>
    /// Creates a new <see cref="AVFrame"/> from an <see cref="SKBitmap"/>.
    /// </summary>
    /// <param name="image">
    /// The source bmp.
    /// </param>
    /// <returns>
    /// A newly allocated frame containing the bmp data.
    /// </returns>
    /// <remarks>
    /// The resulting frame uses the FFmpeg pixel format corresponding to the
    /// bmp's <see cref="SKColorType"/>.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The bmp's color type has no equivalent FFmpeg pixel format.
    /// </exception>
    public static unsafe AVFrame ToAVFrame(this SKBitmap image)
    {
        if (!image.ColorType.HasFFmpegEquivalent())
            throw new NotSupportedException();
        AVFrame frame = AVFrame.Allocate();
        try
        {
            frame.Width = image.Width;
            frame.Height = image.Height;
            frame.Format = (int)image.ColorType.ToPixelFormat();
            frame.CreateBuffer().ThrowIfError();
            Buffer.MemoryCopy(image.GetPixels().ToPointer(), (void*)frame.Data[0], image.Info.BytesSize, image.Info.BytesSize);
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a new <see cref="AVFrame"/> from an <see cref="SKBitmap"/> using the
    /// specified pixel format.
    /// </summary>
    /// <param name="image">
    /// The source bmp.
    /// </param>
    /// <param name="targetFormat">
    /// The desired FFmpeg pixel format. Specify <see cref="PixelFormat.None"/> to
    /// preserve the bmp's native format.
    /// </param>
    /// <returns>
    /// A newly allocated frame containing the converted bmp.
    /// </returns>
    /// <remarks>
    /// If the requested pixel format already matches the bmp's color type, no
    /// conversion is performed.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The bmp's color type has no equivalent FFmpeg pixel format.
    /// </exception>
    public static AVFrame ToAVFrame(this SKBitmap image, PixelFormat targetFormat = PixelFormat.None)
    {
        if (!image.ColorType.HasFFmpegEquivalent())
            throw new NotSupportedException();
        if (targetFormat == PixelFormat.None || targetFormat == image.ColorType.ToPixelFormat())
            return ToAVFrame(image);
        AVFrame frame = AVFrame.Allocate();
        try
        {
            frame.Width = image.Width;
            frame.Height = image.Height;
            frame.PixelFormat = targetFormat;
            Images.SwsContext.Convert(image.GetPixels(), new Images.ImageInfo(image.Width, image.Height, image.Info.ColorType.ToPixelFormat()), frame, Images.SwsAlgorithm.FastBilinear()).ThrowIfError();

            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Copies the contents of an <see cref="AVFrame"/> into an existing
    /// <see cref="SKBitmap"/>.
    /// </summary>
    /// <param name="frame">
    /// The source video frame.
    /// </param>
    /// <param name="bitmap">
    /// The destination bmp.
    /// </param>
    /// <remarks>
    /// <para>
    /// If the frame and bmp have matching dimensions and compatible pixel
    /// formats, the pixel data is copied directly.
    /// </para>
    /// <para>
    /// Otherwise, the frame is scaled and/or converted using
    /// <see cref="SwsContext"/> before being written to the bmp.
    /// </para>
    /// <para>
    /// <see cref="SKBitmap.NotifyPixelsChanged"/> is called automatically after the
    /// bmp has been updated.
    /// </para>
    /// </remarks>
    public static void CopyTo(this AVFrame frame, SKBitmap bitmap)
    {
        if (CheckCopy(frame, bitmap.Info))
            Extensions.CopyFrame(frame, bitmap);
        else
        {
            if(bitmap.IsEmpty)
            {
                SKRectI cropRect = frame.CroppedRect();
                SKImageInfo info = new(cropRect.Width, cropRect.Height, frame.PixelFormat.ToBestSkiaColorType(),SKAlphaType.Unpremul);
                if (!bitmap.TryAllocPixels(info))
                    throw new ArgumentException($"The bitmap was empty but buffer allocation failed: {info}");
            }
            using SwsContext swsContext = GetSwsContext(frame, bitmap.Info);
            swsContext.Convert(frame, bitmap.GetPixels()).ThrowIfError();
        }
        bitmap.NotifyPixelsChanged();
    }

    /// <summary>
    /// Copies the contents of an <see cref="SKBitmap"/> into an existing
    /// <see cref="AVFrame"/>.
    /// </summary>
    /// <param name="bitmap">
    /// The source bmp.
    /// </param>
    /// <param name="frame">
    /// The destination video frame.
    /// </param>
    /// <remarks>
    /// <para>
    /// If the destination frame has not been initialized, its dimensions and
    /// pixel format are set to match the bmp before the copy operation.
    /// </para>
    /// <para>
    /// If the destination frame has already been initialized, its dimensions and
    /// pixel format are preserved. The bmp is scaled and/or converted as
    /// necessary before being written to the frame.
    /// </para>
    /// <para>
    /// If the destination frame does not already own a buffer, one is allocated
    /// automatically.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The bmp's <see cref="SKColorType"/> has no equivalent FFmpeg pixel
    /// format.
    /// </exception>
    public static void CopyTo(this SKBitmap bitmap, AVFrame frame)
    {
        var targetFormat = frame.PixelFormat;
        var sourceFormat = bitmap.ColorType.ToPixelFormat();

        bool resetProperties = frame.Width == 0 || frame.Height == 0 || targetFormat == PixelFormat.None;
        if (!bitmap.ColorType.HasFFmpegEquivalent())
            throw new NotSupportedException();

        if (resetProperties)
        {
            frame.Unreference();
            frame.Width = bitmap.Width;
            frame.Height = bitmap.Height;
            frame.PixelFormat = sourceFormat;
        }
        if (!frame.HasBuffer)
            frame.CreateBuffer().ThrowIfError();

        var swAlgorithm = (bitmap.Width != frame.Width || bitmap.Height != frame.Height) ? SwsAlgorithm.Bicubic() : Images.SwsAlgorithm.FastBilinear();
        Images.SwsContext.Convert(bitmap.GetPixels(), new Images.ImageInfo(bitmap.Width, bitmap.Height, sourceFormat), frame, swAlgorithm).ThrowIfError();

    }
}
