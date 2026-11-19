using FFmpeg.Images;
using FFmpeg.Utils;
using System.Runtime.InteropServices;

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
    /// A new <see cref="SKImage"/> containing the visible region of the frame.
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
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The frame does not contain valid image dimensions.
    /// </exception>
    public static SKImage ToSKImage(this AVFrame frame) => ImageFromBitmap(frame.ToSKBitmap());

    /// <summary>
    /// Creates a new <see cref="SKImage"/> from an <see cref="AVFrame"/> using the
    /// specified Skia color type.
    /// </summary>
    /// <param name="frame">
    /// The source video frame.
    /// </param>
    /// <param name="colorType">
    /// The desired color type of the resulting image. If
    /// <see cref="SKColorType.Unknown"/> is specified, the color type is chosen as in <see cref="ToSKImage(AVFrame)"/>.
    /// </param>
    /// <returns>
    /// A new <see cref="SKImage"/> containing the visible region of the frame.
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
        => ImageFromBitmap(frame.ToSKBitmap(colorType));

    private static SKImage ImageFromBitmap(SKBitmap bitmap)
    {
        using (bitmap)
        {
            bitmap.SetImmutable(); // lets Skia share the pixels instead of copying them
            return SKImage.FromBitmap(bitmap) ?? throw new InvalidOperationException("Skia could not create an image from the bitmap.");
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
    /// An <see cref="SKImage"/> representing the visible region of the frame.
    /// </returns>
    /// <remarks>
    /// <para>
    /// If the frame uses a pixel format that is directly supported by Skia, no pixel
    /// data is copied. Instead, a new reference to the frame is created and the returned image
    /// points into its buffer.
    /// </para>
    /// <para>
    /// The frame reference remains alive until the returned <see cref="SKImage"/> is
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
        FrameInterop.ThrowIfNoImage(frame);
        if (!CanShare(frame))
            return ToSKImage(frame);

        AVFrame visible = FrameInterop.CloneCropped(frame);
        try
        {
            SKColorType colorType = visible.PixelFormat.ToSkiaColorType();
            SKImageInfo info = FrameInterop.NewInfo(visible.Width, visible.Height, colorType);
            using SKPixmap pixmap = new(info, visible.Data[0], visible.LineSize[0]);
            // Unlike SKBitmap.InstallPixels, SKImage.FromPixels does not call the release proc when it fails.
            return SKImage.FromPixels(pixmap, ReleaseImageFrame, visible)
                ?? throw new InvalidOperationException($"Skia rejected the frame's pixels ({info}, row bytes {visible.LineSize[0]}).");
        }
        catch
        {
            visible.Dispose();
            throw;
        }
    }

    private static readonly SKImageRasterReleaseDelegate ReleaseImageFrame = (_, context) => ((AVFrame)context).Dispose();

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
    /// image's <see cref="SKColorType"/>. Lazily decoded images (e.g. from <c>SKImage.FromEncodedData</c>)
    /// are decoded first. Premultiplied alpha is converted to straight alpha.
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The image's color type has no equivalent FFmpeg pixel format, or the image's pixels cannot be read
    /// (for example, because it is GPU-backed).
    /// </exception>
    public static AVFrame ToAVFrame(this SKImage image) => ToAVFrame(image, PixelFormat.None);

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
    /// The image's color type has no equivalent FFmpeg pixel format, or the image's pixels cannot be read
    /// (for example, because it is GPU-backed).
    /// </exception>
    public static AVFrame ToAVFrame(this SKImage image, PixelFormat targetFormat = PixelFormat.None)
    {
        AVFrame frame = AVFrame.Allocate();
        try
        {
            WithImagePixels(image, (pixels, rowBytes, info) =>
            {
                frame.Width = info.Width;
                frame.Height = info.Height;
                frame.PixelFormat = targetFormat == PixelFormat.None ? info.ColorType.ToPixelFormat() : targetFormat;
                using FrameConverter converter = new();
                FrameInterop.ReadIntoFrame(pixels, rowBytes, info, frame, converter);
            });
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Copies the contents of an <see cref="SKImage"/> into an existing
    /// <see cref="AVFrame"/>.
    /// </summary>
    /// <param name="image">
    /// The source image.
    /// </param>
    /// <param name="frame">
    /// The destination video frame.
    /// </param>
    /// <remarks>
    /// <para>
    /// If the destination frame has not been initialized, its dimensions and
    /// pixel format are set to match the image before the copy operation.
    /// </para>
    /// <para>
    /// If the destination frame has already been initialized, its dimensions and
    /// pixel format are preserved. The image is scaled and/or converted as
    /// necessary before being written to the frame.
    /// </para>
    /// <para>
    /// If the destination frame does not already own a buffer, one is allocated
    /// automatically. If its buffer is shared with other frames, it is made writable first.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// The image's <see cref="SKColorType"/> has no equivalent FFmpeg pixel
    /// format, or the image's pixel data cannot be accessed (for example, because
    /// it is GPU-backed).
    /// </exception>
    public static void CopyTo(this SKImage image, AVFrame frame)
    {
        WithImagePixels(image, (pixels, rowBytes, info) =>
        {
            PrepareTarget(frame, info.Width, info.Height, info.ColorType.ToPixelFormat());
            using FrameConverter converter = new();
            FrameInterop.ReadIntoFrame(pixels, rowBytes, info, frame, converter);
        });
    }

    private delegate void PixelsAction(IntPtr pixels, int rowBytes, SKImageInfo info);

    /// <summary>
    /// Gives access to the pixels of a raster image, or reads lazily decoded images into a temporary buffer.
    /// </summary>
    private static void WithImagePixels(SKImage image, PixelsAction action)
    {
        using (SKPixmap pixmap = new())
        {
            if (image.PeekPixels(pixmap))
            {
                _ = FrameInterop.ToSupportedPixelFormat(pixmap.ColorType);
                action(pixmap.GetPixels(), pixmap.RowBytes, pixmap.Info);
                return;
            }
        }

        // Not a raster image (e.g. lazily decoded): let Skia produce the pixels.
        SKColorType colorType = image.ColorType.HasFFmpegEquivalent() ? image.ColorType : SKColorType.Rgba8888;
        SKImageInfo info = FrameInterop.NewInfo(image.Width, image.Height, colorType);
        if (info.Width <= 0 || info.Height <= 0)
            throw new ArgumentException("The image is empty.", nameof(image));
        int rowBytes = info.RowBytes;
        IntPtr buffer = Marshal.AllocHGlobal((IntPtr)((long)rowBytes * info.Height));
        try
        {
            if (!image.ReadPixels(info, buffer, rowBytes, 0, 0))
                throw new NotSupportedException("Could not read the image's pixels; GPU-backed images are not supported.");
            action(buffer, rowBytes, info);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

}
