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