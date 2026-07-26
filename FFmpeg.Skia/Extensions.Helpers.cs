using FFmpeg.Images;
using AVFrame = FFmpeg.Utils.AVFrame;

namespace FFmpeg.Skia;

public static partial class Extensions
{

    /// <summary>
    /// Creates an <see cref="Images.SwsContext"/> for converting the specified
    /// frame to the requested Skia image format.
    /// </summary>
    /// <remarks>
    /// <see cref="SwsAlgorithm.FastBilinear"/> is used when no scaling is required.
    /// <see cref="SwsAlgorithm.Bicubic()"/> is used when the image dimensions differ.
    /// </remarks>
    private static Images.SwsContext GetSwsContext(AVFrame frame, SKImageInfo info) => frame.Width == info.Width && frame.Height == info.Height
            ? new Images.SwsContext(frame.Width, frame.Height, frame.PixelFormat, info.Width, info.Height, info.ColorType.ToPixelFormat(), SwsAlgorithm.FastBilinear())
            : new Images.SwsContext(frame.Width, frame.Height, frame.PixelFormat, info.Width, info.Height, info.ColorType.ToPixelFormat(), SwsAlgorithm.Bicubic());

    /// <summary>
    /// Gets the cropping rectangle of the frame as an <see cref="SKRectI"/>.
    /// </summary>
    /// <returns>
    /// An <see cref="SKRectI"/> describing the visible region of the frame after
    /// applying the cropping values stored in the frame.
    /// </returns>
    public static SKRectI CroppedRect(this AVFrame frame) => new(
    (int)frame.CropLeft,
    (int)frame.CropTop,
    frame.Width - (int)frame.CropRight,
    frame.Height - (int)frame.CropBottom);

    /// <summary>
    /// Copies the pixel data from an <see cref="AVFrame"/> into an existing
    /// <see cref="SKPixmap"/>.
    /// </summary>
    /// <remarks>
    /// The source frame and destination pixmap are assumed to have compatible
    /// dimensions and pixel formats. The copy is optimized for matching row
    /// strides and falls back to copying one scanline at a time when necessary.
    /// </remarks>
    internal static unsafe void CopyFrame(AVFrame frame, SKPixmap pixmap)
    {
        if (pixmap.RowBytes == frame.LineSize[0])
            Buffer.MemoryCopy((void*)frame.Data[0], pixmap.GetPixels().ToPointer(), pixmap.BytesSize, pixmap.BytesSize);
        else
        {
            var imagePtr = (byte*)pixmap.GetPixels().ToPointer();
            for (int y = 0; y < pixmap.Height; y++)
            {
                IntPtr framePtr = frame.Data[0] + (y * frame.LineSize[0]);
                var pixPtr = imagePtr + (y * pixmap.RowBytes);
                Buffer.MemoryCopy((void*)framePtr, pixPtr, pixmap.RowBytes, pixmap.BytesSize);
            }
        }
    }

    /// <summary>
    /// Copies the pixel data from an <see cref="AVFrame"/> into an existing
    /// <see cref="SKBitmap"/>.
    /// </summary>
    /// <remarks>
    /// The source frame and destination bitmap are assumed to have compatible
    /// dimensions and pixel formats. The copy is optimized for matching row
    /// strides and falls back to copying one scanline at a time when necessary.
    /// </remarks>
    internal static unsafe void CopyFrame(AVFrame frame, SKBitmap bitmap)
    {
        if (bitmap.RowBytes == frame.LineSize[0])
            Buffer.MemoryCopy((void*)frame.Data[0], bitmap.GetPixels().ToPointer(), bitmap.Info.BytesSize, bitmap.Info.BytesSize);
        else
        {
            var imagePtr = (byte*)bitmap.GetPixels().ToPointer();
            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr framePtr = frame.Data[0] + (y * frame.LineSize[0]);
                var pixPtr = imagePtr + (y * bitmap.RowBytes);
                Buffer.MemoryCopy((void*)framePtr, pixPtr, bitmap.RowBytes, bitmap.Info.BytesSize);
            }
        }
    }

    /// <summary>
    /// Determines whether the frame can be copied directly into a Skia image
    /// without pixel format conversion or scaling.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if the frame dimensions and pixel format already
    /// match the destination image; otherwise, <see langword="false"/>.
    /// </returns>
    private static bool CheckCopy(AVFrame frame, SKImageInfo info) => frame.CroppedWidth == info.Width
            && frame.CroppedHeight == info.Height
            && frame.PixelFormat == info.ColorType.ToPixelFormat();



    private static bool HasCrop(this AVFrame frame)
    => frame.CropLeft != 0
    || frame.CropTop != 0
    || frame.CropRight != 0
    || frame.CropBottom != 0;
}

