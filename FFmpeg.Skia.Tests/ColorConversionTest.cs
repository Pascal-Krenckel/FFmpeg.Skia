namespace FFmpeg.Skia.Tests;

/// <summary>
/// Checks the color conversion against the BT.601/BT.709/BT.2020 formulas (<see cref="ColorReference"/>):
/// YUV matrix and range of the frames in both directions, gray, HDR and interlaced frames.
/// </summary>
[TestClass]
public class ColorConversionTest
{
    private const int Tolerance = 2;

    [TestMethod]
    [DataRow(PixelFormat.YUV420P, ColorSpace.Unspecified, ColorRange.Unspecified, 120, 90, 200, DisplayName = "untagged = BT.601 limited")]
    [DataRow(PixelFormat.YUV420P, ColorSpace.SMPTE170M, ColorRange.MPEG, 120, 90, 200, DisplayName = "BT.601 limited")]
    [DataRow(PixelFormat.YUV420P, ColorSpace.BT709, ColorRange.MPEG, 120, 90, 200, DisplayName = "BT.709 limited")]
    [DataRow(PixelFormat.YUV420P, ColorSpace.BT2020_NCL, ColorRange.MPEG, 120, 90, 200, DisplayName = "BT.2020 limited")]
    [DataRow(PixelFormat.YUV420P, ColorSpace.BT709, ColorRange.JPEG, 120, 90, 200, DisplayName = "BT.709 full range yuv420p")]
    [DataRow(PixelFormat.YUV420P, ColorSpace.BT470BG, ColorRange.JPEG, 120, 90, 200, DisplayName = "BT.601 full range yuv420p")]
    [DataRow(PixelFormat.YUVJ420P, ColorSpace.Unspecified, ColorRange.Unspecified, 120, 90, 200, DisplayName = "yuvj420p = full range")]
    [DataRow(PixelFormat.YUV420P10LE, ColorSpace.BT709, ColorRange.MPEG, 480, 360, 800, DisplayName = "BT.709 limited 10 bit")]
    public void YuvToSkia_UsesMatrixAndRangeOfTheFrame(PixelFormat format, ColorSpace colorSpace, ColorRange range, int y, int u, int v)
    {
        using AVFrame frame = Pixels.YuvFrame(32, 32, y, u, v, format);
        frame.ColorSpace = colorSpace;
        frame.ColorRange = range;
        var expected = ColorReference.ToRgb(format, colorSpace, range, y, u, v);

        foreach (SKColorType colorType in new[] { SKColorType.Rgba8888, SKColorType.Bgra8888 })
        {
            using SKBitmap bitmap = frame.ToSKBitmap(colorType);
            var p = Pixels.Get(bitmap, 16, 16);
            Pixels.AreNear(expected.R, p.R, Tolerance, $"{colorType} R");
            Pixels.AreNear(expected.G, p.G, Tolerance, $"{colorType} G");
            Pixels.AreNear(expected.B, p.B, Tolerance, $"{colorType} B");
        }
    }

    [TestMethod]
    public void YuvToGray8_IsFullRange()
    {
        using AVFrame frame = Pixels.YuvFrame(32, 32, 128, 128, 128); // limited: (128 - 16) / 219 -> 130

        using SKBitmap bitmap = frame.ToSKBitmap(SKColorType.Gray8);

        Pixels.AreNear(130, Pixels.Gray(bitmap, 16, 16), Tolerance, "gray");
    }

    [TestMethod]
    [DataRow(ColorSpace.Unspecified, ColorRange.Unspecified, DisplayName = "untagged target")]
    [DataRow(ColorSpace.BT709, ColorRange.MPEG, DisplayName = "BT.709 limited target")]
    [DataRow(ColorSpace.BT709, ColorRange.JPEG, DisplayName = "BT.709 full range target")]
    [DataRow(ColorSpace.BT2020_NCL, ColorRange.MPEG, DisplayName = "BT.2020 limited target")]
    public void SkiaToYuv_UsesMatrixAndRangeOfTheFrame(ColorSpace colorSpace, ColorRange range)
    {
        using SKBitmap bitmap = new(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Opaque));
        Pixels.Fill(bitmap, 0xFF3C78DCu); // R=DC G=78 B=3C
        using AVFrame frame = AVFrame.Allocate();
        frame.Width = 32;
        frame.Height = 32;
        frame.PixelFormat = PixelFormat.YUV420P;
        frame.ColorSpace = colorSpace;
        frame.ColorRange = range;

