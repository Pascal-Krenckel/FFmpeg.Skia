namespace FFmpeg.Skia.Tests;

/// <summary>
/// Tests for the AVFrame &lt;-&gt; SKBitmap/SKImage extension methods: row strides, cropping, buffer sharing and alpha.
/// </summary>
[TestClass]
public class ExtensionsTest
{
    private static AVFrame CroppedFrame()
    {
        AVFrame frame = Pixels.RgbaFrame(333, 201);
        frame.CropLeft = 5;
        frame.CropTop = 7;
        frame.CropRight = 11;
        frame.CropBottom = 3;
        return frame;
    }

    #region Row strides

    [TestMethod]
    public void ToSKBitmap_CopiesFrameWithPaddedRows()
    {
        using AVFrame frame = Pixels.RgbaFrame(333, 201);
        Assert.AreNotEqual(333 * 4, frame.LineSize[0], "test setup: the frame rows must be padded");

        using SKBitmap bitmap = frame.ToSKBitmap();

        Assert.AreEqual(SKColorType.Rgba8888, bitmap.ColorType);
        Pixels.AssertPattern(bitmap);
    }

    [TestMethod]
    public void CopyTo_ExistingBitmap_CopiesFrameWithPaddedRows()
    {
        using AVFrame frame = Pixels.RgbaFrame(333, 201);
        using SKBitmap bitmap = new(new SKImageInfo(333, 201, SKColorType.Rgba8888, SKAlphaType.Unpremul));

        frame.CopyTo(bitmap);

        Pixels.AssertPattern(bitmap);
    }

    [TestMethod]
    public void SKImage_ToAVFrame_ImageWithPaddedRows()
    {
        using AVFrame frame = Pixels.RgbaFrame(333, 201);
        using SKImage image = frame.AsSKImage(); // row bytes of the frame

        using AVFrame copy = image.ToAVFrame();

        Assert.AreEqual((333, 201), (copy.Width, copy.Height));
        foreach ((int x, int y) in new[] { (0, 0), (332, 0), (0, 200), (332, 200), (100, 100) })
            Assert.AreEqual(((byte)x, (byte)y), (Pixels.Get(copy, x, y).R, Pixels.Get(copy, x, y).G), $"pixel ({x},{y})");
    }

