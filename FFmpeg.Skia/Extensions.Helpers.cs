using FFmpeg.AutoGen;
using FFmpeg.Images;
using FFmpeg.Utils;
using System.Runtime.InteropServices;
using AVFrame = FFmpeg.Utils.AVFrame;

namespace FFmpeg.Skia;

public static partial class Extensions
{
    /// <summary>
    /// Gets the cropping rectangle of the frame as an <see cref="SKRectI"/>.
    /// </summary>
    /// <returns>
    /// An <see cref="SKRectI"/> describing the visible region of the frame after
    /// applying the cropping values stored in the frame.
    /// </returns>
    public static SKRectI CroppedRect(this AVFrame frame) => new(
        (int)frame.CropLeft,
        (int)frame.CropTop,
        frame.Width - (int)frame.CropRight,
        frame.Height - (int)frame.CropBottom);
}

/// <summary>
/// The pixel plumbing shared by the extension methods, <see cref="FFCodec2Skia"/> and <see cref="SKAnimatedImage"/>.
/// </summary>
internal static unsafe class FrameInterop
{
    private const int AV_FRAME_CROP_UNALIGNED = 1;

    /// <summary>Copies <paramref name="height"/> rows of <paramref name="rowBytes"/> bytes between buffers with any stride.</summary>
    /// <remarks>Negative strides (bottom-up images) are supported.</remarks>
    public static void CopyRows(IntPtr src, long srcStride, IntPtr dst, long dstStride, long rowBytes, int height)
    {
        if (height <= 0 || rowBytes <= 0)
            return;
        if (src == IntPtr.Zero || dst == IntPtr.Zero)
            throw new ArgumentException("The source or destination has no pixel data.");
        byte* s = (byte*)src;
        byte* d = (byte*)dst;
        if (srcStride == rowBytes && dstStride == rowBytes)
        {
            long size = rowBytes * height;
            Buffer.MemoryCopy(s, d, size, size);
            return;
        }
        for (int y = 0; y < height; y++)
            Buffer.MemoryCopy(s + (y * srcStride), d + (y * dstStride), rowBytes, rowBytes);
    }

    public static bool HasCrop(AVFrame frame)
        => frame.CropLeft != 0 || frame.CropTop != 0 || frame.CropRight != 0 || frame.CropBottom != 0;

