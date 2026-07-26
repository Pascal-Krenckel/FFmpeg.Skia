using FFmpeg.Images;
using FFmpeg.Utils;
using System;
using System.Collections.Generic;
using System.Text;

namespace FFmpeg.Skia;

public static partial class Extensions
{

    /// <summary>
    /// Creates a new <see cref="SKImage"/> from an <see cref="AVFrame"/>.
    /// </summary>
    /// <param name="frame">
    /// The source video frame.
    /// </param>
    /// <returns>
    /// A new <see cref="SKImage"/> containing the frame data.
    /// </returns>
    /// <remarks>
    /// <para>
    /// If the frame's pixel format is directly supported by Skia, the pixel data is
    /// copied without conversion.
    /// </para>
    /// <para>
    /// Otherwise, the frame is converted to a compatible Skia color type using
    /// <see cref="Images.SwsContext"/>.
    /// </para>
    /// <para>
    /// If the frame contains cropping information, the returned image represents
    /// only the visible region.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The frame does not contain valid image dimensions.
    /// </exception>
    public static SKImage ToSKImage(this AVFrame frame)
    {
        PixelFormat pixfmt = frame.PixelFormat;
        int width = frame.Width;
        int height = frame.Height;
        if (width <= 0 || height <= 0)
            throw new ArgumentException("Invalid image size");
        SKColorType colorType = pixfmt.HasSkiaEquivalent() ? pixfmt.ToSkiaColorType() : SKImageInfo.PlatformColorType;
        if (!colorType.HasFFmpegEquivalent())
            colorType = SKColorType.Rgba8888; // make sure that platform color type is correct
        bool justCopy = colorType.ToPixelFormat() == pixfmt;
        SKImageInfo info = new(width, height, colorType, SKAlphaType.Unpremul);

        SKImage skImage = SKImage.Create(info);
        try
        {
            using SKPixmap pixmap = skImage.PeekPixels();

            if (justCopy)
                CopyFrame(frame, pixmap);
            else
                Images.SwsContext.Convert(frame, pixmap.GetPixels(), new(width, height, colorType.ToPixelFormat()), Images.SwsAlgorithm.FastBilinear()).ThrowIfError();
            return  (!frame.HasCrop())
                ? skImage
                : skImage.Subset(frame.CroppedRect());
        }
        catch { skImage.Dispose(); throw; }
    }

    /// <summary>
    /// Creates a new <see cref="SKImage"/> from an <see cref="AVFrame"/> using the
    /// specified Skia color type.
    /// </summary>
    /// <param name="frame">
    /// The source video frame.
    /// </param>
    /// <param name="colorType">
    /// The desired color type of the resulting image. If
    /// <see cref="SKColorType.Unknown"/> is specified, the most appropriate Skia
    /// color type is selected automatically.
    /// </param>
    /// <returns>
    /// A new <see cref="SKImage"/> containing the converted frame.
    /// </returns>
    /// <remarks>
    /// If the requested color type already matches the frame's pixel format, no
    /// pixel format conversion is performed.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The frame does not contain valid image dimensions.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The specified color type cannot be converted to an FFmpeg pixel format.
    /// </exception>
    public static SKImage ToSKImage(this AVFrame frame, SKColorType colorType = SKColorType.Unknown)
    {
        if (colorType == SKColorType.Unknown)
            return ToSKImage(frame);
        PixelFormat srcFormat = frame.PixelFormat;
        if (srcFormat.HasSkiaEquivalent() && srcFormat.ToSkiaColorType() == colorType)
            return ToSKImage(frame);
        if (frame.Width < 0 || frame.Height < 0)
            throw new ArgumentException();
        SKImageInfo info = new(frame.Width, frame.Height, colorType, SKAlphaType.Unpremul);
        SKImage skImage = SKImage.Create(info);
        try
        {
            using SKPixmap pixmap = skImage.PeekPixels();
            Images.SwsContext.Convert(frame, pixmap.GetPixels(), new(pixmap.Width, pixmap.Height, colorType.ToPixelFormat()), Images.SwsAlgorithm.FastBilinear()).ThrowIfError();
            SKImage image = skImage.Subset(frame.CroppedRect());
            return image;
        }
        catch
        {
            skImage.Dispose();
            throw;
        }
    }


