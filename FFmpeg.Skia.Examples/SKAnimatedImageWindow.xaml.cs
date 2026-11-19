using FFmpeg.Threading;
using SkiaSharp;
using SkiaSharp.Views.WPF;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
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
/// Interaktionslogik für SKAnimatedImageWindow.xaml
/// </summary>
public partial class SKAnimatedImageWindow : Window
{
    private const string file = "mp4-example-video-download-full-hd-1920x1080.1min.mp4";
    SKAnimatedImage[] images = [];
    SKGLElement[] elements = [];
    readonly int width = 5;
    readonly int height = 5;
    public SKAnimatedImageWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var queue = new FFmpegWorkQueue(8);
        int count = width * height;
        images = new SKAnimatedImage[count];
        elements = new SKGLElement[count];
        //for (int x = 0; x < width; x++)
        //    grid.ColumnDefinitions.Add(new());
        //for (int y = 0; y < height; y++)
        //    grid.RowDefinitions.Add(new());
        for (int i = 0; i < count; i++)
            InitialzeImage(i, queue);

        for (int i = 0; i < count; i++)
            images[i].Start(true);
        elements[0] = new();
        elements[0].PaintSurface += (sender,e) => SKAnimatedImageWindow_PaintSurface(0,e);
        elements[0].RenderContinuously = true;
        grid.Children.Add(elements[0]);
    }

    private void InitialzeImage(int i, FFmpegWorkQueue? queue)
    {
        images[i] = SKAnimatedImage.Create(file,workQueue: queue);
        
        //elements[i] = new SKGLElement();
        //Grid.SetColumn(elements[i], i % width);
        //Grid.SetRow(elements[i], i / width);
        //elements[i].PaintSurface += (sender,e) => SKAnimatedImageWindow_PaintSurface(i,e);
        //elements[i].RenderContinuously = true;
        //grid.Children.Add(elements[i]);
    }


    private void SKAnimatedImageWindow_PaintSurface(int index, SkiaSharp.Views.Desktop.SKPaintGLSurfaceEventArgs e)
    {
        var bounds = e.Surface.Canvas.DeviceClipBounds;
        var imageSize = new SKSize(bounds.Width/width, bounds.Height/height);

        for(int x = 0; x < width; x++)
            for(int y = 0; y < height; y++)
            {
                index = x + y * width;
                if (images[index].Frame.DrawsNothing)
                    continue;
                SKRect localBounds = SKRect.Create(imageSize.Width * x, imageSize.Height * y, imageSize.Width, imageSize.Height);
                var dst = localBounds.AspectFit(images[index].Frame.Info.Size);
                e.Surface.Canvas.DrawBitmap(images[index].Frame, dst, new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
            }

        //if (images[index].Frame.DrawsNothing) return;
        //var dst = e.Surface.Canvas.DeviceClipBounds.AspectFit(images[index].Frame.Info.Size);
        //e.Surface.Canvas.DrawBitmap(images[index].Frame, dst, new SkiaSharp.SKSamplingOptions(SkiaSharp.SKCubicResampler.Mitchell));
    }

    private void Window_Unloaded(object sender, RoutedEventArgs e)
    {
        Task.WaitAll(images.Select(i => i.DisposeAsync().AsTask()));
        for (int i = 0; i < images.Length; i++)
            elements[i]?.Dispose();
    }
}
