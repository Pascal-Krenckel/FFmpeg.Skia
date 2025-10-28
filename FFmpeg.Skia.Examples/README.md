# FFmpeg.Skia.Examples

## Overview
This project demonstrates how to use FFmpeg with SkiaSharp for video processing. It provides example code to integrate FFmpeg with the SkiaSharp library, allowing you to work with multimedia files in a cross-platform environment.

## Dependencies

This project relies on the following NuGet packages:

### 1. **FFmpegDotNet**
   - **Purpose**: Provides a .NET wrapper for FFmpeg, enabling video and audio processing in your .NET application.
   - **NuGet Link**: [FFmpegDotNet](https://www.nuget.org/packages/FFmpegDotNet)
   - **Installation**: 
     ```bash
     dotnet add package FFmpegDotNet
     ```

### 2. **FFmpegDotNet.Skia**
   - **Purpose**: Adds support for FFmpeg's integration with SkiaSharp, enabling rendering and manipulation of video frames with SkiaSharp.
   - **NuGet Link**: [FFmpegDotNet.Skia](https://www.nuget.org/packages/FFmpegDotNet.Skia)
   - **Installation**: 
     ```bash
     dotnet add package FFmpegDotNet.Skia
     ```

### 3. **FFmpegDotNet.bin.winx64**
   - **Purpose**: Contains the necessary FFmpeg binary files for Windows (64-bit) for video and audio processing.
   - **NuGet Link**: [FFmpegDotNet.bin.winx64](https://www.nuget.org/packages/FFmpegDotNet.bin.winx64)
   - **Installation**:
     ```bash
     dotnet add package FFmpegDotNet.bin.winx64
     ```

### 4. **SkiaSharp.Views.WPF**
   - **Purpose**: Provides the SkiaSharp integration for WPF applications. It allows you to display and manipulate SkiaSharp content in a WPF environment.
   - **NuGet Link**: [SkiaSharp.Views.WPF](https://www.nuget.org/packages/SkiaSharp.Views.WPF)
   - **Installation**:
     ```bash
     dotnet add package SkiaSharp.Views.WPF
     ```

## Installation Instructions

To get started with this project, you can install the dependencies using the following commands:

```bash
dotnet restore
```

## Examples

### Videoplayer.xaml

Videoplayer uses FFmpeg.Skia.SKVideo to render the video frames.
```csharp
    private void Skvideo_FrameReadyToRender(object? sender, (SkiaSharp.SKBitmap frame, FFCodecFrameInfo frameInfo) e)
    {
        if (bitmap.DrawsNothing)
            _ = bitmap.TryAllocPixels(e.frame.Info);
        
        e.frame.GetPixelSpan().CopyTo(bitmap.GetPixelSpan());
        frameInfo = e.frameInfo;
        bitmap.NotifyPixelsChanged();
    }
```
You could just save a reference to the front buffer instead of copying the data.
The SKBitmap stays valid until SKVideo gets disposed. However you may not draw or dispose the frame, as it is one of the buffers the AVFrame will be copied into.

```csharp
    private void Skvideo_FrameReadyToRender(object? sender, (SkiaSharp.SKBitmap frame, FFCodecFrameInfo frameInfo) e)
    {
        frameInfo = e.frameInfo;
        bitmap = e.frame;
    }
```

### FF2SkiaCodecWindows.xaml

Instead of using SKVideo this uses FF2SkiaCodec directly.

```csharp
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
```