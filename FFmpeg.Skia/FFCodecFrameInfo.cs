namespace FFmpeg.Skia;

/// <summary>
/// Contains timing information for a decoded video frame.
/// </summary>
/// <remarks>
/// Instances of this structure are returned by <see cref="FFCodec2Skia"/> when decoding
/// frames and provide the presentation timestamp and display duration of the decoded frame.
/// </remarks>
public readonly struct FFCodecFrameInfo : IEquatable<FFCodecFrameInfo>
{
    /// <summary>
    /// Gets the amount of time the frame should be displayed.
    /// </summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// Gets the presentation timestamp of the frame.
    /// </summary>
    /// <remarks>
    /// This value represents the intended presentation time of the frame relative
    /// to the start of the media.
    /// </remarks>
    public TimeSpan TimeStamp { get; init; }

    /// <summary>
    /// Determines whether the specified object is equal to the current frame information.
    /// </summary>
    /// <param name="obj">The object to compare with the current instance.</param>
    /// <returns>
    /// <see langword="true"/> if <paramref name="obj"/> is an
    /// <see cref="FFCodecFrameInfo"/> with the same timestamp and duration;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    public override bool Equals(object? obj) => obj is FFCodecFrameInfo info && Equals(info);

    /// <summary>
    /// Determines whether the specified frame information is equal to the current instance.
    /// </summary>
    /// <param name="other">The frame information to compare.</param>
    /// <returns>
    /// <see langword="true"/> if both instances have the same timestamp and duration;
    /// otherwise, <see langword="false"/>.
    /// </returns>
    public bool Equals(FFCodecFrameInfo other) =>
        Duration.Equals(other.Duration) && TimeStamp.Equals(other.TimeStamp);

    /// <summary>
    /// Returns the hash code for this instance.
    /// </summary>
    /// <returns>A hash code for the current frame information.</returns>
    public override int GetHashCode() => HashCode.Combine(Duration, TimeStamp);

    /// <summary>
    /// Determines whether two <see cref="FFCodecFrameInfo"/> instances are equal.
    /// </summary>
    public static bool operator ==(FFCodecFrameInfo left, FFCodecFrameInfo right) => left.Equals(right);

    /// <summary>
    /// Determines whether two <see cref="FFCodecFrameInfo"/> instances are not equal.
    /// </summary>
    public static bool operator !=(FFCodecFrameInfo left, FFCodecFrameInfo right) => !(left == right);

    /// <summary>
    /// Returns a string that represents the current frame information.
    /// </summary>
    /// <returns>
    /// A string containing the presentation timestamp and the end time of the frame.
    /// </returns>
    public override string ToString() => $"{TimeStamp} -> {TimeStamp + Duration}";
}