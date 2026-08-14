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
/// to convert the frame into the requested Skia color type.
/// </para>
/// </remarks>
public sealed class FFCodec2Skia : IDisposable
{
    private readonly MediaSource mediaSource;
    private Images.SwsContext? swsContext;

    private readonly AVFrame frame;
    private readonly int streamIndex = -1;


    /// <summary>
    /// Gets the image information describing decoded frames.
    /// </summary>
    /// <remarks>
    /// The image information specifies the dimensions and color type of the
    /// <see cref="SKBitmap"/> instances produced by this decoder.
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
    public TimeSpan Duration { get; }

    private FFCodec2Skia(MediaSource mediaSource, int streamIndex, int targetWidth, int targetHeight, SKColorType colorType)
    {
        this.mediaSource = mediaSource;
        this.streamIndex = streamIndex;
        for (int i = 0; i < streamIndex; i++)
            mediaSource.Streams[i].Discard = Formats.DiscardFlags.All;
        for (int i = streamIndex + 1; i < mediaSource.Streams.Count; i++)
            mediaSource.Streams[i].Discard = Formats.DiscardFlags.All;

        targetWidth = targetWidth > 0 ? targetWidth : mediaSource.CodecContexts[streamIndex].Width;
        targetHeight = targetHeight > 0 ? targetHeight : mediaSource.CodecContexts[streamIndex].Height;

        if (!colorType.HasFFmpegEquivalent() && colorType != SKColorType.Unknown)
        {
            mediaSource.Dispose();
            throw new ArgumentException(nameof(colorType));
        }
        if (colorType == SKColorType.Unknown && mediaSource.CodecContexts[streamIndex].PixelFormat.HasSkiaEquivalent())
            colorType = mediaSource.CodecContexts[streamIndex].PixelFormat.ToSkiaColorType();
        else if (colorType == SKColorType.Unknown)
            colorType = SKImageInfo.PlatformColorType.HasFFmpegEquivalent() ? SKImageInfo.PlatformColorType : SKColorType.Rgba8888;
        frame = AVFrame.Allocate();
        Frames = mediaSource.Streams[streamIndex].NumberOfFrames;
        Duration = mediaSource.Streams[streamIndex].Duration * mediaSource.Streams[streamIndex].TimeBase;
        FrameRate = mediaSource.CodecContexts[streamIndex].FrameRate;

        Info = new SKImageInfo(targetWidth, targetHeight, colorType);
    }



    /// <summary>
    /// Decodes the next video frame into a newly allocated <see cref="SKBitmap"/>.
    /// </summary>
    /// <param name="frameInfo">
    /// Receives timing information describing the decoded frame.
    /// </param>
    /// <returns>
    /// A decoded <see cref="SKBitmap"/>, or <see langword="null"/> if the end of the
    /// video stream has been reached.
    /// </returns>
    /// <exception cref="ObjectDisposedException">
    /// The decoder has already been disposed.
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
            return !NextImage(image, out frameInfo) ? null : image;
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
    /// The destination bitmap that receives the decoded frame.
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
    /// the pixel data is copied directly whenever possible.
    /// </para>
    /// <para>
    /// Otherwise, the frame is converted using an internal
    /// <see cref="SwsContext"/> before being written into the bitmap.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// The decoder has already been disposed.
    /// </exception>
    public bool NextImage(SKBitmap skImage, out FFCodecFrameInfo frameInfo)
    {
        frameInfo = default;
        AVResult32 res;
        do
        {
            CheckDisposed();
            if ((res = mediaSource.ReadAndDecodeAVFrame(frame)).IsError)
                return false;
        } while (res != streamIndex);
        frameInfo = new()
        {
            Duration = frame.Duration * frame.TimeBase,
            TimeStamp = frame.GetPresentationTimestamp() * frame.TimeBase,
        };
        CheckDisposed();
        if (CheckCopy(skImage.Info))
        {
            Extensions.CopyFrame(frame, skImage);
        }
        else
        {
            if (skImage.DrawsNothing)
            {
                SKColorType colorType = frame.PixelFormat.ToBestSkiaColorType();
                if (!skImage.TryAllocPixels(new(frame.Width, frame.Height, colorType, SKAlphaType.Unpremul)))
                    return false;
            }
            else if (!skImage.Info.ColorType.HasFFmpegEquivalent())
                return false;
            swsContext = GetSwsContext(frame, skImage.Info);
            swsContext.Convert(frame, skImage.GetPixels()).ThrowIfError();
        }
        skImage.NotifyPixelsChanged();
        return true;
    }

