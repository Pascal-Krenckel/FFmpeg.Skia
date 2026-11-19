using FFmpeg.Images;
using FFmpeg.Utils;

namespace FFmpeg.Skia;


/// <summary>
/// Provides extension methods for interoperating between FFmpeg image types and
/// SkiaSharp.
/// </summary>
/// <remarks>
/// <para>
/// This class contains helper methods for converting between FFmpeg
/// <see cref="PixelFormat"/> values and SkiaSharp <see cref="SKColorType"/>
/// values, as well as methods for converting image data between
/// <see cref="AVFrame"/>, <see cref="SKImage"/>, and <see cref="SKBitmap"/>.
/// </para>
/// <para>
/// When the source and destination pixel formats are compatible, image data is
/// copied directly without conversion. Otherwise, pixel format conversion and
/// image scaling are performed using <see cref="Images.SwsContext"/>.
/// </para>
/// <para>
/// Methods prefixed with <c>To</c> create independent copies of the image data,
/// while methods prefixed with <c>As</c> create Skia objects that share the
/// underlying pixel buffer with a new reference to the <see cref="AVFrame"/>, avoiding an
/// additional memory copy whenever possible.
/// </para>
/// <para>
/// FFmpeg stores straight (unpremultiplied) alpha. Skia images and bitmaps created by these methods
/// use <see cref="SKAlphaType.Unpremul"/> for color types with alpha and <see cref="SKAlphaType.Opaque"/> otherwise.
/// When copying from or into premultiplied Skia images, the alpha is converted.
/// </para>
/// <para>
/// The cropping values of a frame (<see cref="AVFrame.CropLeft"/>, ...) are applied: only the visible region is
/// converted.
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
    /// A new <see cref="SKBitmap"/> containing the visible region of the frame.
    /// </returns>
    /// <remarks>
    /// <para>
    /// If the frame's pixel format is directly supported by Skia, the pixel data is
    /// copied without conversion.
    /// </para>
    /// <para>
    /// Otherwise, the frame is converted to <see cref="SKImageInfo.PlatformColorType"/> (or
    /// <see cref="SKColorType.Rgba8888"/>) using <see cref="Images.SwsContext"/>.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The frame does not contain valid image dimensions.
    /// </exception>
    public static SKBitmap ToSKBitmap(this AVFrame frame)
    {
        PixelFormat pixfmt = frame.PixelFormat;
        SKColorType colorType = pixfmt.HasSkiaEquivalent() ? pixfmt.ToSkiaColorType() : SKImageInfo.PlatformColorType;
        if (!colorType.HasFFmpegEquivalent())
            colorType = SKColorType.Rgba8888; // make sure that platform color type is correct
        return ToNewBitmap(frame, colorType);
    }

    /// <summary>
    /// Creates a new <see cref="SKBitmap"/> from an <see cref="AVFrame"/> using the
    /// specified Skia color type.
    /// </summary>
    /// <param name="frame">
    /// The source video frame.
    /// </param>
    /// <param name="colorType">
    /// The desired color type of the resulting bitmap. If
    /// <see cref="SKColorType.Unknown"/> is specified, the color type is chosen as in <see cref="ToSKBitmap(AVFrame)"/>.
    /// </param>
    /// <returns>
    /// A new <see cref="SKBitmap"/> containing the visible region of the frame.
    /// </returns>
    /// <remarks>
    /// If the requested color type already matches the frame's pixel format, no
    /// pixel format conversion is performed.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The frame does not contain valid image dimensions.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The specified color type has no equivalent FFmpeg pixel format.
    /// </exception>
    public static SKBitmap ToSKBitmap(this AVFrame frame, SKColorType colorType = SKColorType.Unknown)
        => colorType == SKColorType.Unknown ? ToSKBitmap(frame) : ToNewBitmap(frame, colorType);

    private static SKBitmap ToNewBitmap(AVFrame frame, SKColorType colorType)
    {
        FrameInterop.ThrowIfNoImage(frame);
        _ = FrameInterop.ToSupportedPixelFormat(colorType);
        SKImageInfo info = FrameInterop.NewInfo(frame.CroppedWidth, frame.CroppedHeight, colorType);
        SKBitmap bitmap = new(info);
        try
        {
            using ConverterPool.Lease lease = ConverterPool.Rent();
            FrameConverter converter = lease.Converter;
            FrameInterop.WriteFrame(frame, bitmap.GetPixels(), bitmap.RowBytes, bitmap.Info, converter);
            bitmap.NotifyPixelsChanged();
            return bitmap;
        }
        catch
        {
            bitmap.Dispose();
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
    /// An <see cref="SKBitmap"/> representing the visible region of the frame.
    /// </returns>
    /// <remarks>
    /// <para>
    /// If the frame uses a pixel format that is directly supported by Skia, no pixel
    /// data is copied. Instead, a new reference to the frame is created and the returned bitmap
    /// points into its buffer. The buffer may be shared with other frames (e.g. the decoder's), so treat the
    /// bitmap as read-only.
    /// </para>
    /// <para>
    /// The frame reference remains alive until the returned <see cref="SKBitmap"/> is
    /// disposed or reset.
    /// </para>
    /// <para>
    /// If the frame's pixel format is not supported by Skia, this method falls back
    /// to <see cref="ToSKBitmap(AVFrame)"/>, which performs a pixel format
    /// conversion and copies the image data.
    /// </para>
    /// </remarks>
    public static SKBitmap AsSKBitmap(this AVFrame frame)
    {
        FrameInterop.ThrowIfNoImage(frame);
        if (!CanShare(frame))
            return frame.ToSKBitmap();
        SKBitmap bmp = new();
        try
        {
            InstallFrame(bmp, frame);
            return bmp;
        }
        catch
        {
            bmp.Dispose();
            throw;
        }
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
    /// The bitmap's previous pixels are released first.
    /// </para>
    /// <para>
    /// If the frame uses a pixel format that is directly supported by Skia, no pixel
    /// data is copied. Instead, a new reference to the frame is created and the bitmap points into its buffer
    /// (see <see cref="AsSKBitmap(AVFrame)"/>).
    /// The frame reference remains alive until the bitmap is reset or disposed.
    /// </para>
    /// <para>
    /// If the frame's pixel format is not supported by Skia, the frame is converted into newly allocated pixels
    /// as in <see cref="CopyTo(AVFrame,SKBitmap)"/>.
    /// </para>
    /// </remarks>
    public static void Reference(this SKBitmap bmp, AVFrame frame)
    {
        FrameInterop.ThrowIfNoImage(frame);
        bmp.Reset(); // never write into pixels the bitmap may share with a previously referenced frame
        if (CanShare(frame))
            InstallFrame(bmp, frame);
        else
            frame.CopyTo(bmp);
    }

    private static bool CanShare(AVFrame frame) => frame.PixelFormat.HasSkiaEquivalent() && frame.LineSize[0] > 0;

    private static readonly SKBitmapReleaseDelegate ReleaseFrame = (_, context) => ((AVFrame)context).Dispose();

    private static void InstallFrame(SKBitmap bmp, AVFrame frame)
    {
        AVFrame visible = FrameInterop.CloneCropped(frame);
        SKColorType colorType = visible.PixelFormat.ToSkiaColorType();
        SKImageInfo info = FrameInterop.NewInfo(visible.Width, visible.Height, colorType);
        // Skia calls the release proc if InstallPixels fails; AVFrame.Dispose is idempotent.
        if (!bmp.InstallPixels(info, visible.Data[0], visible.LineSize[0], ReleaseFrame, visible))
        {
            visible.Dispose();
            throw new InvalidOperationException($"Skia rejected the frame's pixels ({info}, row bytes {visible.LineSize[0]}).");
        }
    }

    /// <summary>
    /// Creates a new <see cref="AVFrame"/> from an <see cref="SKBitmap"/>.
    /// </summary>
    /// <param name="image">
    /// The source bitmap.
    /// </param>
    /// <returns>
    /// A newly allocated frame containing the bitmap data.
    /// </returns>
    /// <remarks>
    /// The resulting frame uses the FFmpeg pixel format corresponding to the
    /// bitmap's <see cref="SKColorType"/>. Premultiplied alpha is converted to straight alpha.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The bitmap's color type has no equivalent FFmpeg pixel format.
    /// </exception>
    public static AVFrame ToAVFrame(this SKBitmap image) => ToAVFrame(image, PixelFormat.None);

    /// <summary>
    /// Creates a new <see cref="AVFrame"/> from an <see cref="SKBitmap"/> using the
    /// specified pixel format.
    /// </summary>
    /// <param name="image">
    /// The source bitmap.
    /// </param>
    /// <param name="targetFormat">
    /// The desired FFmpeg pixel format. Specify <see cref="PixelFormat.None"/> to
    /// preserve the bitmap's native format.
    /// </param>
    /// <returns>
    /// A newly allocated frame containing the converted bitmap.
    /// </returns>
    /// <remarks>
    /// If the requested pixel format already matches the bitmap's color type, no
    /// conversion is performed.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The bitmap's color type has no equivalent FFmpeg pixel format.
    /// </exception>
    public static AVFrame ToAVFrame(this SKBitmap image, PixelFormat targetFormat = PixelFormat.None)
    {
        PixelFormat sourceFormat = FrameInterop.ToSupportedPixelFormat(image.ColorType);
        if (image.DrawsNothing)
            throw new ArgumentException("The bitmap has no pixels.", nameof(image));
        AVFrame frame = AVFrame.Allocate();
        try
        {
            frame.Width = image.Width;
            frame.Height = image.Height;
            frame.PixelFormat = targetFormat == PixelFormat.None ? sourceFormat : targetFormat;
            using ConverterPool.Lease lease = ConverterPool.Rent();
            FrameConverter converter = lease.Converter;
            FrameInterop.ReadIntoFrame(image.GetPixels(), image.RowBytes, image.Info, frame, converter);
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
    /// The destination bitmap.
    /// </param>
    /// <remarks>
    /// <para>
    /// If the bitmap has no pixels, it is allocated with the visible size of the frame and the frame's color type,
    /// or the closest 8 bit color type if Skia does not support the frame's pixel format.
    /// </para>
    /// <para>
    /// If the frame and bitmap have matching dimensions and compatible pixel
    /// formats, the pixel data is copied directly.
    /// Otherwise, the frame is scaled and/or converted using
    /// <see cref="Images.SwsContext"/> before being written to the bitmap.
    /// </para>
    /// <para>
    /// <see cref="SKBitmap.NotifyPixelsChanged"/> is called automatically after the
    /// bitmap has been updated.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The bitmap's color type has no equivalent FFmpeg pixel format.
    /// </exception>
    public static void CopyTo(this AVFrame frame, SKBitmap bitmap)
    {
        using ConverterPool.Lease lease = ConverterPool.Rent();
        FrameConverter converter = lease.Converter;
        FrameInterop.CopyToBitmap(frame, bitmap, converter);
    }

    /// <summary>
    /// Copies the contents of an <see cref="SKBitmap"/> into an existing
    /// <see cref="AVFrame"/>.
    /// </summary>
    /// <param name="bitmap">
    /// The source bitmap.
    /// </param>
    /// <param name="frame">
    /// The destination video frame.
    /// </param>
    /// <remarks>
    /// <para>
    /// If the destination frame has not been initialized, its dimensions and
    /// pixel format are set to match the bitmap before the copy operation.
    /// </para>
    /// <para>
    /// If the destination frame has already been initialized, its dimensions and
    /// pixel format are preserved. The bitmap is scaled and/or converted as
    /// necessary before being written to the frame.
    /// </para>
    /// <para>
    /// If the destination frame does not already own a buffer, one is allocated
    /// automatically. If its buffer is shared with other frames, it is made writable first.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The bitmap's <see cref="SKColorType"/> has no equivalent FFmpeg pixel
    /// format.
    /// </exception>
    public static void CopyTo(this SKBitmap bitmap, AVFrame frame)
    {
        PixelFormat sourceFormat = FrameInterop.ToSupportedPixelFormat(bitmap.ColorType);
        if (bitmap.DrawsNothing)
            throw new ArgumentException("The bitmap has no pixels.", nameof(bitmap));
        PrepareTarget(frame, bitmap.Width, bitmap.Height, sourceFormat);
        using ConverterPool.Lease lease = ConverterPool.Rent();
        FrameConverter converter = lease.Converter;
        FrameInterop.ReadIntoFrame(bitmap.GetPixels(), bitmap.RowBytes, bitmap.Info, frame, converter);
    }

    private static void PrepareTarget(AVFrame frame, int width, int height, PixelFormat format)
    {
        if (frame.Width == 0 || frame.Height == 0 || frame.PixelFormat == PixelFormat.None)
        {
            frame.Unreference();
            frame.Width = width;
            frame.Height = height;
            frame.PixelFormat = format;
        }
    }
}
