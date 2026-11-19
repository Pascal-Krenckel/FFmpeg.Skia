using FFmpeg.Formats;
using FFmpeg.Threading;
using FFmpeg.Utils;
using System.Diagnostics;

namespace FFmpeg.Skia;

/// <summary>
/// Represents an animated image that can be decoded and presented frame by frame.
/// </summary>
/// <remarks>
/// <para>
/// The image supports two mutually exclusive decoding modes: manual and automatic.
/// In manual mode, individual frames are decoded by calling
/// <see cref="DecodeNextImage"/> or <see cref="DecodeNextImageAsync(CancellationToken)"/>.
/// </para>
/// <para>
/// In automatic mode, frames are decoded and presented according to their timing
/// information after calling <see cref="Start(bool)"/>.
/// </para>
/// <para>
/// The decoded frame is exposed through <see cref="Frame"/> and its timing
/// information through <see cref="FrameInfo"/>.
/// </para>
/// <para>
/// Only one decoding or seeking operation can be active at a time.
/// </para>
/// </remarks>
public sealed class SKAnimatedImage : IDisposable, IAsyncDisposable
{
    /// <summary>Display time of a frame without duration or timestamps.</summary>
    private static readonly TimeSpan DefaultFrameDelay = TimeSpan.FromMilliseconds(100);

    readonly MediaSource source;
    readonly AVFrame frame;
    readonly FrameConverter converter;
    readonly int videoStreamIndex;
    SKBitmap frontBuffer, backBuffer;
    readonly FFmpegWorkQueue? workQueue;
    CancellationTokenSource? cts;
    CancellableWorkItem? asyncOperation;
    Task decodingTask = Task.CompletedTask;
    Task handlerTask = Task.CompletedTask;
    TaskCompletionSource<bool> manualOperation = CompletedOperation();
    readonly object stateLock = new();
    int decodingState = 0;
    int released = 0;

    /// <summary>
    /// Gets the most recently decoded frame.
    /// </summary>
    /// <value>
    /// The bitmap containing the most recently decoded frame.
    /// </value>
    /// <remarks>
    /// The bitmap is reused internally between frames. Consumers should not dispose
    /// the returned bitmap or retain it for use after another frame has been decoded.
    /// </remarks>
    public SKBitmap Frame => frontBuffer;

    /// <summary>
    /// Gets the timing information of the most recently decoded frame.
    /// </summary>
    /// <value>
    /// The presentation timestamp and duration of the current frame.
    /// </value>
    public FFCodecFrameInfo FrameInfo { get; private set; }

    /// <summary>
    /// Gets the current decoding state.
    /// </summary>
    /// <value>
    /// The current <see cref="DecodingState"/> of the image.
    /// </value>
    public DecodingState State => (DecodingState)Volatile.Read(ref decodingState);

    private SKAnimatedImage(MediaSource source, int videoStreamIndex, FFmpegWorkQueue? workQueue)
    {
        if (videoStreamIndex == -1)
            videoStreamIndex = source.FindBestStream(MediaType.Video);
        if (videoStreamIndex < 0 || videoStreamIndex >= source.Streams.Count)
            throw new ArgumentOutOfRangeException(nameof(videoStreamIndex), videoStreamIndex, "The media does not contain this (or any) video stream.");
        if (source.Streams[videoStreamIndex].CodecParameters.MediaType != MediaType.Video)
            throw new ArgumentException($"Stream {videoStreamIndex} is not a video stream.", nameof(videoStreamIndex));
        foreach (var stream in source.Streams)
            stream.Discard = Formats.DiscardFlags.All;
        source.Streams[videoStreamIndex].Discard = Formats.DiscardFlags.Default;
        this.source = source;
        this.videoStreamIndex = videoStreamIndex;
        this.workQueue = workQueue;
        // allocated last: nothing native leaks if the checks above throw
        frontBuffer = new SKBitmap();
        backBuffer = new SKBitmap();
        frame = AVFrame.Allocate();
        converter = new FrameConverter();
    }