    /// <summary>
    /// Returns a new reference to <paramref name="frame"/> whose data pointers, width and height describe only the
    /// visible (cropped) region. The caller owns the returned frame.
    /// </summary>
    public static AVFrame CloneCropped(AVFrame frame)
    {
        AVFrame clone = frame.Clone();
        try
        {
            if (HasCrop(clone))
                ((AVResult32)ffmpeg.av_frame_apply_cropping(FrameConverter.Pointer(clone), AV_FRAME_CROP_UNALIGNED)).ThrowIfError();
            return clone;
        }
        catch
        {
            clone.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Calls <paramref name="action"/> with the visible region of <paramref name="frame"/>, without copying pixels.
    /// </summary>
    public static void WithCropped(AVFrame frame, Action<AVFrame> action)
    {
        if (!HasCrop(frame))
        {
            action(frame);
            return;
        }
        using AVFrame cropped = CloneCropped(frame);
        action(cropped);
    }

    public static void ThrowIfNoImage(AVFrame frame)
    {
        if (frame.Width <= 0 || frame.Height <= 0 || frame.PixelFormat == PixelFormat.None)
            throw new ArgumentException("The frame does not contain a video image.", nameof(frame));
        if (frame.CroppedWidth <= 0 || frame.CroppedHeight <= 0)
            throw new ArgumentException("The frame's crop values leave no visible area.", nameof(frame));
    }

    /// <summary>Whether the color type stores an alpha channel.</summary>
    public static bool HasAlpha(SKColorType colorType) => colorType is SKColorType.Bgra8888 or SKColorType.Rgba8888
        or SKColorType.RgbaF16 or SKColorType.RgbaF32 or SKColorType.RgbaF16Clamped or SKColorType.Argb4444
        or SKColorType.Rgba1010102 or SKColorType.Bgra1010102 or SKColorType.Rgba16161616 or SKColorType.Alpha8
        or SKColorType.AlphaF16 or SKColorType.Alpha16;

    /// <summary>
    /// The alpha type of Skia images created from FFmpeg data: FFmpeg stores straight (unpremultiplied) alpha.
    /// </summary>
    public static SKAlphaType AlphaTypeFor(SKColorType colorType) => HasAlpha(colorType) ? SKAlphaType.Unpremul : SKAlphaType.Opaque;

    /// <summary>Whether the alpha type of <paramref name="info"/> differs from FFmpeg's straight alpha.</summary>
    private static bool IsPremultiplied(SKImageInfo info) => info.AlphaType == SKAlphaType.Premul && HasAlpha(info.ColorType);

    private static bool HasAlpha(PixelFormat format)
    {
        _AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get((_AVPixelFormat)format);
        return desc != null && (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_ALPHA) != 0;
    }

    public static PixelFormat ToSupportedPixelFormat(SKColorType colorType)
        => colorType.HasFFmpegEquivalent()
            ? colorType.ToPixelFormat()
            : throw new NotSupportedException($"The color type {colorType} has no equivalent FFmpeg pixel format.");

    /// <summary>
    /// Writes the visible region of <paramref name="frame"/> into a Skia pixel buffer described by
    /// <paramref name="info"/> and <paramref name="rowBytes"/>, scaling and converting as needed. Straight alpha is
    /// premultiplied when the destination is premultiplied.
    /// </summary>
    public static void WriteFrame(AVFrame frame, IntPtr pixels, int rowBytes, SKImageInfo info, FrameConverter converter)
    {
        ThrowIfNoImage(frame);
        if (pixels == IntPtr.Zero)
            throw new ArgumentException("The destination has no pixels.");
        _ = ToSupportedPixelFormat(info.ColorType);

        WithCropped(frame, visible =>
        {
            if (!IsPremultiplied(info) || !HasAlpha(visible.PixelFormat))
            {
                converter.Convert(visible, new PixelBuffer(pixels, rowBytes, info));
                return;
            }

            // FFmpeg has straight alpha: convert into an unpremultiplied scratch buffer and let Skia premultiply.
            SKImageInfo straight = info.WithAlphaType(SKAlphaType.Unpremul);
            int scratchRowBytes = straight.RowBytes;
            IntPtr scratch = Marshal.AllocHGlobal((IntPtr)((long)scratchRowBytes * straight.Height));
            try
            {
                converter.Convert(visible, new PixelBuffer(scratch, scratchRowBytes, straight));
                using SKPixmap pixmap = new(straight, scratch, scratchRowBytes);
                if (!pixmap.ReadPixels(info, pixels, rowBytes))
                    throw new NotSupportedException($"Skia cannot convert {straight} to {info}.");
            }
            finally
            {
                Marshal.FreeHGlobal(scratch);
            }
        });
    }

    /// <summary>
    /// Writes a Skia pixel buffer into <paramref name="frame"/>, scaling and converting to the frame's size and
    /// format. Premultiplied alpha is converted to FFmpeg's straight alpha. The frame gets a buffer if it has none
    /// and is made writable otherwise.
    /// </summary>
    public static void ReadIntoFrame(IntPtr pixels, int rowBytes, SKImageInfo info, AVFrame frame, FrameConverter converter)
    {
        if (pixels == IntPtr.Zero || info.Width <= 0 || info.Height <= 0)
            throw new ArgumentException("The source image has no pixels.");
        _ = ToSupportedPixelFormat(info.ColorType);
        if (frame.Width <= 0 || frame.Height <= 0 || frame.PixelFormat == PixelFormat.None)
            throw new ArgumentException("The frame has no size or pixel format.", nameof(frame));

        if (frame.HasBuffer)
            frame.MakeWriteable().ThrowIfError(); // never write into buffers shared with other frames (e.g. a decoder)
        else
            frame.CreateBuffer(0).ThrowIfError();

        if (!IsPremultiplied(info) || !HasAlpha(frame.PixelFormat))
        {
            converter.Convert(new PixelBuffer(pixels, rowBytes, info), frame);
            return;
        }

        SKImageInfo straight = info.WithAlphaType(SKAlphaType.Unpremul);
        int scratchRowBytes = straight.RowBytes;
        IntPtr scratch = Marshal.AllocHGlobal((IntPtr)((long)scratchRowBytes * straight.Height));
        try
        {
            using SKPixmap pixmap = new(info, pixels, rowBytes);
            if (!pixmap.ReadPixels(straight, scratch, scratchRowBytes))
                throw new NotSupportedException($"Skia cannot convert {info} to {straight}.");
            converter.Convert(new PixelBuffer(scratch, scratchRowBytes, straight), frame);
        }
        finally
        {
            Marshal.FreeHGlobal(scratch);
        }
    }

    /// <summary>
    /// Copies <paramref name="frame"/> into <paramref name="bitmap"/>. A bitmap without pixels is allocated first,
    /// with <paramref name="allocInfo"/> or, if <see langword="null"/>, the visible size of the frame and the closest
    /// Skia color type.
    /// </summary>
    public static void CopyToBitmap(AVFrame frame, SKBitmap bitmap, FrameConverter converter, SKImageInfo? allocInfo = null)
    {
        ThrowIfNoImage(frame);
        if (bitmap.DrawsNothing)
        {
            SKImageInfo info = allocInfo ?? NewInfo(frame.CroppedWidth, frame.CroppedHeight, frame.PixelFormat.ToBestSkiaColorType());
            if (!bitmap.TryAllocPixels(info))
                throw new ArgumentException($"The bitmap was empty but buffer allocation failed: {info}");
        }
        WriteFrame(frame, bitmap.GetPixels(), bitmap.RowBytes, bitmap.Info, converter);
        bitmap.NotifyPixelsChanged();
    }

    public static SKImageInfo NewInfo(int width, int height, SKColorType colorType)
        => new(width, height, colorType, AlphaTypeFor(colorType));
}