    [TestMethod]
    public void SKBitmap_ToAVFrame_SubsetBitmap()
    {
        using SKBitmap parent = new(new SKImageInfo(333, 201, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (AVFrame frame = Pixels.RgbaFrame(333, 201))
            frame.CopyTo(parent);
        using SKBitmap subset = new();
        Assert.IsTrue(parent.ExtractSubset(subset, new SKRectI(10, 20, 110, 70)));

        using AVFrame copy = subset.ToAVFrame();

        Assert.AreEqual((100, 50), (copy.Width, copy.Height));
        Assert.AreEqual(((byte)109, (byte)69), (Pixels.Get(copy, 99, 49).R, Pixels.Get(copy, 99, 49).G));
    }

    [TestMethod]
    public void SKBitmap_ToAVFrame_ConvertsToYuvAndBack()
    {
        using SKBitmap parent = new(new SKImageInfo(101, 77, SKColorType.Bgra8888, SKAlphaType.Opaque));
        Pixels.Fill(parent, 0xFF20A0E0u); // B=E0 G=A0 R=20
        using SKBitmap subset = new();
        Assert.IsTrue(parent.ExtractSubset(subset, new SKRectI(3, 3, 99, 75)));

        using AVFrame yuv = subset.ToAVFrame(PixelFormat.YUV420P);
        using SKBitmap back = yuv.ToSKBitmap(SKColorType.Rgba8888);

        Assert.AreEqual((PixelFormat.YUV420P, 96, 72), (yuv.PixelFormat, yuv.Width, yuv.Height));
        var p = Pixels.Get(back, 50, 40);
        Pixels.AreNear(0x20, p.R, 6, "R");
        Pixels.AreNear(0xA0, p.G, 6, "G");
        Pixels.AreNear(0xE0, p.B, 6, "B");
    }

    #endregion

    #region Cropping

    [TestMethod]
    public void AsSKBitmap_AppliesCrop()
    {
        using AVFrame frame = CroppedFrame();

        using SKBitmap bitmap = frame.AsSKBitmap();

        Assert.AreEqual((317, 191), (bitmap.Width, bitmap.Height));
        Pixels.AssertPattern(bitmap, 5, 7);
    }

    [TestMethod]
    public void AsSKImage_AppliesCrop()
    {
        using AVFrame frame = CroppedFrame();

        using SKImage image = frame.AsSKImage();
        using SKPixmap pixmap = image.PeekPixels()!;

        Assert.AreEqual(((byte)5, (byte)7), (Pixels.Get(pixmap, 0, 0).R, Pixels.Get(pixmap, 0, 0).G));
        Assert.AreEqual((unchecked((byte)321), (byte)197), (Pixels.Get(pixmap, 316, 190).R, Pixels.Get(pixmap, 316, 190).G));
    }

    [TestMethod]
    public void ToSKBitmap_And_CopyTo_ApplyCrop()
    {
        using AVFrame frame = CroppedFrame();

        using (SKBitmap bitmap = frame.ToSKBitmap())
            Pixels.AssertPattern(bitmap, 5, 7);
        using SKBitmap target = new(new SKImageInfo(317, 191, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        frame.CopyTo(target);
        Pixels.AssertPattern(target, 5, 7);
        using SKBitmap empty = new();
        frame.CopyTo(empty);
        Assert.AreEqual((317, 191), (empty.Width, empty.Height));
        Pixels.AssertPattern(empty, 5, 7);
    }

    [TestMethod]
    public void SKImage_ToAVFrame_FromCroppedFrame()
    {
        using AVFrame frame = CroppedFrame();
        using SKImage image = frame.AsSKImage();

        using AVFrame copy = image.ToAVFrame();

        Assert.AreEqual((317, 191), (copy.Width, copy.Height));
        Assert.AreEqual((unchecked((byte)321), (byte)197), (Pixels.Get(copy, 316, 190).R, Pixels.Get(copy, 316, 190).G));
    }

    [TestMethod]
    public void ToSKBitmap_CroppedYuvFrame_ConvertsVisibleRegion()
    {
        using AVFrame frame = Pixels.YuvFrame(64, 64, 40, 128, 128);
        for (int row = 32; row < 64; row++)
            Pixels.FillLumaRow(frame, row, 200);
        frame.CropTop = 32;

        using SKBitmap bitmap = frame.ToSKBitmap(SKColorType.Rgba8888);

        Assert.AreEqual(32, bitmap.Height);
        Assert.IsTrue(Pixels.Get(bitmap, 10, 0).G > 180, $"the top row should be bright: {Pixels.Get(bitmap, 10, 0)}");
    }

    #endregion

    #region Buffer sharing

    [TestMethod]
    public void Reference_DoesNotWriteIntoPreviouslyReferencedFrame()
    {
        using AVFrame first = Pixels.RgbaFrame(64, 48);
        using SKBitmap bitmap = new();
        bitmap.Reference(first);
        using AVFrame second = Pixels.YuvFrame(64, 48, 235, 128, 128);

        bitmap.Reference(second);

        Assert.AreEqual(((byte)10, (byte)10, (byte)77), (Pixels.Get(first, 10, 10).R, Pixels.Get(first, 10, 10).G, Pixels.Get(first, 10, 10).B));
        Assert.IsTrue(Pixels.Get(bitmap, 10, 10).G > 240, $"bitmap shows {Pixels.Get(bitmap, 10, 10)}");
    }

    [TestMethod]
    public void AsSKBitmap_And_AsSKImage_ReleaseTheirFrameReference()
    {
        using AVFrame frame = Pixels.RgbaFrame(40, 30);
        Assert.AreEqual(1, Pixels.BufferReferences(frame));

        using (SKBitmap bitmap = frame.AsSKBitmap())
            Assert.AreEqual(2, Pixels.BufferReferences(frame), "AsSKBitmap shares the buffer");
        Assert.AreEqual(1, Pixels.BufferReferences(frame), "disposing the bitmap releases the frame");

        using (SKImage image = frame.AsSKImage())
            Assert.AreEqual(2, Pixels.BufferReferences(frame), "AsSKImage shares the buffer");
        Assert.AreEqual(1, Pixels.BufferReferences(frame), "disposing the image releases the frame");
    }

    [TestMethod]
    public void CopyTo_SharedFrame_IsMadeWritable()
    {
        using AVFrame target = Pixels.YuvFrame(64, 48, 16, 128, 128);
        using AVFrame other = target.Clone(); // shares the buffer
        using SKBitmap white = new(new SKImageInfo(320, 240, SKColorType.Rgba8888, SKAlphaType.Opaque));
        Pixels.Fill(white, 0xFFFFFFFFu);

        white.CopyTo(target);

        Assert.AreEqual((64, 48), (target.Width, target.Height), "the frame keeps its size");
        Assert.IsTrue(Pixels.Luma(target, 10, 10) > 230, $"target luma {Pixels.Luma(target, 10, 10)}");
        Assert.AreEqual((byte)16, Pixels.Luma(other, 10, 10), "the frame sharing the buffer was modified");
    }

    [TestMethod]
    public void SKImage_CopyTo_InitializedFrame_Scales()
    {
        using AVFrame source = Pixels.RgbaFrame(64, 48);
        using SKImage image = source.ToSKImage();
        using AVFrame target = AVFrame.Allocate();
        target.Width = 32;
        target.Height = 24;
        target.PixelFormat = PixelFormat.BGRA;

        image.CopyTo(target);

        var p = Pixels.Get(target, 31, 23);
        Pixels.AreNear(62, p.R, 3, "R");
        Pixels.AreNear(46, p.G, 3, "G");
    }

    [TestMethod]
    public void LazySKImage_ToAVFrame()
    {
        using SKBitmap bitmap = new(new SKImageInfo(8, 4, SKColorType.Rgba8888, SKAlphaType.Opaque));
        Pixels.Fill(bitmap, 0xFF030201u);
        using SKImage raster = SKImage.FromBitmap(bitmap)!;
        using SKData png = raster.Encode(SKEncodedImageFormat.Png, 100);
        using SKImage lazy = SKImage.FromEncodedData(png);
        Assert.IsNull(lazy.PeekPixels(), "test setup: the image must not be a raster image");

        using AVFrame frame = lazy.ToAVFrame();

        Assert.AreEqual(((byte)1, (byte)2, (byte)3, (byte)255), Pixels.Get(frame, 7, 3));
    }

    #endregion

    #region Alpha

    [TestMethod]
    public void SkiaObjectsFromFrames_UseStraightAlpha()
    {
        using AVFrame frame = Pixels.RgbaFrame(16, 16, alpha: 128);

        using SKBitmap shared = frame.AsSKBitmap();
        using SKBitmap copy = frame.ToSKBitmap();
        using SKImage image = frame.AsSKImage();

        Assert.AreEqual(SKAlphaType.Unpremul, shared.AlphaType);
        Assert.AreEqual(SKAlphaType.Unpremul, copy.AlphaType);
        Assert.AreEqual(SKAlphaType.Unpremul, image.AlphaType);
    }

    [TestMethod]
    public void PremultipliedBitmap_ToAVFrame_HasStraightAlpha()
    {
        using SKBitmap bitmap = new(new SKImageInfo(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul));
        Pixels.Fill(bitmap, 0x80404040u); // premultiplied (64, 64, 64, 128)

        using AVFrame frame = bitmap.ToAVFrame();

        Pixels.AreNear(128, Pixels.Get(frame, 3, 3).R, 1, "straight red");
        Assert.AreEqual((byte)128, Pixels.Get(frame, 3, 3).A);
    }

    #endregion
}
