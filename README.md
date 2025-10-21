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