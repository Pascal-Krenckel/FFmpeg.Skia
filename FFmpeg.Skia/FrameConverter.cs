using FFmpeg.AutoGen;
using FFmpeg.Images;
using FFmpeg.Unsafe;
using FFmpeg.Utils;
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
/// Converts between <see cref="AVFrame"/>s and packed Skia pixel buffers that may have any row stride.
/// </summary>
/// <remarks>
/// <para>
/// Frames with the same size and pixel format as the buffer are copied row by row; everything else is
/// converted with a dynamic swscale context (<c>sws_scale_frame</c>), which FFmpeg 8 and 9 both provide. It takes
/// the color properties of the frame into account:
/// </para>
/// <list type="bullet">
/// <item>the YUV matrix (<see cref="AVFrame.ColorSpace"/>) and range (<see cref="AVFrame.ColorRange"/>); untagged
/// YUV is BT.601 limited range, as everywhere in FFmpeg.</item>
/// <item>Skia buffers are SDR with BT.709/sRGB primaries. HDR (PQ, HLG) is tone mapped and wide gamuts
/// (BT.2020, P3, ...) are mapped to BT.709 when converting to Skia, and the other way round when converting
/// into frames tagged that way. Other transfer functions and primaries are passed through unchanged.</item>
/// <item>YUV frames written from Skia that have no matrix or range set are tagged with what was used
/// (BT.601, limited range), so encoders and players interpret them correctly.</item>
/// </list>
/// <para>
/// The scaler is kept between calls, so a long-lived instance (one per decoder) does not set up a new
/// conversion for every frame. Instances are not thread-safe.
/// </para>
/// </remarks>
internal sealed unsafe class FrameConverter : IDisposable
{
#if FFMPEG9
    private readonly SwsContext sws = new();
#else
    private readonly SwsContext sws = SwsContext.Allocate();
#endif
    /// <summary>Reusable frame that points at a Skia buffer (no ownership, no AVBufferRef).</summary>
    private readonly AVFrame view = AVFrame.Allocate();
    private bool disposed;

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
        _AVFrame* s = Pointer(src);
        _AVFrame* v = SetView(dst);
        try
        {
            // the Skia side is SDR BT.709/sRGB; anything else that needs it is color mapped by swscale
            v->color_primaries = IsWideGamut(s->color_primaries) ? _AVColorPrimaries.AVCOL_PRI_BT709 : s->color_primaries;
            v->color_trc = IsHdr(s->color_trc) ? _AVColorTransferCharacteristic.AVCOL_TRC_BT709 : s->color_trc;
            Scale(s, v);
        }
        finally
        {
            ffmpeg.av_frame_unref(v);
        }
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
        _AVFrame* d = Pointer(dst);
        TagYuv(d);
        _AVFrame* v = SetView(src);
        try
        {
            bool map = IsWideGamut(d->color_primaries) || IsHdr(d->color_trc);
            v->color_primaries = map ? _AVColorPrimaries.AVCOL_PRI_BT709 : d->color_primaries;
            v->color_trc = map ? _AVColorTransferCharacteristic.AVCOL_TRC_BT709 : d->color_trc;
            Scale(v, d);
        }
        finally
        {
            ffmpeg.av_frame_unref(v);
        }
    }

    private void Scale(_AVFrame* src, _AVFrame* dst)
    {
        SwsAlgorithm algorithm = src->width == dst->width && src->height == dst->height
            ? SwsAlgorithm.FastBilinear()
            : SwsAlgorithm.Bicubic();

        // A frame marked interlaced would be scaled field by field into a progressive buffer (half the lines);
        // like FFmpeg's scale filter, scale both as progressive frames if only one of them is interlaced.
        int srcFlags = src->flags, dstFlags = dst->flags;
        bool interlaceMismatch = ((srcFlags ^ dstFlags) & ffmpeg.AV_FRAME_FLAG_INTERLACED) != 0;
        if (interlaceMismatch)
        {
            src->flags &= ~ffmpeg.AV_FRAME_FLAG_INTERLACED;
            dst->flags &= ~ffmpeg.AV_FRAME_FLAG_INTERLACED;
        }
        try
        {
#if FFMPEG9
            sws.Algorithm = algorithm;
#else
            _SwsContext* context = ((IAVPointer<_SwsContext>)sws).Pointer;
            context->flags = (uint)algorithm.AlgorithmFlags;
            context->scaler_params[0] = algorithm.Param1;
            context->scaler_params[1] = algorithm.Param2;
#endif
            ((AVResult32)ffmpeg.sws_scale_frame(((IAVPointer<_SwsContext>)sws).Pointer, dst, src)).ThrowIfError();
        }
        finally
        {
            if (interlaceMismatch)
            {
                src->flags = srcFlags;
                dst->flags = dstFlags;
            }
        }
    }

    /// <summary>
    /// Tags a YUV frame without matrix or range with FFmpeg's defaults for untagged YUV (BT.601, limited range),
    /// which is what swscale writes into it.
    /// </summary>
    private static void TagYuv(_AVFrame* frame)
    {
        _AVPixFmtDescriptor* desc = ffmpeg.av_pix_fmt_desc_get((_AVPixelFormat)frame->format);
        const ulong NotYuv = ffmpeg.AV_PIX_FMT_FLAG_RGB | ffmpeg.AV_PIX_FMT_FLAG_PAL | ffmpeg.AV_PIX_FMT_FLAG_BAYER
            | ffmpeg.AV_PIX_FMT_FLAG_XYZ | ffmpeg.AV_PIX_FMT_FLAG_HWACCEL;
        if (desc == null || (desc->flags & NotYuv) != 0 || desc->nb_components < 3)
            return; // RGB, gray, ...
        if (frame->colorspace == _AVColorSpace.AVCOL_SPC_UNSPECIFIED)
            frame->colorspace = _AVColorSpace.AVCOL_SPC_SMPTE170M;
        if (frame->color_range == _AVColorRange.AVCOL_RANGE_UNSPECIFIED && !IsJpegFormat((_AVPixelFormat)frame->format))
            frame->color_range = _AVColorRange.AVCOL_RANGE_MPEG;
    }

    private static bool IsJpegFormat(_AVPixelFormat format) => format is _AVPixelFormat.AV_PIX_FMT_YUVJ420P
        or _AVPixelFormat.AV_PIX_FMT_YUVJ422P or _AVPixelFormat.AV_PIX_FMT_YUVJ444P
        or _AVPixelFormat.AV_PIX_FMT_YUVJ440P or _AVPixelFormat.AV_PIX_FMT_YUVJ411P;

    private static bool IsHdr(_AVColorTransferCharacteristic trc)
        => trc is _AVColorTransferCharacteristic.AVCOL_TRC_SMPTE2084 or _AVColorTransferCharacteristic.AVCOL_TRC_ARIB_STD_B67;

    private static bool IsWideGamut(_AVColorPrimaries primaries) => primaries is _AVColorPrimaries.AVCOL_PRI_BT2020
        or _AVColorPrimaries.AVCOL_PRI_SMPTE431 or _AVColorPrimaries.AVCOL_PRI_SMPTE432 or _AVColorPrimaries.AVCOL_PRI_SMPTE428;

    /// <summary>Points the reusable view frame at a packed buffer.</summary>
    private _AVFrame* SetView(in PixelBuffer buffer)
    {
        _AVFrame* f = Pointer(view);
        ffmpeg.av_frame_unref(f);
        f->width = buffer.Width;
        f->height = buffer.Height;
        f->format = (int)buffer.Format;
        f->data[0] = (byte*)buffer.Pixels;
        f->linesize[0] = buffer.RowBytes;
        return f;
    }

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
        sws.Dispose();
        view.Dispose();
    }
}

/// <summary>
/// A small pool of <see cref="FrameConverter"/>s for the extension methods, which are called once per frame.
/// A converter keeps its swscale setup (for HDR this includes a tone mapping table that takes seconds to build),
/// so consecutive conversions of the same kind do not set the conversion up again.
/// </summary>
internal static class ConverterPool
{
    private static readonly System.Collections.Concurrent.ConcurrentBag<FrameConverter> pool = [];
    private static readonly int maxPooled = Math.Min(Environment.ProcessorCount, 8);
    private static int pooled;

    public static Lease Rent()
    {
        if (pool.TryTake(out FrameConverter? converter))
        {
            _ = Interlocked.Decrement(ref pooled);
            return new Lease(converter);
        }
        return new Lease(new FrameConverter());
    }

    private static void Return(FrameConverter converter)
    {
        if (Interlocked.Increment(ref pooled) <= maxPooled)
            pool.Add(converter);
        else
        {
            _ = Interlocked.Decrement(ref pooled);
            converter.Dispose();
        }
    }

    public readonly struct Lease(FrameConverter converter) : IDisposable
    {
        public FrameConverter Converter { get; } = converter;
        public void Dispose() => Return(Converter);
    }
}
