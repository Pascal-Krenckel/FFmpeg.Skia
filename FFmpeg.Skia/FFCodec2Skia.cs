using FFmpeg.HW;
using FFmpeg.Images;
using FFmpeg.Utils;

namespace FFmpeg.Skia;

/// <summary>
/// Decodes video frames from a media source directly into <see cref="SKBitmap"/> instances.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FFCodec2Skia"/> provides an API similar to <c>SKCodec</c>, but uses FFmpeg
/// for decoding instead of Skia's built-in image codecs. Frames can be decoded into newly
/// allocated bitmaps or into an existing <see cref="SKBitmap"/> supplied by the caller.
/// </para>
/// <para>
/// If the source pixel format is directly supported by Skia, pixel data is copied without
/// conversion whenever possible. Otherwise, an internal <see cref="SwsContext"/> is used
/// to convert the frame into the requested Skia color type. The scaler is kept between frames.
/// </para>
/// <para>
/// Instances are not thread-safe: call the decoding and seeking methods from one thread at a time.
/// </para>
/// </remarks>
public sealed class FFCodec2Skia : IDisposable
{
    private readonly MediaSource mediaSource;
    private readonly FrameConverter converter;

    private readonly AVFrame frame;
    private readonly int streamIndex = -1;

    /// <summary>Frames that end before this time are decoded but not returned (exact seeking).</summary>
    private TimeSpan? skipUntil;


    /// <summary>
    /// Gets the image information describing decoded frames.
    /// </summary>
    /// <remarks>
    /// The image information specifies the dimensions and color type of the
    /// <see cref="SKBitmap"/> instances produced by this decoder. Its alpha type is
    /// <see cref="SKAlphaType.Unpremul"/> for color types with alpha (FFmpeg uses straight alpha), otherwise
    /// <see cref="SKAlphaType.Opaque"/>.
    /// </remarks>
    public SKImageInfo Info { get; private set; }

    /// <summary>
    /// Gets the total number of frames reported by the video stream.
    /// </summary>
    /// <remarks>
    /// Some container formats do not store the total frame count. In such cases,
    /// this value may be zero or estimated by FFmpeg.
    /// </remarks>
    public long Frames { get; }

    /// <summary>
    /// Gets the nominal frame rate of the video stream.
    /// </summary>
    /// <remarks>
    /// The value is taken from the underlying FFmpeg codec context and may differ
    /// from the effective playback rate for variable-frame-rate content.
    /// </remarks>
    public Rational FrameRate { get; }



    /// <summary>
    /// Gets the duration of the video stream.
    /// </summary>
    /// <remarks>
    /// If the stream does not store its duration, the duration of the whole media is used;
    /// <see cref="TimeSpan.Zero"/> if that is unknown too (e.g. live streams).
    /// </remarks>
    public TimeSpan Duration { get; }

    private readonly TimeSpan startTime;

    private FFCodec2Skia(MediaSource mediaSource, int streamIndex, int targetWidth, int targetHeight, SKColorType colorType)
    {
        if (streamIndex < 0 || streamIndex >= mediaSource.Streams.Count)
            throw new ArgumentException("The media does not contain a video stream.", nameof(streamIndex));
        if (!colorType.HasFFmpegEquivalent() && colorType != SKColorType.Unknown)
            throw new ArgumentException($"The color type {colorType} has no equivalent FFmpeg pixel format.", nameof(colorType));

        this.mediaSource = mediaSource;
        this.streamIndex = streamIndex;
        for (int i = 0; i < mediaSource.Streams.Count; i++)
            if (i != streamIndex)
                mediaSource.Streams[i].Discard = Formats.DiscardFlags.All;

        Codecs.CodecContext codec = mediaSource.CodecContexts[streamIndex];
        targetWidth = targetWidth > 0 ? targetWidth : codec.Width;
        targetHeight = targetHeight > 0 ? targetHeight : codec.Height;

        if (colorType == SKColorType.Unknown && codec.PixelFormat.HasSkiaEquivalent())
            colorType = codec.PixelFormat.ToSkiaColorType();
        else if (colorType == SKColorType.Unknown)
            colorType = SKImageInfo.PlatformColorType.HasFFmpegEquivalent() ? SKImageInfo.PlatformColorType : SKColorType.Rgba8888;

        Formats.AVStream stream = mediaSource.Streams[streamIndex];
        Frames = stream.NumberOfFrames;
        Duration = GetDuration(mediaSource, stream);
        startTime = stream.StartTime != AutoGen.ffmpeg.AV_NOPTS_VALUE && stream.TimeBase.Denominator != 0
            ? FFCodecFrameInfo.ToTimeSpan(stream.StartTime, stream.TimeBase)
            : TimeSpan.Zero;
        FrameRate = codec.FrameRate;

        Info = FrameInterop.NewInfo(targetWidth, targetHeight, colorType);
        // allocated last: nothing native leaks if the checks above throw
        frame = AVFrame.Allocate();
        converter = new FrameConverter();
    }

