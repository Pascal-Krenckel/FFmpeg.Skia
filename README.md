# FFmpeg.Skia

**FFmpeg.Skia** extends the functionality of the [FFmpeg (.NET Wrapper)](../FFmpeg) project by integrating it with **[SkiaSharp](https://github.com/mono/SkiaSharp)**.  
It provides convenient tools to convert decoded FFmpeg frames (`AVFrame`) into Skia image objects (`SKImage`, `SKBitmap`) and classes for easily decoding and rendering video or image sequences using Skia.

---

## 📖 Overview

`FFmpeg.Skia` bridges the gap between **FFmpeg’s powerful decoding capabilities** and **SkiaSharp’s GPU-accelerated rendering**.
Using GPU Frames directly is not supported.
It allows .NET developers to display video frames or image sequences directly in Skia-based applications (such as MAUI, Avalonia, or WPF with Skia integration).

This library builds on top of the main `FFmpeg` project and offers:

- Extension methods to convert FFmpeg frames to Skia objects  
- Simplified video and image decoding with Skia-ready bitmaps  
- Real-time frame rendering via events and async video playback  

---

## 🚀 Getting Started

### 1. Prerequisites

- The [**FFmpeg**](https://www.nuget.org/packages/FFmpegDotNet) core project
- [**SkiaSharp**](https://www.nuget.org/packages/SkiaSharp/)
- FFmpeg binaries (see https://github.com/Pascal-Krenckel/FFmpeg)


### Nuget-Package

 [FFmpegDotNet.Skia](https://www.nuget.org/packages/FFmpegDotNet.Skia)

### FFmpeg 8 and FFmpeg 9

The package is built for both FFmpeg major versions from the same sources. The first number of the package version
is the FFmpeg major version, like for [FFmpegDotNet](https://www.nuget.org/packages/FFmpegDotNet):

| FFmpegDotNet.Skia | FFmpegDotNet | FFmpeg |
|---|---|---|
| 9.x (e.g. 9.2.4.0) | 9.x | 9.0 |
| 8.x (e.g. 8.2.4.0) | 8.x | 8.1 |

Use the one that matches the FFmpegDotNet version (and the FFmpeg binaries) of your application.
Both behave the same.

To build a specific version:

```bash
dotnet pack FFmpeg.Skia -c Release -p:FFmpegMajor=8
dotnet pack FFmpeg.Skia -c Release -p:FFmpegMajor=9   # default
```

### Notes

- FFmpeg stores straight (unpremultiplied) alpha. Skia objects created from frames use `SKAlphaType.Unpremul`
  (or `Opaque` for color types without alpha); premultiplied Skia images are converted when copied into frames.
- The crop values of a frame are applied: only the visible region is converted.
- Colors are converted with FFmpeg's scaler using the frame's color properties: the YUV matrix
  (`AVFrame.ColorSpace`: BT.601, BT.709, BT.2020, ...) and range (`AVFrame.ColorRange`). Untagged YUV is treated
  as BT.601 limited range, like everywhere in FFmpeg. Skia images are SDR with BT.709/sRGB primaries: HDR video
  (PQ, HLG) is tone mapped and wide gamuts (BT.2020, P3) are mapped to BT.709; when writing into a frame tagged as
  HDR or wide gamut, the Skia colors are mapped into it. YUV frames written from Skia without a matrix or range
  are tagged BT.601 / limited range.
- `AsSKBitmap`/`AsSKImage` share the frame's buffer without copying when the pixel format is supported by Skia.
- Videos with non-square pixels are decoded with square pixels by `FFCodec2Skia`, `SKVideo` and `SKAnimatedImage`
  (`SampleAspectRatio`). The display rotation (e.g. of phone videos) is not applied to the decoded images; it is
  available as `Rotation` and can be applied when drawing:

  ```csharp
  canvas.DrawRotated(bitmap, destination, video.Rotation, new SKSamplingOptions(SKCubicResampler.Mitchell));
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
| `Release.yml` | tag `v<SkiaVersion>`, e.g. `v2.4.0` | builds both packages (`8.<SkiaVersion>`, `9.<SkiaVersion>`), publishes them to NuGet and creates a GitHub release |
| `documentation.yml` | push to master, manually | builds the API documentation with DocFX and publishes it to GitHub Pages |

To release, set `SkiaVersion` in `FFmpeg.Skia/FFmpeg.Skia.csproj` and push the matching tag. Publishing uses NuGet trusted
publishing (a policy for this repository and `Release.yml` on nuget.org); GitHub Pages must use "GitHub Actions" as source.
Dependabot (`.github/dependabot.yml`) proposes updates for the actions and NuGet packages weekly.
