using FFmpeg.HW;
using FFmpeg.Images;
using FFmpeg.Utils;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FFmpeg.Skia;
public sealed class FFCodec2Skia : IDisposable
{
    readonly MediaSource mediaSource;
    Images.SwsContext? swsContext;

    readonly AVFrame frame;
    readonly int streamIndex = -1;


    public SKImageInfo Info { get; private set; }
    public long Frames { get; }
    public Rational FrameRate { get; }

    public TimeSpan Duration { get; }

    private FFCodec2Skia(MediaSource mediaSource, int streamIndex, int targetWidth, int targetHeight, SKColorType colorType)
    {
        this.mediaSource = mediaSource;
        this.streamIndex = streamIndex;
        for (int i = 0; i < streamIndex; i++) mediaSource.Streams[i].Discard = Formats.DiscardFlags.All;
        for (int i = streamIndex + 1; i < mediaSource.Streams.Count; i++) mediaSource.Streams[i].Discard = Formats.DiscardFlags.All;

        targetWidth = targetWidth > 0 ? targetWidth : mediaSource.CodecContexts[streamIndex].Width;
        targetHeight = targetHeight > 0 ? targetHeight : mediaSource.CodecContexts[streamIndex].Height;

        if (!colorType.IsSupportedFFmpegFormat() && colorType != SKColorType.Unknown)
        {
            mediaSource.Dispose();
            throw new ArgumentException(nameof(colorType));
        }
        if (colorType == SKColorType.Unknown && mediaSource.CodecContexts[streamIndex].PixelFormat.IsSupportedSkiaColorType())
            colorType = mediaSource.CodecContexts[streamIndex].PixelFormat.ToSkiaColorType();
        else if (colorType == SKColorType.Unknown) colorType = SKImageInfo.PlatformColorType.IsSupportedFFmpegFormat() ? SKImageInfo.PlatformColorType : SKColorType.Rgba8888;
        frame = AVFrame.Allocate();
        Frames = mediaSource.Streams[streamIndex].NumberOfFrames;
        Duration = mediaSource.Streams[streamIndex].Duration * mediaSource.Streams[streamIndex].TimeBase;
        FrameRate = mediaSource.CodecContexts[streamIndex].FrameRate;

        Info = new SKImageInfo(targetWidth, targetHeight, colorType);
    }



    public SKBitmap? NextImage(out FFCodecFrameInfo frameInfo)
    {
        SKBitmap image = new(Info);
        try
        {
            if (!NextImage(image, out frameInfo)) return null;
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    public bool NextImage(SKBitmap skImage, out FFCodecFrameInfo frameInfo)
    {

        if (disposedValue) throw new ObjectDisposedException(GetType().FullName);
        frameInfo = default;
        AVResult32 res;
        do
        {
            if ((res = mediaSource.ReadAndDecodeAVFrame(frame)).IsError) return false;
        } while (res != streamIndex);
        frameInfo = new()
        {
            Duration = frame.Duration * frame.TimeBase,
            TimeStamp = frame.GetPresentationTimestamp() * frame.TimeBase,
        };
        if (CheckCopy(skImage.Info))
        {            
            Extensions.CopyFrame(frame,skImage);
        }
        else
        {
            swsContext = GetSwsContext(frame, skImage.Info);
            swsContext.Convert(frame, skImage.GetPixels()).ThrowIfError();
        }
        skImage.NotifyPixelsChanged();
        return true;
    }

    public AVResult32 Seek(TimeSpan time) => mediaSource.Seek(time, streamIndex);
    public AVResult32 Seek(long frame) => mediaSource.Seek(frame, streamIndex);

    public AVResult32 Restart() => Seek(0);

    private SwsContext GetSwsContext(AVFrame frame, SKImageInfo info)
    {
        if (swsContext != null && frame.Width == swsContext.SourceWidth && frame.Height == swsContext.SourceHeight && frame.Format == (int)swsContext.SourceFormat)
            return swsContext;
        swsContext?.Dispose();
        if (frame.Width == info.Width && frame.Height == info.Height)
            swsContext = new SwsContext(frame.Width, frame.Height, frame.PixelFormat, info.Width, info.Height, info.ColorType.ToPixelFormat(), SwsAlgorithm.FastBilinear());
        else
            swsContext = new SwsContext(frame.Width, frame.Height, frame.PixelFormat, info.Width, info.Height, info.ColorType.ToPixelFormat(), SwsAlgorithm.Bicubic());
        return swsContext;
    }

    private bool CheckCopy(SKImageInfo info)
    {
        return frame.CroppedWidth == info.Width 
            && frame.CroppedHeight == info.Height 
            && frame.PixelFormat == info.ColorType.ToPixelFormat();
    }


    #region Create

    public static FFCodec2Skia? Create(string url, DeviceType deviceType = DeviceType.None)
    {
        var source = MediaSource.Open(url, deviceType: deviceType);
        if (source == null) return null;
        return new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), 0, 0, SKColorType.Unknown);
    }

