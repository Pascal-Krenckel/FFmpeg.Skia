using FFmpeg.AutoGen;
using FFmpeg.Images;
using FFmpeg.Unsafe;
using AVFrame = FFmpeg.Utils.AVFrame;

namespace FFmpeg.Skia;

/// <summary>
/// A packed (single plane) pixel buffer, e.g. the memory of an <see cref="SKBitmap"/> or <see cref="SKPixmap"/>.
/// </summary>
internal readonly struct PixelBuffer(IntPtr pixels, int rowBytes, int width, int height, int bytesPerPixel, PixelFormat format)
{
    public IntPtr Pixels { get; } = pixels;
    public int RowBytes { get; } = rowBytes;
    public int Width { get; } = width;
    public int Height { get; } = height;
    public int BytesPerPixel { get; } = bytesPerPixel;
    public PixelFormat Format { get; } = format;

    public PixelBuffer(IntPtr pixels, int rowBytes, SKImageInfo info)
        : this(pixels, rowBytes, info.Width, info.Height, info.BytesPerPixel, info.ColorType.ToPixelFormat()) { }
}

/// <summary>
/// Converts between <see cref="AVFrame"/>s and packed pixel buffers that may have any row stride.
/// </summary>
/// <remarks>
/// <para>
/// Frames with the same size and pixel format as the buffer are copied row by row; everything else is
/// converted with swscale. The scaler is kept between calls, so a long-lived instance (one per decoder)
/// does not set up a new conversion for every frame.
/// </para>
/// <para>
/// FFmpeg 9 (FFmpegDotNet 9.x) uses a dynamic <see cref="SwsContext"/>, which honours the frame's color
/// properties (YUV matrix, range, ...). FFmpeg 8 (FFmpegDotNet 8.x) uses a fixed context and passes the
/// row strides explicitly, because fixed contexts always allocate their own destination buffers in
/// <c>sws_scale_frame</c>.
/// </para>
/// <para>Instances are not thread-safe.</para>
/// </remarks>
internal sealed unsafe class FrameConverter : IDisposable
{
#if FFMPEG9
    private readonly SwsContext sws = new();
    private readonly AVFrame view = AVFrame.Allocate();
#else
    private SwsContext? sws;
    private (int srcW, int srcH, PixelFormat srcFmt, int dstW, int dstH, PixelFormat dstFmt) key;
#endif
    private bool disposed;

    private static SwsAlgorithm AlgorithmFor(int srcW, int srcH, int dstW, int dstH)
        => srcW == dstW && srcH == dstH ? SwsAlgorithm.FastBilinear() : SwsAlgorithm.Bicubic();

    /// <summary>
    /// Writes <paramref name="src"/> (already cropped) into <paramref name="dst"/>, scaling and converting as needed.
    /// </summary>
    public void Convert(AVFrame src, in PixelBuffer dst)
    {
        ThrowIfDisposed();
        if (src.Width == dst.Width && src.Height == dst.Height && src.PixelFormat == dst.Format)
        {
            FrameInterop.CopyRows(src.Data[0], src.LineSize[0], dst.Pixels, dst.RowBytes, (long)dst.Width * dst.BytesPerPixel, dst.Height);
            return;
        }
#if FFMPEG9
        SetView(dst);
        try
        {
            sws.Algorithm = AlgorithmFor(src.Width, src.Height, dst.Width, dst.Height);
            sws.Convert(src, view).ThrowIfError();
        }
        finally
        {
            ffmpeg.av_frame_unref(Pointer(view));
        }
#else
        SwsContext context = GetContext(src.Width, src.Height, src.PixelFormat, dst.Width, dst.Height, dst.Format);
        IntPtr* planes = stackalloc IntPtr[4] { dst.Pixels, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero };
        int* strides = stackalloc int[4] { dst.RowBytes, 0, 0, 0 };
        context.Convert(src, new ReadOnlySpan<IntPtr>(planes, 4), new ReadOnlySpan<int>(strides, 4)).ThrowIfError();
#endif
    }

    /// <summary>
    /// Writes <paramref name="src"/> into <paramref name="dst"/>, scaling and converting as needed.
    /// <paramref name="dst"/> must have its size and format set and a writable buffer.
    /// </summary>
    public void Convert(in PixelBuffer src, AVFrame dst)
    {
        ThrowIfDisposed();
        if (src.Width == dst.Width && src.Height == dst.Height && src.Format == dst.PixelFormat)
        {
            FrameInterop.CopyRows(src.Pixels, src.RowBytes, dst.Data[0], dst.LineSize[0], (long)src.Width * src.BytesPerPixel, src.Height);
            return;
        }
#if FFMPEG9
        SetView(src);
        try
        {
            sws.Algorithm = AlgorithmFor(src.Width, src.Height, dst.Width, dst.Height);
            sws.Convert(view, dst).ThrowIfError();
        }
        finally
        {
            ffmpeg.av_frame_unref(Pointer(view));
        }
#else
        SwsContext context = GetContext(src.Width, src.Height, src.Format, dst.Width, dst.Height, dst.PixelFormat);
        IntPtr* planes = stackalloc IntPtr[4] { src.Pixels, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero };
        int* strides = stackalloc int[4] { src.RowBytes, 0, 0, 0 };
        context.Convert(new ReadOnlySpan<IntPtr>(planes, 4), new ReadOnlySpan<int>(strides, 4), dst).ThrowIfError();
#endif
    }

#if FFMPEG9
    /// <summary>Points the reusable view frame at a packed buffer (no ownership, no AVBufferRef).</summary>
    private void SetView(in PixelBuffer buffer)
    {
        _AVFrame* f = Pointer(view);
        ffmpeg.av_frame_unref(f);
        f->width = buffer.Width;
        f->height = buffer.Height;
        f->format = (int)buffer.Format;
        f->data[0] = (byte*)buffer.Pixels;
        f->linesize[0] = buffer.RowBytes;
    }
#else
    private SwsContext GetContext(int srcW, int srcH, PixelFormat srcFmt, int dstW, int dstH, PixelFormat dstFmt)
    {
        var wanted = (srcW, srcH, srcFmt, dstW, dstH, dstFmt);
        if (sws != null && key == wanted)
            return sws;
        sws?.Dispose();
        sws = null;
        sws = new SwsContext(srcW, srcH, srcFmt, dstW, dstH, dstFmt, AlgorithmFor(srcW, srcH, dstW, dstH));
        key = wanted;
        return sws;
    }
#endif

    internal static _AVFrame* Pointer(AVFrame frame) => ((IAVPointer<_AVFrame>)frame).Pointer;

    private void ThrowIfDisposed()
    {
        if (disposed)
            throw new ObjectDisposedException(nameof(FrameConverter));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
#if FFMPEG9
        sws.Dispose();
        view.Dispose();
#else
        sws?.Dispose();
        sws = null;
#endif
    }
}
