#define Copy
using SkiaSharp;
using System.Windows;
using System.Windows.Input;

namespace FFmpeg.Skia.Examples;
/// <summary>
/// Interaktionslogik für Videoplayer.xaml
/// </summary>
public partial class Videoplayer : Window
{
    private readonly object _lock = new();
    private const string file = "mp4-example-video-download-full-hd-1920x1080.1min.mp4";
    private readonly SKVideo skvideo = new(file);
#if Copy
    private readonly SkiaSharp.SKBitmap bitmap = new(); // <- dispose at window unload
#else 
    SkiaSharp.SKBitmap? bitmap;
#endif
    private FFmpeg.Skia.FFCodecFrameInfo frameInfo;
    public Videoplayer()
    {
        InitializeComponent();
        skvideo.FrameReadyToRender += Skvideo_FrameReadyToRender;
        skvideo.Ended += (_, _) => skvideo.Seek(0); // seek to the start
    }

    private void Skvideo_FrameReadyToRender(object? sender, (SkiaSharp.SKBitmap frame, FFCodecFrameInfo frameInfo) e)
    {
#if Copy
        if (bitmap.DrawsNothing)
            _ = bitmap.TryAllocPixels(e.frame.Info);

        e.frame.GetPixelSpan().CopyTo(bitmap.GetPixelSpan());
        frameInfo = e.frameInfo;
        bitmap.NotifyPixelsChanged();
#else
        frameInfo = e.frameInfo;
        bitmap = e.frame;
#endif
    }
    private void canvas_PaintSurface(object sender, SkiaSharp.Views.Desktop.SKPaintGLSurfaceEventArgs e)
    {
        e.Surface.Canvas.Clear();
        if (bitmap != null && !bitmap.DrawsNothing)
        {
            SKRectI dest = e.Surface.Canvas.DeviceClipBounds.AspectFit(bitmap.Info.Size);
            e.Surface.Canvas.DrawBitmap(bitmap, dest, new SKSamplingOptions(SKCubicResampler.Mitchell)); // DrawBitmap does not have the right override, yet.          
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
    private bool running = false;
    private void canvas_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            // property Running not in the current nuget-package, yet
            if (!running)
                skvideo.Resume();
            else
                skvideo.Pause();
            running = !running;
        }
        if (e.Key == Key.Left)
        {
            TimeSpan seek = TimeSpan.FromSeconds(Math.Max(0, frameInfo.TimeStamp.TotalSeconds - 10));
            _ = skvideo.Seek(seek);
        }
        else if (e.Key == Key.Right)
        {
            TimeSpan seek = TimeSpan.FromSeconds(Math.Min(skvideo.Duration.TotalSeconds, frameInfo.TimeStamp.TotalSeconds + 10));
            _ = skvideo.Seek(seek);
        }
    }

    private void Window_Unloaded(object sender, RoutedEventArgs e)
    {
        skvideo.Dispose();
#if Copy
        bitmap.Dispose();
#endif
    }
}