    public static FFCodec2Skia? Create(string url, SKImageInfo info, DeviceType deviceType = DeviceType.None)
    {
        var source = MediaSource.Open(url, deviceType: deviceType);
        if (source == null) return null;
        return new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), info.Width, info.Height, info.ColorType);
    }

    public static FFCodec2Skia? Create(string url, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
    {
        var source = MediaSource.Open(url, options: codecOptions, deviceType: deviceType);
        if (source == null) return null;
        return new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), 0, 0, SKColorType.Unknown);
    }

    public static FFCodec2Skia? Create(string url, SKImageInfo info, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
    {
        var source = MediaSource.Open(url, options: codecOptions, deviceType: deviceType);
        if (source == null) return null;
        return new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), info.Width, info.Height, info.ColorType);
    }

    public static FFCodec2Skia? Create(Stream stream, DeviceType deviceType = DeviceType.None)
    {
        var source = MediaSource.Open(stream, deviceType: deviceType);
        if (source == null) return null;
        return new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), 0, 0, SKColorType.Unknown);
    }

    public static FFCodec2Skia? Create(Stream stream, SKImageInfo info, DeviceType deviceType = DeviceType.None)
    {
        var source = MediaSource.Open(stream, deviceType: deviceType);
        if (source == null) return null;
        return new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), info.Width, info.Height, info.ColorType);
    }

    public static FFCodec2Skia? Create(Stream stream, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
    {
        var source = MediaSource.Open(stream, options: codecOptions, deviceType: deviceType);
        if (source == null) return null;
        return new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), 0, 0, SKColorType.Unknown);
    }

    public static FFCodec2Skia? Create(Stream stream, SKImageInfo info, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
    {
        var source = MediaSource.Open(stream, options: codecOptions, deviceType: deviceType);
        if (source == null) return null;
        return new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), info.Width, info.Height, info.ColorType);
    }

    public static FFCodec2Skia? Create(IO.IOContext io, DeviceType deviceType = DeviceType.None)
    {
        var source = MediaSource.Open(io, deviceType: deviceType);
        if (source == null) return null;
        return new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), 0, 0, SKColorType.Unknown);
    }

    public static FFCodec2Skia? Create(IO.IOContext io, SKImageInfo info, DeviceType deviceType = DeviceType.None)
    {
        var source = MediaSource.Open(io, deviceType: deviceType);
        if (source == null) return null;
        return new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), info.Width, info.Height, info.ColorType);
    }

    public static FFCodec2Skia? Create(IO.IOContext io, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
    {
        var source = MediaSource.Open(io, options: codecOptions, deviceType: deviceType);
        if (source == null) return null;
        return new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), 0, 0, SKColorType.Unknown);
    }

    public static FFCodec2Skia? Create(IO.IOContext io, SKImageInfo info, IDictionary<string, string> codecOptions, DeviceType deviceType = DeviceType.None)
    {
        var source = MediaSource.Open(io, options: codecOptions, deviceType: deviceType);
        if (source == null) return null;
        return new FFCodec2Skia(source, source.FindBestStream(MediaType.Video), info.Width, info.Height, info.ColorType);
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

    public void Dispose()
    {
        // Ändern Sie diesen Code nicht. Fügen Sie Bereinigungscode in der Methode "Dispose(bool disposing)" ein.
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
    #endregion
}
