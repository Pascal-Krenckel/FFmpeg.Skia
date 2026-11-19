using FFmpeg.Threading;
using System.Diagnostics;

namespace FFmpeg.Skia.Tests;

/// <summary>
/// Tests for <see cref="SKAnimatedImage"/>: manual and automatic decoding, timing, state and disposal.
/// </summary>
[TestClass]
public class SKAnimatedImageTest
{
    [TestMethod]
    public void DecodeNextImage_AnimatedPng()
    {
        using SKAnimatedImage image = SKAnimatedImage.Create(TestFiles.AnimatedPng);

        int n = 0;
        while (image.DecodeNextImage())
        {
            var p = Pixels.Get(image.Frame, 300, 200);
            Assert.AreEqual((unchecked((byte)300), (byte)200, (byte)(40 * n)), (p.R, p.G, p.B), $"frame {n}");
            n++;
        }

        Assert.AreEqual(5, n);
    }

    [TestMethod]
    public void DecodeNextImage_Gif()
    {
        using SKAnimatedImage image = SKAnimatedImage.Create(TestFiles.AnimatedGif);

        Assert.IsTrue(image.DecodeNextImage());

        var p = Pixels.Get(image.Frame, 5, 5);
        Assert.IsTrue(p.R > 200 && p.G < 50, $"the first frame should be red: {p}");
    }

    [TestMethod]
    public async Task DecodeNextImageAsync_And_SeekAsync_WithWorkQueue()
    {
        using FFmpegWorkQueue queue = new();
        SKAnimatedImage image = SKAnimatedImage.Create(TestFiles.AnimatedPng, workQueue: queue);

        int n = 0;
        while (await image.DecodeNextImageAsync())
            n++;
        Assert.AreEqual(5, n);
        Assert.IsFalse((await image.SeekAsync(TimeSpan.Zero)).IsError);
        Assert.IsTrue(await image.DecodeNextImageAsync());

        await image.DisposeAsync();
        Assert.AreEqual(DecodingState.Disposed, image.State);
    }

    [TestMethod]
    public void Start_PresentsEachFrameForItsDuration()
    {
        using SKAnimatedImage image = SKAnimatedImage.Create(TestFiles.AnimatedPng);
        Stopwatch watch = new();
        List<long> times = [];
        using ManualResetEventSlim finished = new();
        image.ReadyToRender += (_, _) => { lock (times) times.Add(watch.ElapsedMilliseconds); };
        image.Finished += (_, _) => { lock (times) times.Add(watch.ElapsedMilliseconds); finished.Set(); };

        watch.Start();
        image.Start(false);

        Assert.IsTrue(finished.Wait(5000), "playback did not finish");
        // frames: 0 ms (shown 100), 100 (300), 400 (100), 500 (500), 1000 (500) -> finished at 1500
        long[] expected = [0, 100, 400, 500, 1000, 1500];
        lock (times)
        {
            Assert.AreEqual(expected.Length, times.Count, string.Join(", ", times));
            for (int i = 0; i < expected.Length; i++)
                Assert.IsTrue(Math.Abs(times[i] - expected[i]) <= 60, $"event {i} at {times[i]} ms, expected {expected[i]} ms ({string.Join(", ", times)})");
        }
    }

    [TestMethod]
    public void Stop_ThenStartAgain()
    {
        using SKAnimatedImage image = SKAnimatedImage.Create(TestFiles.Counter);
        image.Start(true);
        Thread.Sleep(100);

        Assert.IsTrue(image.Stop().Wait(2000), "stop did not finish");
        Assert.AreEqual(DecodingState.None, image.State);
        image.Start(true);
        Thread.Sleep(100);
        Assert.IsTrue(image.Stop().Wait(2000), "second stop did not finish");
    }

    [TestMethod]
    public void WhilePlaying_ManualDecodingAndSecondStartThrow()
    {
        using SKAnimatedImage image = SKAnimatedImage.Create(TestFiles.Counter);
        image.Start(true);

        Assert.AreEqual(DecodingState.Automatic, image.State);
        Assert.Throws<InvalidOperationException>(() => image.DecodeNextImage());
        Assert.Throws<InvalidOperationException>(() => image.Seek(TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => image.Start(true));

        Assert.IsTrue(image.Stop().Wait(2000), "stop did not finish");
        Assert.IsTrue(image.DecodeNextImage(), "manual decoding after the loop ended");
    }

    [TestMethod]
    public void Dispose_DuringManualDecoding()
    {
        SKAnimatedImage image = SKAnimatedImage.Create(TestFiles.Counter);
        using ManualResetEventSlim started = new();
        Exception? unexpected = null;
        Thread worker = new(() =>
        {
            started.Set();
            try
            {
                while (true)
                {
                    if (!image.DecodeNextImage())
                        _ = image.Seek(TimeSpan.Zero);
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
            catch (Exception ex) { unexpected = ex; }
        });
        worker.Start();
        started.Wait();
        Thread.Sleep(30);

        image.Dispose();

        Assert.IsTrue(worker.Join(3000), "the decoding thread did not stop");
        Assert.IsNull(unexpected, unexpected?.ToString() ?? "");
        Assert.AreEqual(DecodingState.Disposed, image.State);
    }

    [TestMethod]
    public void Start_Looping_StillImage_Finishes()
    {
        using SKAnimatedImage image = SKAnimatedImage.Create(TestFiles.StillPng);
        int frames = 0;
        using ManualResetEventSlim finished = new();
        image.ReadyToRender += (_, _) => Interlocked.Increment(ref frames);
        image.Finished += (_, _) => finished.Set();

        image.Start(true);

        Assert.IsTrue(finished.Wait(2000), $"no Finished; {frames} frames presented");
        Assert.AreEqual(1, Volatile.Read(ref frames));
    }

    [TestMethod]
    public void Dispose_FromReadyToRender()
    {
        SKAnimatedImage image = SKAnimatedImage.Create(TestFiles.Counter);
        using ManualResetEventSlim disposed = new();
        image.ReadyToRender += (_, _) =>
        {
            image.Dispose();
            disposed.Set();
            _ = Pixels.Get(image.Frame, 1, 1); // the frame stays valid until the handler returns
        };

        image.Start(true);

        Assert.IsTrue(disposed.Wait(3000), "Dispose from a handler did not return");
    }

    [TestMethod]
    public void Create_WithoutVideoStream_Throws()
        => Assert.Throws<ArgumentOutOfRangeException>(() => SKAnimatedImage.Create(TestFiles.AudioOnly));
}
