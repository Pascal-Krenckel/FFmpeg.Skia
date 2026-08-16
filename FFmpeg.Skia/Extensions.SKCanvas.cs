using FFmpeg.Utils;
using System;
using System.Collections.Generic;
using System.Text;

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
            using var image = frame.AsSKImage();
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
            using var image = frame.AsSKImage();
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
            using var image = frame.AsSKImage();
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
            using var image = frame.AsSKImage();
            canvas.DrawImage(image, x, y, sampling, paint);
        }
    }
}