        bitmap.CopyTo(frame);

        // untagged frames are tagged with what was written: BT.601, limited range
        if (colorSpace == ColorSpace.Unspecified)
            Assert.AreEqual((ColorSpace.SMPTE170M, ColorRange.MPEG), (frame.ColorSpace, frame.ColorRange));
        var expected = ColorReference.ToYuv(frame.ColorSpace, frame.ColorRange, 0xDC, 0x78, 0x3C);
        Pixels.AreNear(expected.Y, Pixels.Luma(frame, 16, 16), Tolerance, "Y");
        Pixels.AreNear(expected.U, Pixels.Chroma(frame, 1, 8, 8), Tolerance, "U");
        Pixels.AreNear(expected.V, Pixels.Chroma(frame, 2, 8, 8), Tolerance, "V");

        using SKBitmap back = frame.ToSKBitmap(SKColorType.Rgba8888);
        var p = Pixels.Get(back, 16, 16);
        Pixels.AreNear(0xDC, p.R, 3, "round trip R");
        Pixels.AreNear(0x78, p.G, 3, "round trip G");
        Pixels.AreNear(0x3C, p.B, 3, "round trip B");
    }

    [TestMethod]
    public void HdrPqFrame_IsToneMappedToSdr()
    {
        // 10 bit BT.2020 PQ grey at ~100 nits: the raw PQ code value (0.51) would give ~130
        using AVFrame frame = Pixels.YuvFrame(32, 32, 64 + (int)Math.Round(0.508 * 876), 512, 512, PixelFormat.YUV420P10LE);
        frame.ColorSpace = ColorSpace.BT2020_NCL;
        frame.ColorRange = ColorRange.MPEG;
        frame.ColorPrimaries = ColorPrimaries.BT2020;
        frame.ColorTransferCharacteristic = ColorTransferCharacteristic.SMPTE2084;

        using SKBitmap bitmap = frame.ToSKBitmap(SKColorType.Rgba8888);

        var p = Pixels.Get(bitmap, 16, 16);
        Assert.IsTrue(p.G is > 150 and < 230, $"PQ signal was not tone mapped: {p}");
    }

    [TestMethod]
    public void SdrWhite_IntoPqFrame_IsSdrReferenceWhite()
    {
        using SKBitmap bitmap = new(new SKImageInfo(32, 32, SKColorType.Rgba8888, SKAlphaType.Opaque));
        Pixels.Fill(bitmap, 0xFFFFFFFFu);
        using AVFrame frame = AVFrame.Allocate();
        frame.Width = 32;
        frame.Height = 32;
        frame.PixelFormat = PixelFormat.YUV420P10LE;
        frame.ColorSpace = ColorSpace.BT2020_NCL;
        frame.ColorRange = ColorRange.MPEG;
        frame.ColorPrimaries = ColorPrimaries.BT2020;
        frame.ColorTransferCharacteristic = ColorTransferCharacteristic.SMPTE2084;

        bitmap.CopyTo(frame);

        // SDR white = 203 nits -> PQ 0.58 -> 64 + 0.58 * 876 = 572 (unmapped white would be 940)
        Pixels.AreNear(572, Pixels.Luma10(frame, 16, 16), 15, "Y");
    }

    [TestMethod]
    public void InterlacedFrame_KeepsAllLines()
    {
        using AVFrame frame = Pixels.YuvFrame(32, 32, 50, 128, 128);
        for (int row = 1; row < 32; row += 2)
            Pixels.FillLumaRow(frame, row, 200);
        Pixels.SetInterlaced(frame);

        using SKBitmap bitmap = frame.ToSKBitmap(SKColorType.Rgba8888);

        var even = Pixels.Get(bitmap, 16, 10);
        var odd = Pixels.Get(bitmap, 16, 11);
        Assert.IsTrue(even.G < 60 && odd.G > 190, $"lines were lost: even {even}, odd {odd}");
    }
}
