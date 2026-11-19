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
     Its major version must match FFmpegDotNet (8.x for FFmpeg 8, 9.x for FFmpeg 9); the project selects it with
     `FFmpegMajor` (see `Directory.Build.props`, e.g. `dotnet build -p:FFmpegMajor=8`).
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
The bitmap passed to the handler is one of the buffers `SKVideo` decodes into, so it is copied here.
Keeping a reference and drawing it later also works, but the next frames are decoded into the same buffers, so a
frame drawn while it is being overwritten may show parts of two frames.

```csharp
    private void Skvideo_FrameReadyToRender(object? sender, (SkiaSharp.SKBitmap frame, FFCodecFrameInfo frameInfo) e)
    {
        frameInfo = e.frameInfo;
        bitmap = e.frame; // drawn later, see above
    }
```

Seeking in the `Ended` handler loops the video:

```csharp
    skvideo.Ended += (_, _) => skvideo.Seek(TimeSpan.Zero);
```

### FF2SkiaCodecWindow.xaml

Instead of using SKVideo this uses FFCodec2Skia directly.

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
                        codec.Restart(); // restart at the end of the video (decoding errors throw)
                }
                this.frameInfo = frameInfo;
                Thread.Sleep(frameInfo.Duration); // wait until next frame should be displayed
            }
        }
        catch (OperationCanceledException) { }
    }
```

### MediaSourceWindow.xaml

Decodes with the wrapper's `MediaSource` and converts every frame with `frame.CopyTo(bitmap)`.
The extension methods keep their conversion setup between calls, so this is fast for every frame.

### SKAnimatedImageWindow.xaml

Plays the video in a 5x5 grid with 25 `SKAnimatedImage`s that decode on a shared `FFmpegWorkQueue`.
The paint handler draws `Frame` of every image whenever the surface renders, independently of `ReadyToRender`:
`SKAnimatedImage` keeps the frame shown before untouched for one more frame, so this is safe.
