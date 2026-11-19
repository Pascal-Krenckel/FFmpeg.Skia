using FFmpeg.AutoGen;
using FFmpeg.Unsafe;
using System.Diagnostics;

namespace FFmpeg.Skia.Tests;

/// <summary>
/// One-time setup shared by all tests.
/// </summary>
[TestClass]
public static class TestSetup
{
    /// <summary>
    /// Loads the FFmpeg libraries once, before any test runs.
    /// </summary>
    [AssemblyInitialize]
    public static void Initialize(TestContext _) => FFmpegLoader.Initialize();
}

/// <summary>
/// Paths to the files in Test-Files (created by make_test_files.py), resolved against the test output directory.
/// </summary>
internal static class TestFiles
{
    public static string Directory { get; } = Path.Combine(AppContext.BaseDirectory, "Test-Files");

    /// <summary>APNG, RGBA 333x201 (rows not aligned), 5 frames: 100, 300, 100, 500, 500 ms, alpha 128, R = x, G = y, B = 40 * frame.</summary>
    public static string AnimatedPng => Get("anim.apng");
    /// <summary>GIF 64x48, 3 frames: red, green, blue.</summary>
    public static string AnimatedGif => Get("anim.gif");
    /// <summary>H.264 320x240, 25 fps, 100 frames, key frame every 25 frames, B-frames; frame i is gray 16 + 2i.</summary>
    public static string Counter => Get("count.mp4");
    /// <summary>A single RGBA PNG 333x201.</summary>
    public static string StillPng => Get("still.png");
    /// <summary>H.264 720x480 with sample aspect ratio 32:27 (853x480 with square pixels), 3 frames.</summary>
    public static string Anamorphic => Get("anamorphic.mp4");
    /// <summary><see cref="Counter"/> with a display matrix that rotates it 90 degrees clockwise.</summary>
    public static string Rotated => Get("rotated.mp4");
    /// <summary>Audio only.</summary>
    public static string AudioOnly => Get("audio.wav");

    public static string Get(string fileName) => Path.Combine(Directory, fileName);
}

/// <summary>
/// Creates frames with known content and reads pixels back.
/// </summary>
internal static unsafe class Pixels
{
    /// <summary>RGBA frame with R = x, G = y, B = 77, A = <paramref name="alpha"/>; rows are padded by FFmpeg's default alignment.</summary>
    public static AVFrame RgbaFrame(int width, int height, byte alpha = 200)
    {
        AVFrame frame = AVFrame.Allocate();
        frame.Width = width;
        frame.Height = height;
        frame.PixelFormat = PixelFormat.RGBA;
        frame.CreateBuffer(0).ThrowIfError();
        for (int y = 0; y < height; y++)
        {
            byte* row = (byte*)frame.Data[0] + ((long)y * frame.LineSize[0]);
            for (int x = 0; x < width; x++)
            {
                row[(4 * x) + 0] = (byte)x;
                row[(4 * x) + 1] = (byte)y;
                row[(4 * x) + 2] = 77;
                row[(4 * x) + 3] = alpha;
            }
        }
        return frame;
    }

    /// <summary>Uniform planar YUV 4:2:0 frame (8 or 10 bit).</summary>
    public static AVFrame YuvFrame(int width, int height, int y, int u, int v, PixelFormat format = PixelFormat.YUV420P)
    {
        AVFrame frame = AVFrame.Allocate();
        frame.Width = width;
        frame.Height = height;
        frame.PixelFormat = format;
        frame.CreateBuffer(0).ThrowIfError();
        bool tenBit = format == PixelFormat.YUV420P10LE;
        for (int plane = 0; plane < 3; plane++)
        {
            int planeHeight = plane == 0 ? height : (height + 1) / 2;
            int planeWidth = plane == 0 ? width : (width + 1) / 2;
            int value = plane == 0 ? y : plane == 1 ? u : v;
            for (int row = 0; row < planeHeight; row++)
            {
                byte* line = (byte*)frame.Data[plane] + ((long)row * frame.LineSize[plane]);
                if (tenBit)
                    new Span<ushort>(line, planeWidth).Fill((ushort)value);
                else
                    new Span<byte>(line, planeWidth).Fill((byte)value);
            }
        }
        return frame;
    }

    public static void FillLumaRow(AVFrame frame, int row, byte value)
        => new Span<byte>((byte*)frame.Data[0] + ((long)row * frame.LineSize[0]), frame.Width).Fill(value);

    public static byte Luma(AVFrame frame, int x, int y) => ((byte*)frame.Data[0])[((long)y * frame.LineSize[0]) + x];

    public static int Luma10(AVFrame frame, int x, int y) => ((ushort*)((byte*)frame.Data[0] + ((long)y * frame.LineSize[0])))[x];

    public static byte Chroma(AVFrame frame, int plane, int x, int y) => ((byte*)frame.Data[plane])[((long)y * frame.LineSize[plane]) + x];

    public static void Fill(SKBitmap bitmap, uint value)
    {
        for (int y = 0; y < bitmap.Height; y++)
            new Span<uint>((byte*)bitmap.GetPixels() + ((long)y * bitmap.RowBytes), bitmap.Width).Fill(value);
    }