    /// <summary>
    /// Creates an <see cref="SKImage"/> that shares the pixel buffer of the supplied
    /// <see cref="AVFrame"/> whenever possible.
    /// </summary>
    /// <param name="frame">
    /// The source video frame.
    /// </param>
    /// <returns>
    /// An <see cref="SKImage"/> representing the frame.
    /// </returns>
    /// <remarks>
    /// <para>
    /// If the frame uses a pixel format that is directly supported by Skia, no pixel
    /// data is copied. Instead, the frame is cloned and the returned image references
    /// the cloned frame's buffer.
    /// </para>
    /// <para>
    /// The cloned frame remains alive until the returned <see cref="SKImage"/> is
    /// disposed.
    /// </para>
    /// <para>
    /// If the frame's pixel format is not supported by Skia, this method falls back
    /// to <see cref="ToSKImage(AVFrame)"/>, which performs a pixel format conversion
    /// and copies the image data.
    /// </para>
    /// </remarks>
    public static SKImage AsSKImage(this AVFrame frame)
    {
        if (frame.PixelFormat.HasSkiaEquivalent())
        {
            frame = frame.Clone(); // Clone the frame
            SKRectI cropRect = frame.CroppedRect();
            SKImageInfo info = new(cropRect.Width, cropRect.Height, frame.PixelFormat.ToSkiaColorType());
            long byteSkippedLeft = (long)frame.CropLeft * info.BytesPerPixel;
            long byteSkippedTop = (long)frame.CropTop * info.RowBytes;
            SKPixmap pixmap = new(info, new IntPtr(frame.Data[0].ToInt64() + byteSkippedLeft + byteSkippedTop), frame.LineSize[0]);
            SKImage skImage = SKImage.FromPixels(pixmap, (ptr, obj) =>
            {
                (AVFrame frame, SKPixmap pixmap) = ((AVFrame, SKPixmap))obj;
                pixmap.Dispose();
                frame.Dispose();
            }, (frame, pixmap));
            return skImage;
        }
        else
            return ToSKImage(frame);
    }

    /// <summary>
    /// Creates a new <see cref="AVFrame"/> from an <see cref="SKImage"/>.
    /// </summary>
    /// <param name="image">
    /// The source image.
    /// </param>
    /// <returns>
    /// A newly allocated frame containing the image data.
    /// </returns>
    /// <remarks>
    /// The resulting frame uses the FFmpeg pixel format corresponding to the
    /// image's <see cref="SKColorType"/>.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The image's color type has no equivalent FFmpeg pixel format.
    /// </exception>
    public static unsafe AVFrame ToAVFrame(this SKImage image)
    {
        if (!image.ColorType.HasFFmpegEquivalent())
            throw new NotSupportedException();
        AVFrame frame = AVFrame.Allocate();
        try
        {
            frame.Width = image.Width;
            frame.Height = image.Height;
            frame.Format = (int)image.ColorType.ToPixelFormat();
            frame.CreateBuffer().ThrowIfError();
            using SKPixmap pixmap = image.PeekPixels();
            Buffer.MemoryCopy(pixmap.GetPixels().ToPointer(), (void*)frame.Data[0], pixmap.BytesSize, pixmap.BytesSize);
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates a new <see cref="AVFrame"/> from an <see cref="SKImage"/> using the
    /// specified pixel format.
    /// </summary>
    /// <param name="image">
    /// The source image.
    /// </param>
    /// <param name="targetFormat">
    /// The desired FFmpeg pixel format. Specify <see cref="PixelFormat.None"/> to
    /// preserve the image's native format.
    /// </param>
    /// <returns>
    /// A newly allocated frame containing the converted image.
    /// </returns>
    /// <remarks>
    /// If the requested pixel format already matches the image's color type, no
    /// conversion is performed.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The image's color type has no equivalent FFmpeg pixel format.
    /// </exception>
    public static AVFrame ToAVFrame(this SKImage image, PixelFormat targetFormat = PixelFormat.None)
    {
        if (!image.ColorType.HasFFmpegEquivalent())
            throw new NotSupportedException();
        if (targetFormat == PixelFormat.None || targetFormat == image.ColorType.ToPixelFormat())
            return ToAVFrame(image);
        AVFrame frame = AVFrame.Allocate();
        try
        {
            frame.Width = image.Width;
            frame.Height = image.Height;
            frame.Format = (int)targetFormat;
            using SKPixmap pixmap = image.PeekPixels();
            Images.SwsContext.Convert(pixmap.GetPixels(), new Images.ImageInfo(image.Width, image.Height, image.Info.ColorType.ToPixelFormat()), frame, Images.SwsAlgorithm.FastBilinear()).ThrowIfError();

            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

}
