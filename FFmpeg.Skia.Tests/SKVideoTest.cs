namespace FFmpeg.Skia.Tests;

/// <summary>
/// Tests for <see cref="SKVideo"/>: lifetime, looping and timing.
/// </summary>
[TestClass]
public class SKVideoTest
{
    [TestMethod]
    public void Stop_ThenDispose()
    {
        SKVideo video = new(TestFiles.Counter);
        video.Start();
        Thread.Sleep(100);

        video.Stop();
        video.Dispose();

        Assert.IsTrue(video.Disposed);
    }

    [TestMethod]
    public void SeekInEnded_LoopsTheVideo()
    {
        using SKVideo video = new(TestFiles.AnimatedPng);
        int ended = 0;
        video.Ended += (_, _) =>
        {
            if (Interlocked.Increment(ref ended) < 3)
                _ = video.Seek(TimeSpan.Zero);
        };
        video.FrameReadyToRender += (_, _) => { };

        video.Start();

        Pixels.WaitUntil(() => Volatile.Read(ref ended) >= 2, 5000, "playback did not loop");
    }

    [TestMethod]
    public void Restart_WhilePlaying_KeepsRealTime()
    {
        using SKVideo video = new(TestFiles.Counter);
        int frames = 0;
        video.FrameReadyToRender += (_, _) => Interlocked.Increment(ref frames);
        video.Start();
        Thread.Sleep(600);

        video.Restart();
        Thread.Sleep(50);
        Interlocked.Exchange(ref frames, 0);
        Thread.Sleep(400);

        // 25 fps -> about 10 frames in 400 ms
        int count = Volatile.Read(ref frames);
        Assert.IsTrue(count is >= 6 and <= 14, $"{count} frames in 400 ms after Restart");
    }

    [TestMethod]
    public void Dispose_FromFrameHandler_WhileDrawing()
    {
        SKVideo video = new(TestFiles.Counter);
        using ManualResetEventSlim disposed = new();
        int n = 0;
        video.FrameReadyToRender += (_, e) =>
        {
            if (Interlocked.Increment(ref n) == 3)
            {
                video.Dispose();
                disposed.Set();
            }
            Thread.Sleep(20);
            _ = Pixels.Get(e.frame, 1, 1); // the frame stays valid until the handler returns
        };

        video.Start();

        Assert.IsTrue(disposed.Wait(5000), "Dispose from a handler did not return");
    }

    [TestMethod]
    public void Faulted_IsNotRaisedForNormalPlayback()
    {
        using SKVideo video = new(TestFiles.AnimatedPng);
        Exception? error = null;
        using ManualResetEventSlim ended = new();
        video.Faulted += (_, e) => error = e;
        video.Ended += (_, _) => ended.Set();

        video.Start();

        Assert.IsTrue(ended.Wait(5000), "playback did not end");
        Assert.IsNull(error);
    }
}
