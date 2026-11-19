using FFmpeg.Utils;

namespace FFmpeg.Skia;

public static partial class Extensions
{
    extension(SKCanvas canvas)
    {
        /// <summary>
        /// Draws an <see cref="AVFrame"/> onto the canvas at the specified position.
        /// </summary>
        /// <param name="frame">
        /// The source video frame to draw.
        /// </param>
        /// <param name="p">
        /// The position at which to draw the frame.
        /// </param>
        /// <param name="sampling">
        /// The sampling options used when the frame is scaled.
        /// </param>
        /// <param name="paint">
        /// The optional paint used to draw the frame.
        /// </param>
        public void DrawFrame(
            AVFrame frame,
            SKPoint p,
            SKSamplingOptions sampling,
            SKPaint? paint = null)
        {
            using SKImage image = frame.AsSKImage();
            canvas.DrawImage(image, p, sampling, paint);
        }

        /// <summary>
        /// Draws a portion of an <see cref="AVFrame"/> into a destination rectangle.
        /// </summary>
        /// <param name="frame">
        /// The source video frame to draw.
        /// </param>
        /// <param name="source">
        /// The portion of the frame to draw.
        /// </param>
        /// <param name="dest">
        /// The destination rectangle onto which the source region is drawn.
        /// </param>
        /// <param name="sampling">
        /// The sampling options used when the frame is scaled.
        /// </param>
        /// <param name="paint">
        /// The optional paint used to draw the frame.
        /// </param>
        public void DrawFrame(
            AVFrame frame,
            SKRect source,
            SKRect dest,
            SKSamplingOptions sampling,
            SKPaint? paint = null)
        {
            using SKImage image = frame.AsSKImage();
            canvas.DrawImage(image, source, dest, sampling, paint);
        }

        /// <summary>
        /// Draws an <see cref="AVFrame"/> into the specified destination rectangle.
        /// </summary>
        /// <param name="frame">
        /// The source video frame to draw.
        /// </param>
        /// <param name="dest">
        /// The destination rectangle onto which the frame is drawn.
        /// </param>
        /// <param name="sampling">
        /// The sampling options used when the frame is scaled.
        /// </param>
        /// <param name="paint">
        /// The optional paint used to draw the frame.
        /// </param>
        public void DrawFrame(
            AVFrame frame,
            SKRect dest,
            SKSamplingOptions sampling,
            SKPaint? paint = null)
        {
            using SKImage image = frame.AsSKImage();
            canvas.DrawImage(image, dest, sampling, paint);
        }

        /// <summary>
        /// Draws an <see cref="AVFrame"/> onto the canvas at the specified coordinates.
        /// </summary>
        /// <param name="frame">
        /// The source video frame to draw.
        /// </param>
        /// <param name="x">
        /// The x-coordinate at which to draw the frame.
        /// </param>
        /// <param name="y">
        /// The y-coordinate at which to draw the frame.
        /// </param>
        /// <param name="sampling">
        /// The sampling options used when the frame is scaled.
        /// </param>
        /// <param name="paint">
        /// The optional paint used to draw the frame.
        /// </param>
        public void DrawFrame(
            AVFrame frame,
            float x,
            float y,
            SKSamplingOptions sampling,
            SKPaint? paint = null)
        {
            using SKImage image = frame.AsSKImage();
            canvas.DrawImage(image, x, y, sampling, paint);
        }

        /// <summary>
        /// Draws a bitmap rotated clockwise by <paramref name="rotation"/> degrees so that it fills
        /// <paramref name="dest"/>, e.g. a video frame with its display rotation
        /// (<see cref="FFCodec2Skia.Rotation"/>, <see cref="SKVideo.Rotation"/>, <see cref="SKAnimatedImage.Rotation"/>).
        /// </summary>
        /// <param name="bitmap">The bitmap to draw.</param>
        /// <param name="dest">The destination rectangle, in display orientation (for 90 and 270 degrees its width
        /// corresponds to the bitmap's height).</param>
        /// <param name="rotation">The clockwise rotation in degrees; multiples of 90.</param>
        /// <param name="sampling">The sampling options used when the bitmap is scaled.</param>
        /// <param name="paint">The optional paint used to draw the bitmap.</param>
        public void DrawRotated(SKBitmap bitmap, SKRect dest, int rotation, SKSamplingOptions sampling, SKPaint? paint = null)
        {
            if (VideoGeometry.Normalize(rotation) == 0)
            {
                canvas.DrawBitmap(bitmap, dest, sampling, paint);
                return;
            }
            int saveCount = canvas.Save();
            try
            {
                canvas.DrawBitmap(bitmap, PrepareRotated(canvas, dest, rotation), sampling, paint);
            }
            finally
            {
                canvas.RestoreToCount(saveCount);
            }
        }

        /// <summary>
        /// Draws an image rotated clockwise by <paramref name="rotation"/> degrees so that it fills
        /// <paramref name="dest"/>.
        /// </summary>
        /// <inheritdoc cref="DrawRotated(SKCanvas, SKBitmap, SKRect, int, SKSamplingOptions, SKPaint?)"/>
        public void DrawRotated(SKImage image, SKRect dest, int rotation, SKSamplingOptions sampling, SKPaint? paint = null)
        {
            if (VideoGeometry.Normalize(rotation) == 0)
            {
                canvas.DrawImage(image, dest, sampling, paint);
                return;
            }
            int saveCount = canvas.Save();
            try
            {
                canvas.DrawImage(image, PrepareRotated(canvas, dest, rotation), sampling, paint);
            }
            finally
            {
                canvas.RestoreToCount(saveCount);
            }
        }
    }

    /// <summary>Rotates the canvas around the center of <paramref name="dest"/>; returns the rectangle to draw into.</summary>
    private static SKRect PrepareRotated(SKCanvas canvas, SKRect dest, int rotation)
    {
        rotation = VideoGeometry.Normalize(rotation);
        canvas.Translate(dest.MidX, dest.MidY);
        canvas.RotateDegrees(rotation);
        bool swap = rotation is 90 or 270;
        float halfWidth = (swap ? dest.Height : dest.Width) / 2;
        float halfHeight = (swap ? dest.Width : dest.Height) / 2;
        return new SKRect(-halfWidth, -halfHeight, halfWidth, halfHeight);
    }
}
