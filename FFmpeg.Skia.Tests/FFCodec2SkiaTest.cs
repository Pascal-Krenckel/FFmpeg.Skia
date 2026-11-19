namespace FFmpeg.Skia.Tests;

/// <summary>
/// Tests for <see cref="FFCodec2Skia"/>: decoding, scaling into different bitmaps, seeking and metadata.
/// </summary>
[TestClass]
public class FFCodec2SkiaTest
{
    private static FFCodec2Skia OpenCounter()
        => FFCodec2Skia.Create(TestFiles.Counter, new SKImageInfo(0, 0, SKColorType.Rgba8888))!;

    [TestMethod]
    public void NextImage_DecodesAnimatedPng()
    {
        using FFCodec2Skia codec = FFCodec2Skia.Create(TestFiles.AnimatedPng)!;

        int n = 0;
        while (codec.NextImage(out _) is SKBitmap bitmap)
        {
            using (bitmap)
                Assert.AreEqual(((byte)10, (byte)20, (byte)(40 * n), (byte)128), Pixels.Get(bitmap, 10, 20), $"frame {n}");
            n++;
        }

        Assert.AreEqual(5, n);
    }

    [TestMethod]
    public void NextImage_IntoBitmapsOfDifferentSizes()
    {
        using FFCodec2Skia codec = OpenCounter();
        using SKBitmap large = new(new SKImageInfo(320, 240, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using SKBitmap small = new(new SKImageInfo(160, 120, SKColorType.Rgba8888, SKAlphaType.Opaque));

        Assert.IsTrue(codec.NextImage(large, out _));
        Assert.IsTrue(codec.NextImage(small, out _));

        Pixels.AreNear(1, Pixels.CounterIndex(small), 1, "frame index");
    }

    [TestMethod]
    public void NextImage_IntoPremultipliedBitmap_Premultiplies()
    {
        using FFCodec2Skia codec = FFCodec2Skia.Create(TestFiles.AnimatedPng)!;
        using SKBitmap bitmap = new(new SKImageInfo(333, 201, SKColorType.Rgba8888, SKAlphaType.Premul));

        Assert.IsTrue(codec.NextImage(bitmap, out _));

        var p = Pixels.Get(bitmap, 200, 100); // straight (200, 100, 0, 128)
        Pixels.AreNear(100, p.R, 1, "R");
        Pixels.AreNear(50, p.G, 1, "G");
        Assert.AreEqual((byte)128, p.A);
    }

    [TestMethod]
    public void Seek_Time_ReturnsTheFrameVisibleAtThatTime()
    {
        using FFCodec2Skia codec = OpenCounter();

        Assert.IsFalse(codec.Seek(TimeSpan.FromSeconds(1.5)).IsError);
        using SKBitmap bitmap = codec.NextImage(out FFCodecFrameInfo info)!;

        Assert.AreEqual(1.48, info.TimeStamp.TotalSeconds, 0.005);
        Pixels.AreNear(37, Pixels.CounterIndex(bitmap), 1, "frame index");
    }

    [TestMethod]
    public void Seek_FrameIndex_And_Restart()
    {
        using FFCodec2Skia codec = OpenCounter();

        Assert.IsFalse(codec.Seek(60L).IsError);
        using (SKBitmap bitmap = codec.NextImage(out FFCodecFrameInfo info)!)
        {
            Assert.AreEqual(2.4, info.TimeStamp.TotalSeconds, 0.005);
            Pixels.AreNear(60, Pixels.CounterIndex(bitmap), 1, "frame index");
        }

        Assert.IsFalse(codec.Restart().IsError);
        using (SKBitmap first = codec.NextImage(out FFCodecFrameInfo info)!)
            Assert.AreEqual(TimeSpan.Zero, info.TimeStamp);
    }

    [TestMethod]
    public void Properties()
    {
        using FFCodec2Skia codec = FFCodec2Skia.Create(TestFiles.Counter)!;

        Assert.AreEqual(4.0, codec.Duration.TotalSeconds, 0.1);
        Assert.AreEqual((320, 240), (codec.Info.Width, codec.Info.Height));
    }

    [TestMethod]
    public void Create_WithoutVideoStream_Throws()
        => Assert.Throws<ArgumentException>(() => FFCodec2Skia.Create(TestFiles.AudioOnly));
}