    private static SKAnimatedImage Create(MediaSource source, int videoIndex, FFmpegWorkQueue? workQueue)
    {
        try
        {
            return new(source, videoIndex, workQueue);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Creates an <see cref="SKAnimatedImage"/> from a stream.
    /// </summary>
    /// <param name="stream">
    /// The stream containing the image.
    /// </param>
    /// <param name="videoIndex">
    /// The index of the video stream to decode, or <c>-1</c> to automatically select
    /// the best video stream.
    /// </param>
    /// <param name="workQueue">
    /// The optional <see cref="FFmpegWorkQueue"/> used for asynchronous decoding and
    /// automatic decoding. If <see langword="null"/>, only synchronous decoding and
    /// automatic decoding without queued FFmpeg operations are available.
    /// </param>
    /// <param name="format">
    /// The optional input format to use when opening the stream.
    /// </param>
    /// <param name="options">
    /// Optional format options used when opening the source.
    /// </param>
    /// <param name="hwDevice">
    /// The hardware device to use for decoding, or <see cref="HW.DeviceType.None"/>
    /// to use software decoding.
    /// </param>
    /// <returns>
    /// A new <see cref="SKAnimatedImage"/> instance.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The media has no video stream, or <paramref name="videoIndex"/> is not a valid stream index.
    /// </exception>
    public static SKAnimatedImage Create(Stream stream, int videoIndex = -1, FFmpegWorkQueue? workQueue = null, InputFormat? format = null, IDictionary<string, string>? options = null, HW.DeviceType hwDevice = HW.DeviceType.None)
        => Create(MediaSource.Open(stream, format, options, hwDevice), videoIndex, workQueue);

    /// <summary>
    /// Creates an <see cref="SKAnimatedImage"/> from a media URL.
    /// </summary>
    /// <param name="url">
    /// The URL or path of the media source.
    /// </param>
    /// <param name="videoIndex">
    /// The index of the video stream to decode, or <c>-1</c> to automatically select
    /// the best video stream.
    /// </param>
    /// <param name="workQueue">
    /// The optional <see cref="FFmpegWorkQueue"/> used for asynchronous decoding and
    /// automatic decoding. If <see langword="null"/>, only synchronous decoding and
    /// automatic decoding without queued FFmpeg operations are available.
    /// </param>
    /// <param name="format">
    /// The optional input format to use when opening the source.
    /// </param>
    /// <param name="options">
    /// Optional format options used when opening the source.
    /// </param>
    /// <param name="hwDevice">
    /// The hardware device to use for decoding, or <see cref="HW.DeviceType.None"/>
    /// to use software decoding.
    /// </param>
    /// <returns>
    /// A new <see cref="SKAnimatedImage"/> instance.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The media has no video stream, or <paramref name="videoIndex"/> is not a valid stream index.
    /// </exception>
    public static SKAnimatedImage Create(string url, int videoIndex = -1, FFmpegWorkQueue? workQueue = null, InputFormat? format = null, IDictionary<string, string>? options = null, HW.DeviceType hwDevice = HW.DeviceType.None)
        => Create(MediaSource.Open(url, format, options, hwDevice), videoIndex, workQueue);

    #region Decoding steps

    /// <summary>Decodes the next frame of the video stream into <paramref name="target"/>.</summary>
    private AVResult32 DecodeInto(SKBitmap target)
    {
        AVResult32 result;
        do
            result = source.ReadAndDecodeAVFrame(frame);
        while (!result.IsError && result != videoStreamIndex);
        if (result.IsError)
            return result;
        FrameInterop.CopyToBitmap(frame, target, converter);
        return 0;
    }

    private FFCodecFrameInfo CurrentTiming()
    {
        _ = FFCodecFrameInfo.TryFromFrame(frame, out FFCodecFrameInfo info);
        return info;
    }

    private async Task<(AVResult32, FFCodecFrameInfo)> DecodeStepAsync(SKBitmap target, CancellationToken token)
    {
        AVResult32 result;
        if (workQueue != null)
            result = await (asyncOperation = workQueue.QueueFFmpegWorkItem(() => DecodeInto(target), token)).ConfigureAwait(false);
        else
            result = DecodeInto(target);
        return result.IsError ? (result, default) : (result, CurrentTiming());
    }

    private async Task<AVResult32> SeekStepAsync(TimeSpan time, CancellationToken token)
        => workQueue != null
            ? await (asyncOperation = workQueue.QueueFFmpegWorkItem(() => source.SeekExactly(time, videoStreamIndex), token)).ConfigureAwait(false)
            : source.SeekExactly(time, videoStreamIndex);

    #endregion

    #region Manual decoding

    /// <summary>
    /// Decodes the next frame of the animation.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if a frame was decoded; otherwise,
    /// <see langword="false"/> if the end of the animation was reached.
    /// </returns>
    /// <exception cref="ObjectDisposedException">
    /// The image has been disposed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Another decoding, seeking, or automatic playback operation is currently active.
    /// </exception>
    public bool DecodeNextImage()
    {
        using (BeginManualOperation())
        {
            AVResult32 result = DecodeInto(backBuffer);
            if (result == AVResult32.EndOfFile)
                return false;
            result.ThrowIfError();
            (frontBuffer, backBuffer) = (backBuffer, frontBuffer);
            FrameInfo = CurrentTiming();
            return true;
        }
    }

    /// <summary>
    /// Asynchronously decodes the next frame of the animation.
    /// </summary>
    /// <param name="token">
    /// A token used to cancel the asynchronous decoding operation.
    /// </param>
    /// <returns>
    /// A task that completes with <see langword="true"/> if a frame was decoded;
    /// otherwise, <see langword="false"/> if the end of the animation was reached.
    /// </returns>
    /// <exception cref="ObjectDisposedException">
    /// The image has been disposed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// An <see cref="FFmpegWorkQueue"/> was not provided when the image was created,
    /// or another decoding, seeking, or automatic playback operation is currently active.
    /// </exception>
    public async Task<bool> DecodeNextImageAsync(CancellationToken token = default)
    {
        CheckAsync();
        using (BeginManualOperation())
        {
            (var result, var info) = await DecodeStepAsync(backBuffer, token).ConfigureAwait(false);
            if (result == AVResult32.EndOfFile)
                return false;
            result.ThrowIfError();
            (frontBuffer, backBuffer) = (backBuffer, frontBuffer);
            FrameInfo = info;
            return true;
        }
    }

    /// <summary>
    /// Seeks the decoder to the specified presentation timestamp.
    /// </summary>
    /// <param name="time">
    /// The presentation timestamp to seek to.
    /// </param>
    /// <returns>
    /// The FFmpeg result of the seek operation.
    /// </returns>
    /// <remarks>
    /// After a successful seek, <see cref="FrameInfo"/> is reset because the previously
    /// decoded frame no longer represents the current decoder position.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// The image has been disposed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Another decoding, seeking, or automatic playback operation is currently active.
    /// </exception>
    public AVResult32 Seek(TimeSpan time)
    {
        using (BeginManualOperation())
        {
            var result = source.SeekExactly(time, videoStreamIndex);
            if (!result.IsError)
                FrameInfo = default;
            return result;
        }
    }

    /// <summary>
    /// Asynchronously seeks the decoder to the specified presentation timestamp.
    /// </summary>
    /// <param name="time">
    /// The presentation timestamp to seek to.
    /// </param>
    /// <param name="token">
    /// A token used to cancel the asynchronous seek operation.
    /// </param>
    /// <returns>
    /// A task that completes with the FFmpeg result of the seek operation.
    /// </returns>
    /// <exception cref="ObjectDisposedException">
    /// The image has been disposed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// An <see cref="FFmpegWorkQueue"/> was not provided when the image was created,
    /// or another decoding, seeking, or automatic playback operation is currently active.
    /// </exception>
    public async Task<AVResult32> SeekAsync(TimeSpan time, CancellationToken token = default)
    {
        CheckAsync();
        using (BeginManualOperation())
        {
            var result = await SeekStepAsync(time, token).ConfigureAwait(false);
            if (!result.IsError)
                FrameInfo = default;
            return result;
        }
    }

    #endregion

    #region Automatic decoding

    /// <summary>
    /// Starts automatic decoding and presentation of the animation.
    /// </summary>
    /// <param name="looping">
    /// <see langword="true"/> to restart the animation when its end is reached;
    /// otherwise, playback stops when the end is reached. An image with a single frame
    /// always stops after that frame.
    /// </param>
    /// <remarks>
    /// <para>
    /// Automatic decoding runs independently of the calling thread.
    /// </para>
    /// <para>
    /// A <see cref="ReadyToRender"/> event is raised whenever a new frame is ready
    /// to be presented. Each frame is presented for its duration (or the time to the next frame),
    /// measured from the moment it is presented. The decoding loop waits for the event handlers to complete
    /// before decoding the next frame, allowing presentation to provide back-pressure
    /// when necessary.
    /// </para>
    /// <para>
    /// Automatic decoding and manual decoding or seeking are mutually exclusive. After <see cref="Stop"/>, wait for
    /// the returned task before starting again or decoding manually.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// The image has been disposed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Another decoding or seeking operation is currently active, or automatic
    /// playback is already running or still stopping.
    /// </exception>
    public void Start(bool looping)
    {
        BeginOperation(DecodingState.Automatic);
        cts?.Dispose(); // the previous loop has completed, otherwise BeginOperation had thrown
        cts = new();
        CancellationToken token = cts.Token;
        decodingTask = Task.Factory.StartNew(() => DecodingLoopAsync(looping, token),
                                             CancellationToken.None,
                                             TaskCreationOptions.LongRunning,
                                             TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Stops automatic decoding and presentation.
    /// </summary>
    /// <returns>
    /// A task that completes when the automatic decoding operation has stopped.
    /// </returns>
    /// <exception cref="ObjectDisposedException">
    /// The image has been disposed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A manual decoding or seeking operation is active.
    /// </exception>
    public Task Stop()
    {
        ThrowIfDisposed();
        switch (State)
        {
            case DecodingState.Automatic:
                cts?.Cancel();
                return decodingTask;
            case DecodingState.None:
                return decodingTask; // not running (anymore)
            default:
                throw new InvalidOperationException("Automatic playback is not running; a manual operation is active.");
        }
    }

    private async Task DecodingLoopAsync(bool looping, CancellationToken token)
    {
        try
        {
            Stopwatch shownFor = new();
            FFCodecFrameInfo? shown = null;   // the frame on screen
            int framesSinceStart = 0;

            while (!token.IsCancellationRequested)
            {
                (var result, var info) = await DecodeStepAsync(backBuffer, token).ConfigureAwait(false);
                if (result == AVResult32.EndOfFile)
                {
                    if (looping && framesSinceStart > 1)
                    {
                        (await SeekStepAsync(TimeSpan.Zero, token).ConfigureAwait(false)).ThrowIfError();
                        framesSinceStart = 0;
                        continue;
                    }
                    await WaitAsync(shown, null, shownFor, token).ConfigureAwait(false); // let the last frame be shown for its duration
                    Raise(Finished, EventArgs.Empty);
                    return;
                }
                result.ThrowIfError();
                framesSinceStart++;

                // present the new frame when the current one has been shown long enough
                await WaitAsync(shown, info, shownFor, token).ConfigureAwait(false);
                (backBuffer, frontBuffer) = (frontBuffer, backBuffer);
                FrameInfo = info;
                shown = info;
                shownFor.Restart();

                EventHandler? handler = ReadyToRender;
                if (handler != null)
                {
                    handlerTask = Task.Run(() => handler(this, EventArgs.Empty));
                    try
                    {
                        await WhenCompletedOrCancelled(handlerTask, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* an exception in a handler does not stop the playback */ }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Raise(Faulted, ex);
        }
        finally
        {
            _ = Interlocked.CompareExchange(ref decodingState, (int)DecodingState.None, (int)DecodingState.Automatic);
        }
    }

    /// <summary>Waits until <paramref name="shown"/> has been displayed for its duration.</summary>
    private static Task WaitAsync(FFCodecFrameInfo? shown, FFCodecFrameInfo? next, Stopwatch shownFor, CancellationToken token)
    {
        if (shown is not FFCodecFrameInfo current)
            return Task.CompletedTask; // first frame: present immediately
        TimeSpan displayTime = current.Duration;
        if (displayTime <= TimeSpan.Zero && next is FFCodecFrameInfo following)
            displayTime = following.TimeStamp - current.TimeStamp;
        if (displayTime <= TimeSpan.Zero)
            displayTime = DefaultFrameDelay;
        TimeSpan wait = displayTime - shownFor.Elapsed;
        return wait > TimeSpan.Zero ? Task.Delay(wait, token) : Task.CompletedTask;
    }

    private static async Task WhenCompletedOrCancelled(Task task, CancellationToken token)
    {
        if (!task.IsCompleted)
        {
            TaskCompletionSource<bool> cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, cancelled.Task).ConfigureAwait(false) != task)
                    token.ThrowIfCancellationRequested();
            }
        }
        await task.ConfigureAwait(false);
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler != null)
            _ = Task.Run(() => { try { handler(this, args); } catch { } });
    }

    private void Raise(EventHandler? handler, EventArgs args)
    {
        if (handler != null)
            _ = Task.Run(() => { try { handler(this, args); } catch { } });
    }

    #endregion

    #region Events

    /// <summary>
    /// Occurs when a newly decoded frame is ready to be presented.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The event does not perform or require rendering. Consumers may use the event
    /// to present, copy, inspect, or otherwise process the current <see cref="Frame"/>.
    /// The event is raised on a thread-pool thread.
    /// </para>
    /// <para>
    /// During automatic playback, the decoding loop waits for all event handlers to
    /// complete before continuing with the next frame. This allows a consumer to
    /// provide back-pressure when presentation takes longer than decoding.
    /// </para>
    /// </remarks>
    public event EventHandler? ReadyToRender;

    /// <summary>
    /// Occurs when an exception terminates automatic decoding.
    /// </summary>
    public event EventHandler<Exception>? Faulted;

    /// <summary>
    /// Occurs when automatic playback reaches the end of the animation without looping.
    /// </summary>
    /// <remarks>
    /// This event is not raised when playback is stopped explicitly or cancelled.
    /// </remarks>
    public event EventHandler? Finished;

    #endregion

    #region State

    private static TaskCompletionSource<bool> CompletedOperation()
    {
        TaskCompletionSource<bool> tcs = new();
        tcs.SetResult(true);
        return tcs;
    }

    private readonly struct OperationScope(SKAnimatedImage image, TaskCompletionSource<bool> operation) : IDisposable
    {
        public void Dispose()
        {
            // Only Manual -> None: if the image was disposed meanwhile, the state stays Disposed.
            _ = Interlocked.CompareExchange(ref image.decodingState, (int)DecodingState.None, (int)DecodingState.Manual);
            operation.TrySetResult(true); // lets Dispose continue
        }
    }

    private OperationScope BeginManualOperation()
    {
        TaskCompletionSource<bool> operation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (stateLock) // Dispose reads the state and the operation together
        {
            BeginOperation(DecodingState.Manual);
            manualOperation = operation;
        }
        return new OperationScope(this, operation);
    }

    private void BeginOperation(DecodingState state)
    {
        int previous = Interlocked.CompareExchange(ref decodingState, (int)state, (int)DecodingState.None);
        if (previous == (int)DecodingState.Disposed)
            throw new ObjectDisposedException(nameof(SKAnimatedImage));
        if (previous != (int)DecodingState.None)
            throw new InvalidOperationException($"Another operation is active ({(DecodingState)previous}).");
    }

    private void ThrowIfDisposed()
    {
        if (State == DecodingState.Disposed)
            throw new ObjectDisposedException(nameof(SKAnimatedImage));
    }

    private void CheckAsync()
    {
        ThrowIfDisposed();
        if (workQueue is null)
        {
            throw new InvalidOperationException(
                "Asynchronous decoding requires an FFmpegWorkQueue.");
        }
    }

    #endregion

    #region Dispose

    /// <summary>Cancels running operations; returns a task that completes when they no longer use FFmpeg.</summary>
    private Task? BeginDispose()
    {
        Task operation;
        lock (stateLock)
        {
            if ((DecodingState)Interlocked.Exchange(ref decodingState, (int)DecodingState.Disposed) == DecodingState.Disposed)
                return null;
            operation = manualOperation.Task;
        }
        asyncOperation?.Cancel();
        cts?.Cancel();
        return Task.WhenAll(decodingTask, operation);
    }

    private void Release()
    {
        if (Interlocked.Exchange(ref released, 1) != 0)
            return;
        source.Dispose();
        frame.Dispose();
        converter.Dispose();
        cts?.Dispose();
        SKBitmap front = frontBuffer, back = backBuffer;
        // A ReadyToRender handler may still use Frame.
        _ = handlerTask.ContinueWith(_ =>
        {
            front.Dispose();
            back.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Releases all resources used by the animated image.
    /// </summary>
    /// <remarks>
    /// If automatic or asynchronous decoding is currently active, disposal cancels
    /// the operation and waits for it to finish before releasing the underlying
    /// FFmpeg resources. The bitmaps are released once a running <see cref="ReadyToRender"/> handler has returned,
    /// so Dispose may also be called from a handler.
    /// </remarks>
    public void Dispose()
    {
        Task? pending = BeginDispose();
        if (pending == null)
            return;
        try { pending.Wait(); } catch { /* reported by the operations themselves */ }
        Release();
    }

    /// <summary>
    /// Releases all resources used by the animated image.
    /// </summary>
    /// <remarks>
    /// If automatic or asynchronous decoding is currently active, disposal cancels
    /// the operation and waits for it to finish before releasing the underlying
    /// FFmpeg resources. The bitmaps are released once a running <see cref="ReadyToRender"/> handler has returned.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        Task? pending = BeginDispose();
        if (pending == null)
            return;
        try { await pending.ConfigureAwait(false); } catch { /* reported by the operations themselves */ }
        Release();
    }

    #endregion
}

/// <summary>
/// Specifies the current state of an animated image decoder.
/// </summary>
public enum DecodingState
{
    /// <summary>
    /// The decoder is idle and no decoding operation is currently active.
    /// </summary>
    None = 0,

    /// <summary>
    /// The decoder is performing a manually controlled decoding operation.
    /// </summary>
    /// <remarks>
    /// While in this state, another decoding or seeking operation cannot be started.
    /// The state is also used while an asynchronous decoding operation is pending or running.
    /// </remarks>
    Manual = 1,

    /// <summary>
    /// The decoder is performing automatic animation playback.
    /// </summary>
    /// <remarks>
    /// While in this state, the animation is decoded and presented automatically.
    /// The automatic playback can be stopped by the caller.
    /// </remarks>
    Automatic = 2,

    /// <summary>
    /// The <see cref="SKAnimatedImage"/> has been disposed and can no longer be used.
    /// </summary>
    Disposed = 3,
}
