# FFmpeg.Skia

**FFmpeg.Skia** extends the [FFmpeg .NET wrapper](https://github.com/Pascal-Krenckel/FFmpeg) ([FFmpegDotNet](https://www.nuget.org/packages/FFmpegDotNet)) with **[SkiaSharp](https://github.com/mono/SkiaSharp)** integration.
It converts decoded FFmpeg frames (`AVFrame`) into Skia objects (`SKImage`, `SKBitmap`) and back, and provides classes for decoding and playing videos and animated images with Skia.

📚 **[API Documentation](https://pascal-krenckel.github.io/FFmpeg.Skia/api/FFmpeg.Skia.html)**

---

## 📖 Overview

`FFmpeg.Skia` combines FFmpeg's decoding with Skia's rendering, e.g. in MAUI, Avalonia, WPF or WinUI applications that use SkiaSharp.

- **Extension methods** to convert between `AVFrame` and `SKBitmap`/`SKImage` (`ToSKBitmap`, `AsSKImage`, `CopyTo`, `ToAVFrame`, ...) and to draw frames (`SKCanvas.DrawFrame`)
- **`FFCodec2Skia`**: decodes a video (or image) frame by frame into `SKBitmap`s, like `SKCodec`, with exact seeking
- **`SKVideo`**: plays a video on a background thread and raises an event for each frame to render
- **`SKAnimatedImage`**: animated images (GIF, APNG, WebP, ...) or videos, decoded manually or played automatically

Conversions apply the frame's crop and color properties (YUV matrix, range, HDR), and frames in a pixel format Skia supports are shared without copying.

---

## 🚀 Getting Started

### Installation

```bash
dotnet add package FFmpegDotNet.Skia
```

FFmpeg.Skia is built for FFmpeg 8 and FFmpeg 9 from the same sources. The first number of the package version
is the FFmpeg major version, like for [FFmpegDotNet](https://www.nuget.org/packages/FFmpegDotNet):

| FFmpegDotNet.Skia | FFmpegDotNet | FFmpeg |
|---|---|---|
| 9.x (e.g. 9.3.0.0) | 9.x | 9.0 |
| 8.x (e.g. 8.3.0.0) | 8.x | 8.1 |

Use the one that matches the FFmpegDotNet version and the FFmpeg binaries of your application; both behave the same.
The FFmpeg binaries are not included: install FFmpeg, or use [FFmpegDotNet.bin.winx64](https://www.nuget.org/packages/FFmpegDotNet.bin.winx64) of the same major version on Windows.

### Loading FFmpeg

```csharp
FFmpegLoader.Initialize();                       // searches the application directory and the PATH
FFmpegLoader.Initialize(@"C:\tools\ffmpeg\bin"); // or a specific directory
```

---

## ⚡ Quick Start

### Frames ↔ Skia

```csharp
using FFmpeg;
using FFmpeg.Images;
using FFmpeg.Skia;
using FFmpeg.Utils;
using SkiaSharp;

using MediaSource source = MediaSource.Open("video.mp4");
int videoStream = source.FindBestStream(MediaType.Video);
using AVFrame frame = AVFrame.Allocate();

AVResult32 result;
while (!(result = source.ReadAndDecodeAVFrame(frame)).IsError)
{
    if (result != videoStream)
        continue;

    using SKBitmap copy = frame.ToSKBitmap();   // an independent copy
    using SKImage shared = frame.AsSKImage();   // shares the frame's pixels if Skia supports its format
    canvas.DrawFrame(frame, new SKRect(0, 0, 640, 360), SKSamplingOptions.Default);
}

// and back, e.g. for encoding
using AVFrame yuv = bitmap.ToAVFrame(PixelFormat.YUV420P);
```

### Decoding into bitmaps: `FFCodec2Skia`

```csharp
using FFCodec2Skia codec = FFCodec2Skia.Create("video.mp4")!;
using SKBitmap bitmap = new(codec.Info);

codec.Seek(TimeSpan.FromSeconds(10));          // the next image is the one visible at 10 s
while (codec.NextImage(bitmap, out FFCodecFrameInfo info))
{
    // bitmap holds the frame shown at info.TimeStamp for info.Duration
}
```

### Playing a video: `SKVideo`

```csharp
using SKVideo video = new("video.mp4");
video.FrameReadyToRender += (_, e) =>
{
    // e.frame is reused by SKVideo: draw or copy it here, do not keep it
};
video.Ended += (_, _) => video.Seek(TimeSpan.Zero); // seeking in Ended loops the video
video.Start();
```

### Animated images: `SKAnimatedImage`

```csharp
using SKAnimatedImage animation = SKAnimatedImage.Create("animation.gif");
animation.ReadyToRender += (_, _) => view.InvalidateSurface();
animation.Start(looping: true);

// in the paint handler (Frame may be drawn from a render loop at any time)
canvas.DrawRotated(animation.Frame, destination, animation.Rotation, SKSamplingOptions.Default);
```

For manual decoding, call `DecodeNextImage()` (or `DecodeNextImageAsync()` with an `FFmpegWorkQueue`) instead of `Start`.

The [examples](https://github.com/Pascal-Krenckel/FFmpeg.Skia/tree/master/FFmpeg.Skia.Examples) show these classes in a WPF application.

---

## 📝 Notes

- **Alpha:** FFmpeg stores straight (unpremultiplied) alpha. Skia objects created from frames use `SKAlphaType.Unpremul`
  (or `Opaque` for color types without alpha); premultiplied Skia images are converted when copied into frames.
- **Crop:** the crop values of a frame are applied: only the visible region is converted.
- **Colors:** frames are converted with FFmpeg's scaler using their color properties: the YUV matrix
  (`AVFrame.ColorSpace`: BT.601, BT.709, BT.2020, ...) and range (`AVFrame.ColorRange`). Untagged YUV is treated
  as BT.601 limited range, like everywhere in FFmpeg. Skia images are SDR with BT.709/sRGB primaries: HDR video
  (PQ, HLG) is tone mapped and wide gamuts (BT.2020, P3) are mapped to BT.709; when writing into a frame tagged as
  HDR or wide gamut, the Skia colors are mapped into it. YUV frames written from Skia without a matrix or range
  are tagged BT.601 / limited range.
- **Sharing:** `AsSKBitmap`/`AsSKImage` (and `DrawFrame`) share the frame's buffer without copying when Skia supports
  its pixel format; treat such bitmaps as read-only.
- **Bitmaps allocated for frames** use the frame's color type if Skia supports it, otherwise an 8 bit color type.
- **Pixel aspect ratio and rotation:** videos with non-square pixels are decoded with square pixels by `FFCodec2Skia`,
  `SKVideo` and `SKAnimatedImage` (`SampleAspectRatio`). The display rotation (e.g. of phone videos) is not applied to
  the decoded images; it is available as `Rotation` and applied when drawing with `DrawRotated`:

  ```csharp
  canvas.DrawRotated(bitmap, destination, video.Rotation, new SKSamplingOptions(SKCubicResampler.Mitchell));
  ```
- **Threads:** `FFCodec2Skia` is not thread-safe. `SKVideo` and `SKAnimatedImage` raise their events on thread-pool
  threads; they may be disposed from any thread, including their event handlers.
- **Hardware decoding:** frames decoded on the GPU are copied to memory by `MediaSource.ReadAndDecodeAVFrame`;
  GPU frames cannot be drawn directly.

---

## 🛠️ Development

### Building

```bash
dotnet pack FFmpeg.Skia -c Release -p:FFmpegMajor=8
dotnet pack FFmpeg.Skia -c Release -p:FFmpegMajor=9   # default
```

### Tests

`FFmpeg.Skia.Tests` (MSTest) uses the Windows x64 FFmpeg binaries (`FFmpegDotNet.bin.winx64`) that match `FFmpegMajor`:

```bash
dotnet test FFmpeg.Skia.Tests -p:FFmpegMajor=8
dotnet test FFmpeg.Skia.Tests -p:FFmpegMajor=9
```

The small media files in `FFmpeg.Skia.Tests/Test-Files` are created by `make_test_files.py` (PyAV).

### GitHub Actions

| Workflow | Runs on | Does |
|---|---|---|
| `tests.yml` | push to master, pull requests | builds the library and the examples and runs the tests for FFmpeg 8 and 9 (Windows) |
| `Release.yml` | tag `v<SkiaVersion>`, e.g. `v3.0.0` | builds both packages (`8.<SkiaVersion>`, `9.<SkiaVersion>`), publishes them to NuGet and creates a GitHub release |
| `documentation.yml` | push to master, manually | builds the API documentation with DocFX and publishes it to GitHub Pages |

To release, set `SkiaVersion` in `FFmpeg.Skia/FFmpeg.Skia.csproj` and push the matching tag. Publishing uses NuGet trusted
publishing (a policy for this repository and `Release.yml` on nuget.org); GitHub Pages must use "GitHub Actions" as source.
Dependabot (`.github/dependabot.yml`) proposes updates for the actions and NuGet packages weekly.

---

## 📄 License

LGPL-3.0, see [LICENSE.txt](https://github.com/Pascal-Krenckel/FFmpeg.Skia/blob/master/LICENSE.txt).
