using FFmpeg.Codecs;
using FFmpeg.Formats;
using FFmpeg.Images;
using FFmpeg.Utils;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Windows.Media.Core;

namespace FFmpeg.Skia.Examples;
/// <summary>
/// Interaktionslogik für MediaSourceWindow.xaml
/// </summary>
public partial class MediaSourceWindow : Window
{
    MediaSource source = MediaSource.Open(file);
    int videoIndex;
    SKBitmap? bitmap = null;
    TimeSpan TimeStamp { get; set; }
    TimeSpan Duration => source.Streams[videoIndex].Duration * source.Streams[videoIndex].TimeBase;
    CancellationTokenSource cts = new();
    Task decodingTask = Task.CompletedTask;

    const string file = "mp4-example-video-download-full-hd-1920x1080.1min.mp4";

    public MediaSourceWindow()
    {
        InitializeComponent();
    }

    private void DecodingTask(object? obj)
    {
        if (obj is not CancellationToken token)
            throw new ArgumentException("obj must be an CancellationToken");
        AVFrame frame = AVFrame.Allocate(); // allocate a frame to decode the video into
        try
        {

            while (!token.IsCancellationRequested)
            {
                lock (source)
                {
                    AVResult32 result = source.ReadAndDecodeAVFrame(frame);
                    if (result == AVResult32.EndOfFile)
                    {
                        source.Seek(0).ThrowIfError(); // restart stream
                        result = source.ReadAndDecodeAVFrame(frame);
                    }
                    result.ThrowIfError();
                }
                TimeStamp = frame.GetPresentationTimestamp() * frame.TimeBase;
                if (bitmap == null)
                    bitmap = frame.ToSkiaBitmap();
                else
                    frame.CopyTo(bitmap);
                Task.Delay(frame.Duration * frame.TimeBase, token).Wait(token); // wait until next frame should be displayed
            }
        }
        catch (OperationCanceledException) { }
        finally { frame.Dispose(); }
    }

    private void Window_Unloaded(object sender, RoutedEventArgs e)
    {
        cts.Cancel();
        _ = decodingTask.Wait(1000); // give the decoding task 1s to stop
        cts.Dispose();
        source?.Dispose();
        bitmap?.Dispose();

    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Not needed if you are ok with using ffmpegs default decoder
        // source.GetCodec = (AVStream stream) => stream.CodecId == Codecs.CodecID.H264 ? Codec.FindDecoder("decoderName") : Codec.FindDecoder(stream.CodecId);
        // or source.SetCodec(Codec,videoIndex)
        videoIndex = source.FindBestStream(Utils.MediaType.Video);
        foreach (var stream in source.Streams)
            stream.Discard = Formats.DiscardFlags.All;
        // Discard everything but the main video stream
        source.Streams[videoIndex].Discard = Formats.DiscardFlags.Default;

    }

    private void canvas_PaintSurface(object sender, SkiaSharp.Views.Desktop.SKPaintGLSurfaceEventArgs e)
    {
        e.Surface.Canvas.Clear();

        if (bitmap != null)
        {
            // the decoding task always writes into skBitmaps internal buffer, so no need to lock
            var dest = e.Surface.Canvas.DeviceClipBounds.AspectFit(bitmap.Info.Size);
            e.Surface.Canvas.DrawBitmap(bitmap, dest, new SKPaint() { FilterQuality = SKFilterQuality.High }); // DrawBitmap does not have the right override, yet.          
            e.Surface.Canvas.DrawText($"{TimeStamp:mm\\:ss} / {Duration:mm\\:ss}",
                      30,
                      30,
                      SKTextAlign.Left,
                      new SKFont(SKTypeface.Default),
                      new SKPaint() { Color = SKColors.Red });
        }
        else
        {
            e.Surface.Canvas.DrawText("Press [Enter] or [Space] to start the video.",
                e.Surface.Canvas.DeviceClipBounds.MidX,
                e.Surface.Canvas.DeviceClipBounds.MidY - 10,
                SKTextAlign.Center,
                new SKFont(SKTypeface.Default),
                new SKPaint() { Color = SKColors.Red });
            e.Surface.Canvas.DrawText("Press [Left] for -10s and [Right] for +10s.",
                e.Surface.Canvas.DeviceClipBounds.MidX,
                e.Surface.Canvas.DeviceClipBounds.MidY + 10,
                SKTextAlign.Center,
                new SKFont(SKTypeface.Default),
                new SKPaint() { Color = SKColors.Red });
        }
    }


    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        lock (source)
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                // property Running not in the current nuget-package, yet
                if (decodingTask.IsCompleted)
                {
                    if (!cts.TryReset())
                    {
                        cts?.Dispose();
                        cts = new();
                    }
                    decodingTask = new(DecodingTask, cts.Token, cts.Token);
                    decodingTask.Start();
                }
                else
                    cts.Cancel();
            }
            if (e.Key == Key.Left)
            {
                TimeSpan seek = TimeSpan.FromSeconds(Math.Max(0, TimeStamp.TotalSeconds - 10));
                source.Seek(seek);
            }
            else if (e.Key == Key.Right)
            {
                TimeSpan seek = TimeSpan.FromSeconds(Math.Min((Duration.TotalSeconds), TimeStamp.TotalSeconds + 10));
                source.Seek(seek);
            }
        }
    }

}