    private static TimeSpan GetDuration(MediaSource source, Formats.AVStream stream)
    {
        if (stream.Duration > 0 && stream.Duration != AutoGen.ffmpeg.AV_NOPTS_VALUE && stream.TimeBase.Denominator != 0)
            return FFCodecFrameInfo.ToTimeSpan(stream.Duration, stream.TimeBase);
        long duration = source.FormatContext.Duration; // AV_TIME_BASE units
        return duration > 0 && duration != AutoGen.ffmpeg.AV_NOPTS_VALUE
            ? TimeSpan.FromTicks(duration * (TimeSpan.TicksPerSecond / AutoGen.ffmpeg.AV_TIME_BASE))
            : TimeSpan.Zero;
    }

    /// <summary>
    /// Decodes the next video frame into a newly allocated <see cref="SKBitmap"/>.
    /// </summary>
    /// <param name="frameInfo">
    /// Receives timing information describing the decoded frame.
    /// </param>
    /// <returns>
    /// A decoded <see cref="SKBitmap"/> with the size and color type of <see cref="Info"/>,
    /// or <see langword="null"/> if the end of the video stream has been reached.
    /// </returns>
    /// <exception cref="ObjectDisposedException">
    /// The decoder has already been disposed.
    /// </exception>
    /// <exception cref="Exceptions.FFmpegException">
    /// Reading or decoding failed.
    /// </exception>
    /// <remarks>
    /// The returned bitmap must be disposed by the caller when it is no longer needed.
    /// </remarks>
    public SKBitmap? NextImage(out FFCodecFrameInfo frameInfo)
    {
        CheckDisposed();
        SKBitmap image = new(Info);
        try
        {
            if (NextImage(image, out frameInfo))
                return image;
            image.Dispose();
            return null;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    private void CheckDisposed()
    {
        if (disposedValue)
            throw new ObjectDisposedException(GetType().FullName);
    }

    /// <summary>
    /// Decodes the next video frame into an existing <see cref="SKBitmap"/>.
    /// </summary>
    /// <param name="skImage">
    /// The destination bitmap that receives the decoded frame. If it has no pixels, they are allocated
    /// as described by <see cref="Info"/>.
    /// </param>
    /// <param name="frameInfo">
    /// Receives timing information describing the decoded frame.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a frame was decoded successfully; otherwise,
    /// <see langword="false"/> if the end of the video stream has been reached.
    /// </returns>
    /// <remarks>
    /// <para>
    /// When the bitmap already matches the decoded frame dimensions and color type,
    /// the pixel data is copied directly.
    /// </para>
    /// <para>
    /// Otherwise, the frame is scaled and converted to the bitmap's size and color type.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// The decoder has already been disposed.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The bitmap's color type has no equivalent FFmpeg pixel format.
    /// </exception>
    /// <exception cref="Exceptions.FFmpegException">
    /// Reading or decoding failed.
    /// </exception>
    public bool NextImage(SKBitmap skImage, out FFCodecFrameInfo frameInfo)
    {
        frameInfo = default;
        CheckDisposed();
        if (!skImage.DrawsNothing)
            _ = FrameInterop.ToSupportedPixelFormat(skImage.ColorType);
        for (; ; )
        {
            AVResult32 res = mediaSource.ReadAndDecodeAVFrame(frame);
            if (res == AVResult32.EndOfFile)
                return false;
            res.ThrowIfError();
            if (res != streamIndex)
                continue;
            bool hasTimestamp = FFCodecFrameInfo.TryFromFrame(frame, out frameInfo);
            if (skipUntil is TimeSpan target && hasTimestamp)
            {
                TimeSpan end = frameInfo.TimeStamp + frameInfo.Duration;
                if (frameInfo.Duration > TimeSpan.Zero ? end <= target : frameInfo.TimeStamp < target)
                    continue; // before the seek target
            }
            skipUntil = null;
            break;
        }
        FrameInterop.CopyToBitmap(frame, skImage, converter, Info);
        return true;
    }

    /// <summary>
    /// Seeks to the specified playback position.
    /// </summary>
    /// <param name="time">
    /// The target playback position, in the timestamps of the stream (see <see cref="FFCodecFrameInfo.TimeStamp"/>).
    /// </param>
    /// <returns>
    /// An <see cref="AVResult32"/> indicating whether the seek operation succeeded.
    /// </returns>
    /// <remarks>
    /// FFmpeg seeks to the key frame before <paramref name="time"/>; the frames in between are decoded but skipped,
    /// so the next call to <see cref="NextImage(out FFCodecFrameInfo)"/> or
    /// <see cref="NextImage(SKBitmap, out FFCodecFrameInfo)"/> returns the frame that is visible at
    /// <paramref name="time"/>.
    /// </remarks>
    public AVResult32 Seek(TimeSpan time)
    {
        CheckDisposed();
        AVResult32 res = mediaSource.Seek(time, streamIndex);
        skipUntil = res.IsError ? null : time;
        return res;
    }

    /// <summary>
    /// Seeks to the specified frame number.
    /// </summary>
    /// <param name="frame">
    /// The zero-based frame index.
    /// </param>
    /// <returns>
    /// An <see cref="AVResult32"/> indicating whether the seek operation succeeded.
    /// </returns>
    /// <remarks>
    /// The frame index is converted into a time with <see cref="FrameRate"/> (and the start time of the stream),
    /// which is exact for constant-frame-rate video. If the frame rate is unknown, FFmpeg's frame based seeking is
    /// used, which only few formats support.
    /// </remarks>
    public AVResult32 Seek(long frame)
    {
        CheckDisposed();
        Rational rate = FrameRate;
        if (rate.Numerator > 0 && rate.Denominator > 0)
            return Seek(startTime + TimeSpan.FromTicks((long)Math.Round(frame * (double)rate.Denominator / rate.Numerator * TimeSpan.TicksPerSecond)));
        skipUntil = null;
        return mediaSource.Seek(frame, streamIndex);
    }

    /// <summary>
    /// Restarts decoding from the beginning of the video stream.
    /// </summary>
    /// <returns>
    /// An <see cref="AVResult32"/> indicating whether the operation succeeded.
    /// </returns>
    public AVResult32 Restart() => Seek(TimeSpan.Zero);

    #region Create

    /// <summary>Creates the decoder; the source is disposed if that fails.</summary>
    private static FFCodec2Skia Create(MediaSource source, int width, int height, SKColorType colorType)
    {
        try
        {
            return new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), width, height, colorType);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <inheritdoc cref="Create(string, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(string url, DeviceType deviceType = DeviceType.None)
        => Create(MediaSource.Open(url, deviceType: deviceType), 0, 0, SKColorType.Unknown);

    /// <inheritdoc cref="Create(string, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(string url, SKImageInfo info, DeviceType deviceType = DeviceType.None)
        => Create(MediaSource.Open(url, deviceType: deviceType), info.Width, info.Height, info.ColorType);

    /// <inheritdoc cref="Create(string, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(string url, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
        => Create(MediaSource.Open(url, options: codecOptions, deviceType: deviceType), 0, 0, SKColorType.Unknown);

    /// <summary>
    /// Opens a media source and creates a new <see cref="FFCodec2Skia"/> instance for decoding video frames.
    /// </summary>
    /// <param name="url">
    /// The path or URL of the media source to open.
    /// </param>
    /// <param name="info">
    /// Specifies the dimensions and <see cref="SKColorType"/> of the decoded images.
    /// Frames are scaled and converted as necessary to match this image information.
    /// A width or height of 0 keeps the size of the video, <see cref="SKColorType.Unknown"/> picks a color type.
    /// </param>
    /// <param name="codecOptions">
    /// A collection of codec and format options passed to FFmpeg when opening the media source.
    /// The available options depend on the selected demuxer and decoder.
    /// </param>
    /// <param name="deviceType">
    /// Specifies the hardware acceleration device to use for decoding. Use
    /// <see cref="DeviceType.None"/> for software decoding.
    /// </param>
    /// <returns>
    /// A new <see cref="FFCodec2Skia"/> instance.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The media does not contain a video stream, or <paramref name="info"/> specifies an unsupported color type.
    /// </exception>
    /// <exception cref="Exceptions.FFmpegException">
    /// The media could not be opened.
    /// </exception>
    /// <remarks>
    /// The best available video stream is selected automatically. All other streams are
    /// discarded during decoding. If the requested image size or color type differs from
    /// the source video, frames are converted using FFmpeg's software scaler before being
    /// returned.
    /// </remarks>
    public static FFCodec2Skia? Create(
        string url,
        SKImageInfo info,
        IDictionary<string, string> codecOptions,
        DeviceType deviceType = DeviceType.None)
        => Create(MediaSource.Open(url, options: codecOptions, deviceType: deviceType), info.Width, info.Height, info.ColorType);

    /// <inheritdoc cref="Create(string, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(Stream stream, DeviceType deviceType = DeviceType.None)
        => Create(MediaSource.Open(stream, deviceType: deviceType), 0, 0, SKColorType.Unknown);

    /// <inheritdoc cref="Create(Stream, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(Stream stream, SKImageInfo info, DeviceType deviceType = DeviceType.None)
        => Create(MediaSource.Open(stream, deviceType: deviceType), info.Width, info.Height, info.ColorType);

    /// <inheritdoc cref="Create(Stream, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(Stream stream, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
        => Create(MediaSource.Open(stream, options: codecOptions, deviceType: deviceType), 0, 0, SKColorType.Unknown);

    /// <summary>
    /// Opens a media source from a <see cref="Stream"/> and creates a new <see cref="FFCodec2Skia"/> instance for decoding video frames.
    /// </summary>
    /// <param name="stream">
    /// The stream containing the media data.
    /// </param>
    /// <param name="info">
    /// Specifies the dimensions and <see cref="SKColorType"/> of the decoded images.
    /// Frames are scaled and converted as necessary to match this image information.
    /// A width or height of 0 keeps the size of the video, <see cref="SKColorType.Unknown"/> picks a color type.
    /// </param>
    /// <param name="codecOptions">
    /// A collection of codec and format options passed to FFmpeg when opening the media source.
    /// The available options depend on the selected demuxer and decoder.
    /// </param>
    /// <param name="deviceType">
    /// Specifies the hardware acceleration device to use for decoding. Use
    /// <see cref="DeviceType.None"/> for software decoding.
    /// </param>
    /// <returns>
    /// A new <see cref="FFCodec2Skia"/> instance.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The media does not contain a video stream, or <paramref name="info"/> specifies an unsupported color type.
    /// </exception>
    /// <exception cref="Exceptions.FFmpegException">
    /// The media could not be opened.
    /// </exception>
    /// <remarks>
    /// The best available video stream is selected automatically. All other streams are
    /// discarded during decoding. If the requested image size or color type differs from
    /// the source video, frames are converted using FFmpeg's software scaler before being
    /// returned.
    /// </remarks>
    public static FFCodec2Skia? Create(Stream stream, SKImageInfo info, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
        => Create(MediaSource.Open(stream, options: codecOptions, deviceType: deviceType), info.Width, info.Height, info.ColorType);

    /// <inheritdoc cref="Create(IO.IOContext, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(IO.IOContext io, DeviceType deviceType = DeviceType.None)
        => Create(MediaSource.Open(io, deviceType: deviceType), 0, 0, SKColorType.Unknown);

    /// <inheritdoc cref="Create(IO.IOContext, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(IO.IOContext io, SKImageInfo info, DeviceType deviceType = DeviceType.None)
        => Create(MediaSource.Open(io, deviceType: deviceType), info.Width, info.Height, info.ColorType);

    /// <inheritdoc cref="Create(IO.IOContext, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(IO.IOContext io, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
        => Create(MediaSource.Open(io, options: codecOptions, deviceType: deviceType), 0, 0, SKColorType.Unknown);

    /// <summary>
    /// Opens a media source from an <see cref="IO.IOContext"/> and creates a new <see cref="FFCodec2Skia"/> instance for decoding video frames.
    /// </summary>
    /// <param name="io">
    /// The custom I/O context used to read the media data.
    /// </param>
    /// <param name="info">
    /// Specifies the dimensions and <see cref="SKColorType"/> of the decoded images.
    /// Frames are scaled and converted as necessary to match this image information.
    /// A width or height of 0 keeps the size of the video, <see cref="SKColorType.Unknown"/> picks a color type.
    /// </param>
    /// <param name="codecOptions">
    /// A collection of codec and format options passed to FFmpeg when opening the media source.
    /// The available options depend on the selected demuxer and decoder.
    /// </param>
    /// <param name="deviceType">
    /// Specifies the hardware acceleration device to use for decoding. Use
    /// <see cref="DeviceType.None"/> for software decoding.
    /// </param>
    /// <returns>
    /// A new <see cref="FFCodec2Skia"/> instance.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The media does not contain a video stream, or <paramref name="info"/> specifies an unsupported color type.
    /// </exception>
    /// <exception cref="Exceptions.FFmpegException">
    /// The media could not be opened.
    /// </exception>
    /// <remarks>
    /// The best available video stream is selected automatically. All other streams are
    /// discarded during decoding. If the requested image size or color type differs from
    /// the source video, frames are converted using FFmpeg's software scaler before being
    /// returned.
    /// </remarks>
    public static FFCodec2Skia? Create(IO.IOContext io, SKImageInfo info, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
        => Create(MediaSource.Open(io, options: codecOptions, deviceType: deviceType), info.Width, info.Height, info.ColorType);

    #endregion



    #region Dispose
    private bool disposedValue;

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposedValue)
            return;
        disposedValue = true;
        mediaSource.Dispose();
        frame.Dispose();
        converter.Dispose();
    }
    #endregion
}
