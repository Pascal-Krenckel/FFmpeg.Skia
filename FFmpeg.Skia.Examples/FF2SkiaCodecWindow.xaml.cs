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

namespace FFmpeg.Skia.Examples;
/// <summary>
/// Interaktionslogik für FF2SkiaCodecWindow.xaml
/// </summary>
public partial class FF2SkiaCodecWindow : Window
{
    const string file = "mp4-example-video-download-full-hd-1920x1080.1min.mp4";
    FFmpeg.Skia.FFCodec2Skia codec = FFCodec2Skia.Create(file)!;
    SKBitmap skBitmap = new();
    CancellationTokenSource cts = new();
    Task decodingTask = Task.CompletedTask;
    FFCodecFrameInfo frameInfo;
    public FF2SkiaCodecWindow()
    {
        InitializeComponent();
    }

    private void DecodingTask(object? obj)
    {
        // just for concurrent rw
        FFCodecFrameInfo frameInfo;
        if (obj is not CancellationToken token) throw new ArgumentException("obj must be an CancellationToken");
        try
        {
            while (!token.IsCancellationRequested)
            {
                lock (codec)
                {
                    if (!codec.NextImage(skBitmap, out frameInfo))
                        codec.Restart(); // automatically restart if finished or error
                }
                this.frameInfo = frameInfo;
                Task.Delay(frameInfo.Duration, token).Wait(token); // wait until next frame should be displayed
            }
        }
        catch (OperationCanceledException) { }
    }

    private void canvas_PaintSurface(object sender, SkiaSharp.Views.Desktop.SKPaintGLSurfaceEventArgs e)
    {
        e.Surface.Canvas.Clear();

        if (!skBitmap.DrawsNothing)
        {
            // the decoding task always writes into skBitmaps internal buffer, so no need to lock
            // keep in mind, frameInfo will also be changed, but in this example its ok even if we have concurrent rw
            // but to be save while decoding and drawing frameInfo is transferred to a local variable
            FFCodecFrameInfo frameInfo;
            frameInfo = this.frameInfo;
            var dest = e.Surface.Canvas.DeviceClipBounds.AspectFit(skBitmap.Info.Size);
            e.Surface.Canvas.DrawBitmap(skBitmap, dest, new SKPaint() { FilterQuality = SKFilterQuality.High }); // DrawBitmap does not have the right override, yet.          
            e.Surface.Canvas.DrawText($"{frameInfo.TimeStamp:mm\\:ss} / {codec.Duration:mm\\:ss}",
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

    private void Window_Unloaded(object sender, RoutedEventArgs e)
    {
        cts.Cancel();
        decodingTask.Wait(1_000); // wait for up to 1s for the decoding task to finish
        codec.Dispose();
        skBitmap.Dispose();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        lock (codec)
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
                else cts.Cancel();
            }
            if (e.Key == Key.Left)
            {
                TimeSpan seek = TimeSpan.FromSeconds(Math.Max(0, frameInfo.TimeStamp.TotalSeconds - 10));
                codec.Seek(seek);
            }
            else if (e.Key == Key.Right)
            {
                TimeSpan seek = TimeSpan.FromSeconds(Math.Min(codec.Duration.TotalSeconds, frameInfo.TimeStamp.TotalSeconds + 10));
                codec.Seek(seek);
            }
        }
    }
}
