using FFmpeg.Images;
using System;
using System.Collections.Generic;
using System.Text;

namespace FFmpeg.Skia;

public static partial class Extensions
{
    /// <summary>
    /// Gets the FFmpeg pixel formats that can be represented directly by
    /// <see cref="SKColorType"/> without requiring an intermediate conversion.
    /// </summary>
    /// <remarks>
    /// The order of the formats reflects the preferred formats used when selecting
    /// the closest compatible Skia color type.
    /// </remarks>
    public static PixelFormat[] SKSupportedPixelFormats { get; } =
    [
        PixelFormat.BGRA,
        PixelFormat.Gray8,
        PixelFormat.RGBX,
        PixelFormat.RGBA,
        PixelFormat.RGB565LE,
        PixelFormat.RGBAF16LE,
        PixelFormat.RGBAF32LE
    ];

    /// <summary>
    /// Gets the <see cref="SKColorType"/> values that have a direct equivalent
    /// FFmpeg <see cref="PixelFormat"/>.
    /// </summary>
    /// <remarks>
    /// The order of the color types corresponds to
    /// <see cref="SKSupportedPixelFormats"/>.
    /// </remarks>
    public static SKColorType[] FFSupportedColorTypes { get; } =
    [
        SKColorType.Bgra8888,
        SKColorType.Rgb888x,
        SKColorType.Rgba8888,
        SKColorType.Rgb565,
        SKColorType.Gray8,
        SKColorType.RgbaF16,
        SKColorType.RgbaF32,
    ];

    /// <summary>
    /// Determines whether the specified FFmpeg pixel format can be represented
    /// directly by Skia.
    /// </summary>
    /// <param name="pixFmt">
    /// The FFmpeg pixel format to test.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the pixel format has a direct
    /// <see cref="SKColorType"/> equivalent; otherwise,
    /// <see langword="false"/>.
    /// </returns>
    /// <seealso cref="ToSkiaColorType(PixelFormat)"/>
    public static bool HasSkiaEquivalent(this PixelFormat pixFmt) => pixFmt switch
    {
        PixelFormat.BGRA => true,
        PixelFormat.Gray8 => true,
        PixelFormat.RGBX => true,
        PixelFormat.RGBA => true,
        PixelFormat.RGB565LE => true,
        PixelFormat.RGBAF16LE => true,
        PixelFormat.RGBAF32LE => true,
        _ => false,
    };

    /// <summary>
    /// Determines whether the specified Skia color type has a direct equivalent
    /// FFmpeg pixel format.
    /// </summary>
    /// <param name="pixelFormat">
    /// The Skia color type to test.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if the color type can be converted directly to a
    /// <see cref="PixelFormat"/>; otherwise, <see langword="false"/>.
    /// </returns>
    /// <seealso cref="ToPixelFormat(SKColorType)"/>
    public static bool HasFFmpegEquivalent(this SKColorType pixelFormat) => pixelFormat switch
    {
        SkiaSharp.SKColorType.Bgra8888 => true,
        SkiaSharp.SKColorType.Rgb888x => true,
        SkiaSharp.SKColorType.Rgba8888 => true,
        SkiaSharp.SKColorType.Rgb565 => true,
        SkiaSharp.SKColorType.Gray8 => true,
        SkiaSharp.SKColorType.RgbaF16 => true,
        SkiaSharp.SKColorType.RgbaF32 => true,
        _ => false,
    };

