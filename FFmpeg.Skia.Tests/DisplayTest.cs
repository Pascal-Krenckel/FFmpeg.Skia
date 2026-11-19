using System.Diagnostics;

namespace FFmpeg.Skia.Tests;

/// <summary>
/// Tests for the display helpers: rotation, sample aspect ratio, the color type of allocated bitmaps,
/// buffer reuse in <see cref="SKAnimatedImage"/> and the converter reuse of the extension methods.
/// </summary>
[TestClass]
public class DisplayTest
{
    #region Rotation

    [TestMethod]
    public void Rotation_FromDisplayMatrix()
    {
        using (FFCodec2Skia codec = FFCodec2Skia.Create(TestFiles.Rotated)!)
        {
            Assert.AreEqual(90, codec.Rotation);
            Assert.AreEqual((320, 240), (codec.Info.Width, codec.Info.Height), "the decoded images are not rotated");
            Assert.AreEqual((240, 320), (codec.DisplaySize.Width, codec.DisplaySize.Height));
        }
        using (SKVideo video = new(TestFiles.Rotated))
            Assert.AreEqual(90, video.Rotation);
        using (SKAnimatedImage image = SKAnimatedImage.Create(TestFiles.Rotated))
            Assert.AreEqual(90, image.Rotation);
        using (FFCodec2Skia codec = FFCodec2Skia.Create(TestFiles.Counter)!)
        {
            Assert.AreEqual(0, codec.Rotation);
            Assert.AreEqual((320, 240), (codec.DisplaySize.Width, codec.DisplaySize.Height));
        }
    }

    [TestMethod]
    [DataRow(0, DisplayName = "0°")]
    [DataRow(90, DisplayName = "90°")]
    [DataRow(180, DisplayName = "180°")]
    [DataRow(270, DisplayName = "270°")]
    public void DrawRotated_RotatesClockwiseIntoDestination(int rotation)
    {
        // 4x2 source: top-left red, top-right green, bottom-right blue, bottom-left white
        using SKBitmap source = new(new SKImageInfo(4, 2, SKColorType.Rgba8888, SKAlphaType.Opaque));
        Pixels.Fill(source, 0xFF000000u);
        SetPixel(source, 0, 0, 0xFF0000FFu);
        SetPixel(source, 3, 0, 0xFF00FF00u);
        SetPixel(source, 3, 1, 0xFFFF0000u);
        SetPixel(source, 0, 1, 0xFFFFFFFFu);
        bool swap = rotation is 90 or 270;
        int width = swap ? 2 : 4, height = swap ? 4 : 2;
        using SKBitmap target = new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using (SKCanvas canvas = new(target))
            canvas.DrawRotated(source, new SKRect(0, 0, width, height), rotation, SKSamplingOptions.Default);

        // clockwise: each source corner moves rotation / 90 corners further (TL -> TR -> BR -> BL)
        (int x, int y)[] corners = [(0, 0), (width - 1, 0), (width - 1, height - 1), (0, height - 1)];
        uint[] colors = [0xFF0000FFu, 0xFF00FF00u, 0xFFFF0000u, 0xFFFFFFFFu];
        for (int i = 0; i < 4; i++)
        {
            var (x, y) = corners[(i + (rotation / 90)) % 4];
            Assert.AreEqual(colors[i], GetPixel(target, x, y), $"source corner {i} should be at ({x},{y})");
        }
    }

    private static unsafe void SetPixel(SKBitmap bitmap, int x, int y, uint value)
        => *(uint*)((byte*)bitmap.GetPixels() + (y * bitmap.RowBytes) + (x * 4)) = value;

    private static unsafe uint GetPixel(SKBitmap bitmap, int x, int y)
        => *(uint*)((byte*)bitmap.GetPixels() + (y * bitmap.RowBytes) + (x * 4));

    #endregion

    #region Sample aspect ratio

    [TestMethod]
    public void Anamorphic_IsDecodedWithSquarePixels()
    {
        using (FFCodec2Skia codec = FFCodec2Skia.Create(TestFiles.Anamorphic)!)
        {
            Assert.AreEqual((32, 27), (codec.SampleAspectRatio.Numerator, codec.SampleAspectRatio.Denominator));
            Assert.AreEqual((853, 480), (codec.Info.Width, codec.Info.Height));
            using SKBitmap bitmap = codec.NextImage(out _)!;
            Assert.AreEqual((853, 480), (bitmap.Width, bitmap.Height));
        }
        using (FFCodec2Skia codec = FFCodec2Skia.Create(TestFiles.Anamorphic, new SKImageInfo(720, 480, SKColorType.Rgba8888))!)
            Assert.AreEqual(720, codec.Info.Width, "an explicit size is kept");
        using (SKAnimatedImage image = SKAnimatedImage.Create(TestFiles.Anamorphic))
        {
            Assert.IsTrue(image.DecodeNextImage());
            Assert.AreEqual((853, 480), (image.Frame.Width, image.Frame.Height));
        }
        using (FFCodec2Skia codec = FFCodec2Skia.Create(TestFiles.Counter)!)
            Assert.AreEqual((1, 1), (codec.SampleAspectRatio.Numerator, codec.SampleAspectRatio.Denominator));
    }

    #endregion

    #region Allocation and buffers

    [TestMethod]
    public void CopyTo_EmptyBitmap_Uses8BitColorTypes()
    {
        using AVFrame tenBit = Pixels.YuvFrame(16, 16, 512, 512, 512, PixelFormat.YUV420P10LE);
        using SKBitmap bitmap = new();

        tenBit.CopyTo(bitmap);

        Assert.AreEqual(4, bitmap.BytesPerPixel, $"10 bit video got {bitmap.ColorType}");
    }

    [TestMethod]
    public void SKAnimatedImage_KeepsThePreviousFrameUntouchedForOneFrame()
    {
        using SKAnimatedImage image = SKAnimatedImage.Create(TestFiles.AnimatedPng);
        List<SKBitmap> shown = [];
        for (int n = 0; n < 4; n++)
        {
            Assert.IsTrue(image.DecodeNextImage());
            shown.Add(image.Frame);
            if (n > 0) // the frame shown before still has its content
                Assert.AreEqual((byte)(40 * (n - 1)), Pixels.Get(shown[n - 1], 10, 10).B, $"frame {n - 1} was overwritten by frame {n}");
        }

        Assert.AreEqual(3, shown.Distinct().Count(), "three buffers are used in turn");
        Assert.AreSame(shown[0], shown[3]);
    }

    [TestMethod]
    public void ExtensionMethods_ReuseTheConversionSetup()
    {
        // HDR needs a tone mapping table that takes seconds to build; it must not be built for every frame
        using AVFrame frame = Pixels.YuvFrame(64, 64, 500, 512, 512, PixelFormat.YUV420P10LE);
        frame.ColorPrimaries = ColorPrimaries.BT2020;
        frame.ColorTransferCharacteristic = ColorTransferCharacteristic.SMPTE2084;
        frame.ColorSpace = ColorSpace.BT2020_NCL;
        using SKBitmap bitmap = new(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Opaque));
        frame.CopyTo(bitmap); // first conversion sets up

        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++)
            frame.CopyTo(bitmap);

        Assert.IsTrue(watch.ElapsedMilliseconds < 500, $"5 conversions took {watch.ElapsedMilliseconds} ms");
    }

    #endregion
}