    /// <summary>
    /// Seeks to the specified playback position.
    /// </summary>
    /// <param name="time">
    /// The target playback position.
    /// </param>
    /// <returns>
    /// An <see cref="AVResult32"/> indicating whether the seek operation succeeded.
    /// </returns>
    /// <remarks>
    /// The next call to <see cref="NextImage(out FFCodecFrameInfo)"/> or
    /// <see cref="NextImage(SKBitmap, out FFCodecFrameInfo)"/> decodes the first frame
    /// at or after the requested position.
    /// </remarks>
    public AVResult32 Seek(TimeSpan time)
    {
        CheckDisposed();
        return mediaSource.Seek(time, streamIndex);
    }

    /// <summary>
    /// Seeks to the specified frame number.
    /// </summary>
    /// <param name="frame">
    /// The target frame index.
    /// </param>
    /// <returns>
    /// An <see cref="AVResult32"/> indicating whether the seek operation succeeded.
    /// </returns>
    /// <remarks>
    /// Seeking by frame number is only supported by formats that provide sufficient
    /// indexing information.
    /// </remarks>
    public AVResult32 Seek(long frame)
    {
        CheckDisposed();
        return mediaSource.Seek(frame, streamIndex);
    }

    /// <summary>
    /// Restarts decoding from the beginning of the video stream.
    /// </summary>
    /// <returns>
    /// An <see cref="AVResult32"/> indicating whether the operation succeeded.
    /// </returns>
    public AVResult32 Restart() => Seek(0);

    private SwsContext GetSwsContext(AVFrame frame, SKImageInfo info)
    {
        if (swsContext != null && frame.Width == swsContext.SourceWidth && frame.Height == swsContext.SourceHeight && frame.Format == (int)swsContext.SourceFormat)
            return swsContext;
        swsContext?.Dispose();

        swsContext = frame.Width == info.Width && frame.Height == info.Height
            ? new SwsContext(frame.Width, frame.Height, frame.PixelFormat, info.Width, info.Height, info.ColorType.ToPixelFormat(), SwsAlgorithm.FastBilinear())
            : new SwsContext(frame.Width, frame.Height, frame.PixelFormat, info.Width, info.Height, info.ColorType.ToPixelFormat(), SwsAlgorithm.Bicubic());
        return swsContext;
    }

    private bool CheckCopy(SKImageInfo info) => frame.CroppedWidth == info.Width
            && frame.CroppedHeight == info.Height
            && frame.PixelFormat == info.ColorType.ToPixelFormat();


    #region Create

