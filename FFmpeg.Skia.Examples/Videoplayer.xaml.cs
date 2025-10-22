using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
/// Interaktionslogik für Videoplayer.xaml
/// </summary>
public partial class Videoplayer : Window
{
    readonly object _lock = new();
    const string file = "mp4-example-video-download-full-hd-1920x1080.1min.mp4";
    readonly SKVideo skvideo = new SKVideo(file);
    readonly SkiaSharp.SKBitmap bitmap = new();
    FFmpeg.Skia.FFCodecFrameInfo frameInfo;
    public Videoplayer()
    {
        InitializeComponent();
        skvideo.FrameReadyToRender += Skvideo_FrameReadyToRender;
        skvideo.Ended += (_,_) => skvideo.Seek(0); // seek to the start
    }

    private void Skvideo_FrameReadyToRender(object? sender, (SkiaSharp.SKBitmap frame, FFCodecFrameInfo frameInfo) e)
    {
        if (bitmap.DrawsNothing)
            _ = bitmap.TryAllocPixels(e.frame.Info);
        
        e.frame.GetPixelSpan().CopyTo(bitmap.GetPixelSpan());
        frameInfo = e.frameInfo;
        bitmap.NotifyPixelsChanged();
    }
    private void canvas_PaintSurface(object sender, SkiaSharp.Views.Desktop.SKPaintGLSurfaceEventArgs e)
    {
        e.Surface.Canvas.Clear();
        if (!bitmap.DrawsNothing)
        {
            var dest = e.Surface.Canvas.DeviceClipBounds.AspectFit(bitmap.Info.Size);
            e.Surface.Canvas.DrawBitmap(bitmap, dest, new SKPaint() { FilterQuality = SKFilterQuality.High }); // DrawBitmap does not have the right override, yet.          
            e.Surface.Canvas.DrawText($"{frameInfo.TimeStamp:mm\\:ss} / {skvideo.Duration:mm\\:ss}",
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
                e.Surface.Canvas.DeviceClipBounds.MidY-10,
                SKTextAlign.Center,
                new SKFont(SKTypeface.Default),
                new SKPaint() { Color = SKColors.Red });
            e.Surface.Canvas.DrawText("Press [Left] for -10s and [Right] for +10s.",
                e.Surface.Canvas.DeviceClipBounds.MidX,
                e.Surface.Canvas.DeviceClipBounds.MidY+10,
                SKTextAlign.Center,
                new SKFont(SKTypeface.Default),
                new SKPaint() { Color = SKColors.Red });
        }
    }
    private bool running = false;
    private void canvas_KeyDown(object sender, KeyEventArgs e)
    {
        if(e.Key is Key.Enter or Key.Space)
        {
            // property Running not in the current nuget-package, yet
            if (!running)
                skvideo.Resume();
            else skvideo.Pause();
            running = !running;
        }
        if(e.Key == Key.Left)
        {
            TimeSpan seek = TimeSpan.FromSeconds(Math.Max(0, frameInfo.TimeStamp.TotalSeconds - 10));
            skvideo.Seek(seek);
        }
        else if(e.Key == Key.Right)
        {
            TimeSpan seek = TimeSpan.FromSeconds(Math.Min(skvideo.Duration.TotalSeconds, frameInfo.TimeStamp.TotalSeconds + 10));
            skvideo.Seek(seek);
        }
    }

    private void Window_Unloaded(object sender, RoutedEventArgs e)
    {
        skvideo.Dispose();
        bitmap.Dispose();
    }
}