    public static byte Gray(SKBitmap bitmap, int x, int y) => ((byte*)bitmap.GetPixels())[((long)y * bitmap.RowBytes) + x];

    public static (byte R, byte G, byte B, byte A) Get(SKBitmap bitmap, int x, int y)
        => Read((byte*)bitmap.GetPixels() + ((long)y * bitmap.RowBytes) + (x * 4), bitmap.ColorType == SKColorType.Bgra8888);

    public static (byte R, byte G, byte B, byte A) Get(SKPixmap pixmap, int x, int y)
        => Read((byte*)pixmap.GetPixels() + ((long)y * pixmap.RowBytes) + (x * 4), pixmap.ColorType == SKColorType.Bgra8888);

    public static (byte R, byte G, byte B, byte A) Get(AVFrame frame, int x, int y)
        => Read((byte*)frame.Data[0] + ((long)y * frame.LineSize[0]) + (x * 4), frame.PixelFormat == PixelFormat.BGRA);

    private static (byte R, byte G, byte B, byte A) Read(byte* p, bool bgra) => bgra ? (p[2], p[1], p[0], p[3]) : (p[0], p[1], p[2], p[3]);

    /// <summary>Asserts the R = x + dx, G = y + dy, B = 77 pattern of <see cref="RgbaFrame"/>.</summary>
    public static void AssertPattern(SKBitmap bitmap, int dx = 0, int dy = 0)
    {
        foreach ((int x, int y) in new[] { (0, 0), (1, 0), (0, 1), (bitmap.Width - 1, 0), (0, bitmap.Height - 1), (bitmap.Width - 1, bitmap.Height - 1), (bitmap.Width / 2, bitmap.Height / 2) })
        {
            var p = Get(bitmap, x, y);
            Assert.AreEqual(((byte)(x + dx), (byte)(y + dy), (byte)77), (p.R, p.G, p.B), $"pixel ({x},{y})");
        }
    }

    /// <summary>The frame index of a <see cref="TestFiles.Counter"/> frame (gray 16 + 2i).</summary>
    public static int CounterIndex(SKBitmap bitmap)
        => (int)Math.Round((Get(bitmap, bitmap.Width / 2, bitmap.Height / 2).G - 16) / 2.0);

    public static void AreNear(int expected, int actual, int tolerance, string what)
        => Assert.IsTrue(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected}±{tolerance}, got {actual}");

    /// <summary>The reference count of the frame's first buffer.</summary>
    public static int BufferReferences(AVFrame frame)
    {
        _AVFrame* f = ((IAVPointer<_AVFrame>)frame).Pointer;
        return f->buf[0] == null ? 0 : ffmpeg.av_buffer_get_ref_count(f->buf[0]);
    }

    public static void SetInterlaced(AVFrame frame)
        => ((IAVPointer<_AVFrame>)frame).Pointer->flags |= ffmpeg.AV_FRAME_FLAG_INTERLACED;

    public static void WaitUntil(Func<bool> condition, int timeoutMs, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeoutMs)
                Assert.Fail(message);
            Thread.Sleep(5);
        }
    }
}

/// <summary>
/// The YCbCr formulas (ITU-R BT.601/709/2020) the conversions are checked against.
/// </summary>
internal static class ColorReference
{
    public static (double Kr, double Kb) Coefficients(ColorSpace colorSpace) => colorSpace switch
    {
        ColorSpace.BT709 => (0.2126, 0.0722),
        ColorSpace.BT2020_NCL => (0.2627, 0.0593),
        _ => (0.299, 0.114), // BT.601 (SMPTE170M, BT470BG) and FFmpeg's default for untagged YUV
    };

    public static (int R, int G, int B) ToRgb(PixelFormat format, ColorSpace colorSpace, ColorRange range, int y, int u, int v)
    {
        bool full = range == ColorRange.JPEG || format == PixelFormat.YUVJ420P;
        double max = format == PixelFormat.YUV420P10LE ? 1023 : 255, scale = max / 255;
        double yn = full ? y / max : (y - (16 * scale)) / (219 * scale);
        double cb = full ? (u - (128 * scale)) / max : (u - (128 * scale)) / (224 * scale);
        double cr = full ? (v - (128 * scale)) / max : (v - (128 * scale)) / (224 * scale);
        var (kr, kb) = Coefficients(colorSpace);
        double r = yn + (2 * (1 - kr) * cr), b = yn + (2 * (1 - kb) * cb), g = (yn - (kr * r) - (kb * b)) / (1 - kr - kb);
        return (To8(r), To8(g), To8(b));
    }

    public static (int Y, int U, int V) ToYuv(ColorSpace colorSpace, ColorRange range, int r8, int g8, int b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        var (kr, kb) = Coefficients(colorSpace);
        double y = (kr * r) + ((1 - kr - kb) * g) + (kb * b), cb = (b - y) / (2 * (1 - kb)), cr = (r - y) / (2 * (1 - kr));
        return range == ColorRange.JPEG
            ? ((int)Math.Round(y * 255), (int)Math.Round(128 + (cb * 255)), (int)Math.Round(128 + (cr * 255)))
            : ((int)Math.Round(16 + (y * 219)), (int)Math.Round(128 + (cb * 224)), (int)Math.Round(128 + (cr * 224)));
    }

    private static int To8(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
}