    /// <summary>
    /// Converts a supported <see cref="SKColorType"/> to its corresponding
    /// FFmpeg <see cref="PixelFormat"/>.
    /// </summary>
    /// <param name="pixelFormat">
    /// The Skia color type to convert.
    /// </param>
    /// <returns>
    /// The corresponding FFmpeg pixel format.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when the specified <see cref="SKColorType"/> has no direct
    /// equivalent FFmpeg pixel format.
    /// </exception>
    /// <remarks>
    /// Only color types listed in <see cref="FFSupportedColorTypes"/> are
    /// supported.
    /// </remarks>
    public static PixelFormat ToPixelFormat(this SKColorType pixelFormat) => pixelFormat switch
    {
        SkiaSharp.SKColorType.Bgra8888 => PixelFormat.BGRA,
        SkiaSharp.SKColorType.Rgb888x => PixelFormat.RGBX,
        SkiaSharp.SKColorType.Rgba8888 => PixelFormat.RGBA,
        SkiaSharp.SKColorType.Unknown => PixelFormat.None,
        SkiaSharp.SKColorType.Rgb565 => PixelFormat.RGB565LE,
        SkiaSharp.SKColorType.Gray8 => PixelFormat.Gray8,
        SkiaSharp.SKColorType.RgbaF16 => PixelFormat.RGBAF16LE,
        SkiaSharp.SKColorType.RgbaF32 => PixelFormat.RGBAF32LE,

        SkiaSharp.SKColorType.Alpha8 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Argb4444 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Rgba1010102 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Rgb101010x => throw new NotSupportedException(),

        SkiaSharp.SKColorType.RgbaF16Clamped => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Rg88 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.AlphaF16 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.RgF16 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Alpha16 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Rg1616 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Rgba16161616 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Bgra1010102 => throw new NotSupportedException(),
        SkiaSharp.SKColorType.Bgr101010x => throw new NotSupportedException(),
        _ => throw new NotSupportedException(),
    };


    /// <summary>
    /// Converts a supported FFmpeg <see cref="PixelFormat"/> to its corresponding
    /// <see cref="SKColorType"/>.
    /// </summary>
    /// <param name="pixelFormat">
    /// The FFmpeg pixel format to convert.
    /// </param>
    /// <returns>
    /// The corresponding Skia color type.
    /// </returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when the specified <see cref="PixelFormat"/> has no direct
    /// <see cref="SKColorType"/> equivalent.
    /// </exception>
    /// <remarks>
    /// Only pixel formats listed in <see cref="SKSupportedPixelFormats"/> are
    /// supported.
    /// </remarks>
    public static SKColorType ToSkiaColorType(this PixelFormat pixelFormat) => pixelFormat switch
    {
        PixelFormat.BGRA => SkiaSharp.SKColorType.Bgra8888,
        PixelFormat.RGBX => SkiaSharp.SKColorType.Rgb888x,
        PixelFormat.RGBA => SkiaSharp.SKColorType.Rgba8888,
        PixelFormat.None => SkiaSharp.SKColorType.Unknown,
        PixelFormat.RGB565LE => SkiaSharp.SKColorType.Rgb565,
        PixelFormat.Gray8 => SkiaSharp.SKColorType.Gray8,
        PixelFormat.RGBAF16LE => SkiaSharp.SKColorType.RgbaF16,
        PixelFormat.RGBAF32LE => SkiaSharp.SKColorType.RgbaF32,
        _ => throw new NotSupportedException(),
    };

    /// <summary>
    /// Converts an FFmpeg <see cref="PixelFormat"/> to the most appropriate
    /// <see cref="SKColorType"/>.
    /// </summary>
    /// <param name="pixelFormat">
    /// The FFmpeg pixel format to convert.
    /// </param>
    /// <returns>
    /// The matching <see cref="SKColorType"/> if one exists; otherwise, the color
    /// type corresponding to the closest supported FFmpeg pixel format.
    /// </returns>
    /// <remarks>
    /// If the specified pixel format is not directly supported by Skia, the closest
    /// compatible format is selected using
    /// <see cref="PixelFormatExtensions.FindBestPixelFormat(PixelFormat, ReadOnlySpan{PixelFormat})"/>.
    /// </remarks>
    /// <seealso cref="ToSkiaColorType(PixelFormat)"/>
    public static SKColorType ToBestSkiaColorType(this PixelFormat pixelFormat) => pixelFormat.HasSkiaEquivalent() ?
        pixelFormat.ToSkiaColorType() :
        pixelFormat.FindBestPixelFormat(SKSupportedPixelFormats).ToSkiaColorType();
}
