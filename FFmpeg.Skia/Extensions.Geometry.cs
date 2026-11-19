using FFmpeg.AutoGen;
using FFmpeg.Formats;
using FFmpeg.Unsafe;
using FFmpeg.Utils;

namespace FFmpeg.Skia;

public static partial class Extensions
{
    /// <summary>
    /// Gets the rotation a video stream must be displayed with, from its display matrix (e.g. the orientation of a
    /// phone video).
    /// </summary>
    /// <returns>
    /// The clockwise rotation in degrees: 0, 90, 180 or 270. 0 if the stream has no display matrix.
    /// </returns>
    /// <remarks>
    /// Mirroring in the display matrix is ignored. Use
    /// <see cref="DrawRotated(SKCanvas, SKBitmap, SKRect, int, SKSamplingOptions, SKPaint?)"/> to draw a frame with the rotation.
    /// </remarks>
    public static unsafe int GetDisplayRotation(this AVStream stream)
    {
        _AVCodecParameters* parameters = ((IAVPointer<_AVStream>)stream).Pointer->codecpar;
        if (parameters == null)
            return 0;
        _AVPacketSideData* sideData = ffmpeg.av_packet_side_data_get(parameters->coded_side_data, parameters->nb_coded_side_data, _AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        return sideData == null || sideData->size < (ulong)sizeof(int_array9) ? 0 : VideoGeometry.Rotation(*(int_array9*)sideData->data);
    }

    /// <summary>
    /// Gets the rotation a frame must be displayed with, from its display matrix side data.
    /// </summary>
    /// <returns>
    /// The clockwise rotation in degrees: 0, 90, 180 or 270. 0 if the frame has no display matrix
    /// (the stream may still have one, see <see cref="GetDisplayRotation(AVStream)"/>).
    /// </returns>
    public static unsafe int GetDisplayRotation(this AVFrame frame)
    {
        _AVFrameSideData* sideData = ffmpeg.av_frame_get_side_data(((IAVPointer<_AVFrame>)frame).Pointer, _AVFrameSideDataType.AV_FRAME_DATA_DISPLAYMATRIX);
        return sideData == null || sideData->size < (ulong)sizeof(int_array9) ? 0 : VideoGeometry.Rotation(*(int_array9*)sideData->data);
    }

    /// <summary>
    /// Gets the sample (pixel) aspect ratio of a video stream: the container's value if it has one, otherwise the
    /// codec's (like <c>av_guess_sample_aspect_ratio</c>).
    /// </summary>
    /// <returns>The width of a pixel relative to its height; 1:1 if unknown.</returns>
    public static Rational GetSampleAspectRatio(this AVStream stream)
    {
        Rational sar = stream.SampleAspectRatio;
        if (VideoGeometry.IsValid(sar))
            return sar;
        sar = stream.CodecParameters.SampleAspectRatio;
        return VideoGeometry.IsValid(sar) ? sar : new Rational(1, 1);
    }
}

/// <summary>Display size and orientation helpers.</summary>
internal static class VideoGeometry
{
    public static bool IsValid(Rational ratio) => ratio.Numerator > 0 && ratio.Denominator > 0;

    /// <summary>The clockwise display rotation (0, 90, 180, 270) of a display matrix.</summary>
    public static int Rotation(in int_array9 matrix)
    {
        double degrees = -ffmpeg.av_display_rotation_get(matrix); // FFmpeg returns counterclockwise degrees
        return double.IsNaN(degrees) ? 0 : Normalize((int)Math.Round(degrees / 90) * 90);
    }

    public static int Normalize(int degrees) => ((degrees % 360) + 360) % 360;

    /// <summary>The width a <paramref name="width"/> pixels wide image has with square pixels.</summary>
    public static int DisplayWidth(int width, Rational sampleAspectRatio)
        => IsValid(sampleAspectRatio) && sampleAspectRatio.Numerator != sampleAspectRatio.Denominator
            ? Math.Max(1, (int)Math.Round(width * (double)sampleAspectRatio.Numerator / sampleAspectRatio.Denominator))
            : width;

    /// <summary>The size of an image of the given size when displayed with <paramref name="rotation"/>.</summary>
    public static SKSizeI Rotate(int width, int height, int rotation)
        => Normalize(rotation) is 90 or 270 ? new SKSizeI(height, width) : new SKSizeI(width, height);
}
