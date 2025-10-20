using System;
using System.Collections.Generic;
using System.Text;

namespace FFmpeg.Skia;
public readonly struct FFCodecFrameInfo : IEquatable<FFCodecFrameInfo>
{
    public TimeSpan Duration { get; init; }
    public TimeSpan TimeStamp { get; init; }

    public override bool Equals(object? obj) => obj is FFCodecFrameInfo info && Equals(info);
    public bool Equals(FFCodecFrameInfo other) => Duration.Equals(other.Duration) && TimeStamp.Equals(other.TimeStamp);
    public override int GetHashCode() => HashCode.Combine(Duration, TimeStamp);

    public static bool operator ==(FFCodecFrameInfo left, FFCodecFrameInfo right) => left.Equals(right);
    public static bool operator !=(FFCodecFrameInfo left, FFCodecFrameInfo right) => !(left == right);

    public override string ToString() => $"{TimeStamp} -> {TimeStamp+Duration}";
}