    /// <inheritdoc cref="Create(string, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(string url, DeviceType deviceType = DeviceType.None)
    {
        MediaSource source = MediaSource.Open(url, deviceType: deviceType);
        return source == null ? null : new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), 0, 0, SKColorType.Unknown);
    }

    /// <inheritdoc cref="Create(string, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(string url, SKImageInfo info, DeviceType deviceType = DeviceType.None)
    {
        MediaSource source = MediaSource.Open(url, deviceType: deviceType);
        return source == null
            ? null
            : new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), info.Width, info.Height, info.ColorType);
    }

    /// <inheritdoc cref="Create(string, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(string url, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
    {
        MediaSource source = MediaSource.Open(url, options: codecOptions, deviceType: deviceType);
        return source == null ? null : new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), 0, 0, SKColorType.Unknown);
    }

    /// <summary>
    /// Opens a media source and creates a new <see cref="FFCodec2Skia"/> instance for decoding video frames.
    /// </summary>
    /// <param name="url">
    /// The path or URL of the media source to open.
    /// </param>
    /// <param name="info">
    /// Specifies the dimensions and <see cref="SKColorType"/> of the decoded images.
    /// Frames are scaled and converted as necessary to match this image information.
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
    /// A new <see cref="FFCodec2Skia"/> instance if the media source was opened successfully;
    /// otherwise, <see langword="null"/>.
    /// </returns>
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
    {
        MediaSource source = MediaSource.Open(url, options: codecOptions, deviceType: deviceType);
        return source == null
            ? null
            : new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), info.Width, info.Height, info.ColorType);
    }

    /// <inheritdoc cref="Create(string, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(Stream stream, DeviceType deviceType = DeviceType.None)
    {
        MediaSource source = MediaSource.Open(stream, deviceType: deviceType);
        return source == null ? null : new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), 0, 0, SKColorType.Unknown);
    }

    /// <inheritdoc cref="Create(Stream, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(Stream stream, SKImageInfo info, DeviceType deviceType = DeviceType.None)
    {
        MediaSource source = MediaSource.Open(stream, deviceType: deviceType);
        return source == null
            ? null
            : new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), info.Width, info.Height, info.ColorType);
    }

    /// <inheritdoc cref="Create(Stream, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(Stream stream, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
    {
        MediaSource source = MediaSource.Open(stream, options: codecOptions, deviceType: deviceType);
        return source == null ? null : new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), 0, 0, SKColorType.Unknown);
    }

    /// <summary>
    /// Opens a media source from a <see cref="Stream"/> and creates a new <see cref="FFCodec2Skia"/> instance for decoding video frames.
    /// </summary>
    /// <param name="stream">
    /// The stream containing the media data.
    /// </param>
    /// <param name="info">
    /// Specifies the dimensions and <see cref="SKColorType"/> of the decoded images.
    /// Frames are scaled and converted as necessary to match this image information.
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
    /// A new <see cref="FFCodec2Skia"/> instance if the media source was opened successfully;
    /// otherwise, <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// The best available video stream is selected automatically. All other streams are
    /// discarded during decoding. If the requested image size or color type differs from
    /// the source video, frames are converted using FFmpeg's software scaler before being
    /// returned.
    /// </remarks>
    public static FFCodec2Skia? Create(Stream stream, SKImageInfo info, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
    {
        MediaSource source = MediaSource.Open(stream, options: codecOptions, deviceType: deviceType);
        return source == null
            ? null
            : new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), info.Width, info.Height, info.ColorType);
    }

    /// <inheritdoc cref="Create(IO.IOContext, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(IO.IOContext io, DeviceType deviceType = DeviceType.None)
    {
        MediaSource source = MediaSource.Open(io, deviceType: deviceType);
        return source == null ? null : new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), 0, 0, SKColorType.Unknown);
    }
   
    /// <inheritdoc cref="Create(IO.IOContext, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(IO.IOContext io, SKImageInfo info, DeviceType deviceType = DeviceType.None)
    {
        MediaSource source = MediaSource.Open(io, deviceType: deviceType);
        return source == null
            ? null
            : new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), info.Width, info.Height, info.ColorType);
    }

    /// <inheritdoc cref="Create(IO.IOContext, SKImageInfo, IDictionary{string, string}, DeviceType)"/>
    public static FFCodec2Skia? Create(IO.IOContext io, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
    {
        MediaSource source = MediaSource.Open(io, options: codecOptions, deviceType: deviceType);
        return source == null ? null : new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), 0, 0, SKColorType.Unknown);
    }

    /// <summary>
    /// Opens a media source from an <see cref="IO.IOContext"/> and creates a new <see cref="FFCodec2Skia"/> instance for decoding video frames.
    /// </summary>
    /// <param name="io">
    /// The custom I/O context used to read the media data.
    /// </param>
    /// <param name="info">
    /// Specifies the dimensions and <see cref="SKColorType"/> of the decoded images.
    /// Frames are scaled and converted as necessary to match this image information.
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
    /// A new <see cref="FFCodec2Skia"/> instance if the media source was opened successfully;
    /// otherwise, <see langword="null"/>.
    /// </returns>
    /// <remarks>
    /// The best available video stream is selected automatically. All other streams are
    /// discarded during decoding. If the requested image size or color type differs from
    /// the source video, frames are converted using FFmpeg's software scaler before being
    /// returned.
    /// </remarks>
    public static FFCodec2Skia? Create(IO.IOContext io, SKImageInfo info, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
    {
        MediaSource source = MediaSource.Open(io, options: codecOptions, deviceType: deviceType);
        return source == null
            ? null
            : new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), info.Width, info.Height, info.ColorType);
    }

    #endregion



    #region Dispose
    private bool disposedValue;
    private void Dispose(bool disposing)
    {
        if (!disposedValue)
        {
            if (disposing)
            {
                mediaSource.Dispose();
                frame.Dispose();
                swsContext?.Dispose();
            }

            // TODO: Nicht verwaltete Ressourcen (nicht verwaltete Objekte) freigeben und Finalizer überschreiben
            // TODO: Große Felder auf NULL setzen
            disposedValue = true;
        }
    }

    // // TODO: Finalizer nur überschreiben, wenn "Dispose(bool disposing)" Code für die Freigabe nicht verwalteter Ressourcen enthält
    // ~FFCodec2Skia()
    // {
    //     // Ändern Sie diesen Code nicht. Fügen Sie Bereinigungscode in der Methode "Dispose(bool disposing)" ein.
    //     Dispose(disposing: false);
    // }

    /// <inheritdoc />
    public void Dispose()
    {
        // Ändern Sie diesen Code nicht. Fügen Sie Bereinigungscode in der Methode "Dispose(bool disposing)" ein.
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
    #endregion
}
