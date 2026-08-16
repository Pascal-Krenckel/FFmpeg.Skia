using FFmpeg.Formats;
using FFmpeg.Helper.Exceptions;
using FFmpeg.Threading;
using FFmpeg.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Timers;

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
    readonly MediaSource source;
    readonly AVFrame frame = AVFrame.Allocate();
    readonly int videoStreamIndex;
    SKBitmap frontBuffer, backBuffer;
    readonly FFmpeg.Threading.FFmpegWorkQueue? workQueue;
    CancellationTokenSource cts;
    CancellableWorkItem? asyncOperation;
    Task decodingTask = Task.CompletedTask;
    int decodingState = 0;

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
        IndexOutOfRangeException.ThrowIfOutOfRange(videoStreamIndex, source.Streams.Count, nameof(videoStreamIndex));
        foreach (var stream in source.Streams)
            stream.Discard = Formats.DiscardFlags.All;
        source.Streams[videoStreamIndex].Discard = Formats.DiscardFlags.Default;
        cts = new();
        this.source = source;
        this.videoStreamIndex = videoStreamIndex;
        this.workQueue = workQueue;
        frontBuffer = new SKBitmap();
        backBuffer = new SKBitmap();
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
    public static SKAnimatedImage Create(Stream stream, int videoIndex = -1, FFmpegWorkQueue? workQueue = null, InputFormat? format = null, IDictionary<string, string>? options = null, HW.DeviceType hwDevice = HW.DeviceType.None)
    {
        MediaSource source = MediaSource.Open(stream, format, options, hwDevice);
        return new(source, videoIndex, workQueue);
    }

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
    public static SKAnimatedImage Create(string url, int videoIndex = -1, FFmpegWorkQueue? workQueue = null, InputFormat? format = null, IDictionary<string, string>? options = null, HW.DeviceType hwDevice = HW.DeviceType.None)
    {
        MediaSource source = MediaSource.Open(url, format, options, hwDevice);
        return new(source, videoIndex, workQueue);
    }

    private AVResult32 DecodeStepInternally(SKBitmap target, out FFCodecFrameInfo info)
    {
        info = default;
        AVResult32 result = source.ReadAndDecodeAVFrame(frame);
        if (result.IsError)
            return result;
        frame.CopyTo(target);
        info = new()
        {
            TimeStamp = frame.GetPresentationTimestamp() * frame.TimeBase,
            Duration = frame.Duration * frame.TimeBase,
        };
        return 0;
    }

    private async Task<(AVResult32, FFCodecFrameInfo)> DecodeStepInternallyAsync(SKBitmap target, CancellationToken token)
    {
        AVResult32 result = await (asyncOperation = workQueue!.QueueFFmpegWorkItem(() => source.ReadAndDecodeAVFrame(frame), token)).ConfigureAwait(false);
        if (result.IsError)
            return (result, default);
        frame.CopyTo(target);
        return (0, new()
        {
            TimeStamp = frame.GetPresentationTimestamp() * frame.TimeBase,
            Duration = frame.Duration * frame.TimeBase,
        });
    }

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
        ObjectDisposedException.ThrowIfTrue(State == DecodingState.Disposed, this);
        using (BeginOperation(DecodingState.Manual))
        {

            AVResult32 result = DecodeStepInternally(backBuffer, out var frameInfo);
            if (result == AVResult32.EndOfFile)
                return false;
            result.ThrowIfError();
            (frontBuffer, backBuffer) = (backBuffer, frontBuffer);
            FrameInfo = frameInfo;
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
        ObjectDisposedException.ThrowIfTrue(State == DecodingState.Disposed, this);
        CheckAsync();
        using (BeginOperation(DecodingState.Manual))
        {
            (var result, var info) = await DecodeStepInternallyAsync(backBuffer, token).ConfigureAwait(false);
            if (result == AVResult32.EndOfFile)
                return false;
            result.ThrowIfError();
            (backBuffer, frontBuffer) = (frontBuffer, backBuffer);
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
        ObjectDisposedException.ThrowIfTrue(State == DecodingState.Disposed, this);
        using (BeginOperation(DecodingState.Manual))
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
        ObjectDisposedException.ThrowIfTrue(State == DecodingState.Disposed, this);
        CheckAsync();
        using (BeginOperation(DecodingState.Manual))
        {
            var result = await (asyncOperation = workQueue!.QueueFFmpegWorkItem(() => source.SeekExactly(time, videoStreamIndex), token)).ConfigureAwait(false);
            if (!result.IsError)
                FrameInfo = default;
            return result;
        }
    }

    /// <summary>
    /// Starts automatic decoding and presentation of the animation.
    /// </summary>
    /// <param name="looping">
    /// <see langword="true"/> to restart the animation when its end is reached;
    /// otherwise, playback stops when the end is reached.
    /// </param>
    /// <remarks>
    /// <para>
    /// Automatic decoding runs independently of the calling thread.
    /// </para>
    /// <para>
    /// A <see cref="ReadyToRender"/> event is raised whenever a new frame is ready
    /// to be presented. The decoding loop waits for the event handlers to complete
    /// before decoding the next frame, allowing presentation to provide back-pressure
    /// when necessary.
    /// </para>
    /// <para>
    /// Automatic decoding and manual decoding or seeking are mutually exclusive.
    /// </para>
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// The image has been disposed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Another decoding or seeking operation is currently active, or automatic
    /// playback is already running.
    /// </exception>
    public void Start(bool looping)
    {
        ObjectDisposedException.ThrowIfTrue(State == DecodingState.Disposed, this);
        BeginOperation(DecodingState.Automatic, DecodingState.None);
        cts.Dispose();
        cts = new();

        if (workQueue != null)
            decodingTask = decodingTask = Task.Factory.StartNew(async () => await DecodingLoopAsync(looping, cts.Token).ConfigureAwait(false),
                                                                    CancellationToken.None,
                                                                    TaskCreationOptions.LongRunning,
                                                                    TaskScheduler.Default).Unwrap();
        else
            decodingTask = Task.Factory.StartNew(() => DecodingLoopSync(looping, cts.Token), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

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
    /// Automatic playback is not currently running.
    /// </exception>
    public Task Stop()
    {
        ObjectDisposedException.ThrowIfTrue(State == DecodingState.Disposed, this);
        if (decodingTask.IsCompleted && State == DecodingState.None)
            return decodingTask; // if task completed exit without throwing
        BeginOperation(DecodingState.None, DecodingState.Automatic); // throw if no decoding task is currently running
        cts.Cancel();
        cts.Dispose();
        return decodingTask;
    }
    private void DecodingLoopSync(bool looping, CancellationToken token)
    {
        if (State == DecodingState.Disposed)
            return;
        try
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!token.IsCancellationRequested)
            {
                var result = DecodeStepInternally(backBuffer, out var info);
                if (result == AVResult32.EndOfFile)
                    if (looping)
                    {
                        source.SeekExactly(TimeSpan.Zero, videoStreamIndex).ThrowIfError();
                        FrameInfo = default;
                        timer.Restart();
                        continue;
                    }
                    else
                    {
                        _ = Finished?.InvokeAsync(this, EventArgs.Empty);
                        return;
                    }
                result.ThrowIfError();
                Sleep(info.Duration - timer.Elapsed);
                (backBuffer, frontBuffer) = (frontBuffer, backBuffer);
                try
                {
                    ReadyToRender?.InvokeAsync(this, EventArgs.Empty).Wait(token);
                }
                catch { }
                timer.Restart();
            }
        }
        catch (OperationCanceledException)
        {

        }
        catch (Exception ex)
        {
            _ = Faulted?.InvokeAsync(this, ex);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task DecodingLoopAsync(bool looping, CancellationToken token)
    {
        if (State == DecodingState.Disposed)
            return;
        try
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (!token.IsCancellationRequested)
            {
                (var result, var info) = await DecodeStepInternallyAsync(backBuffer, token).ConfigureAwait(false);
                if (result == AVResult32.EndOfFile)
                    if (looping)
                    {
                        result = await workQueue!.QueueFFmpegWorkItem(() => source.SeekExactly(TimeSpan.Zero, videoStreamIndex), token).ConfigureAwait(false);
                        result.ThrowIfError();
                        FrameInfo = default;
                        timer.Restart();
                        continue;
                    }
                    else
                    {
                        _ = Finished?.InvokeAsync(this, EventArgs.Empty);
                        return;
                    }

                result.ThrowIfError();
                await SleepAsync(info.Duration - timer.Elapsed, token).ConfigureAwait(false);
                (backBuffer, frontBuffer) = (frontBuffer, backBuffer);
                try
                {
                    var awaitable = ReadyToRender?.InvokeAsync(this, EventArgs.Empty, token).ConfigureAwait(false);
                    if (awaitable != null)
                        await awaitable.Value;
                }
                catch (OperationCanceledException) { throw; }
                catch { }
                timer.Restart();
            }
        }
        catch (OperationCanceledException)
        {

        }
        catch (Exception ex)
        {
            _ = Faulted?.InvokeAsync(this, ex);
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>
    /// Occurs when a newly decoded frame is ready to be presented.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The event does not perform or require rendering. Consumers may use the event
    /// to present, copy, inspect, or otherwise process the current <see cref="Frame"/>.
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

    private void Sleep(TimeSpan timeSpan)
    {
        if (timeSpan > TimeSpan.Zero)
            Thread.Sleep(timeSpan);
    }

    private Task SleepAsync(TimeSpan timeSpan, CancellationToken token)
    {
        if (timeSpan > TimeSpan.Zero)
            return Task.Delay(timeSpan, token);
        return Task.CompletedTask;
    }


    private readonly struct OperationLock(SKAnimatedImage image) : IDisposable
    {
        public readonly void Dispose() => Volatile.Write(ref image.decodingState, (int)DecodingState.None);
    }
    private OperationLock BeginOperation(DecodingState state)
    {
        if (Interlocked.CompareExchange(ref decodingState, (int)state, (int)DecodingState.None) != (int)DecodingState.None)
            throw new InvalidOperationException("");
        return new OperationLock(this);
    }

    private void BeginOperation(DecodingState state, DecodingState allowed)
    {
        if (Interlocked.CompareExchange(ref decodingState, (int)state, (int)allowed) != (int)allowed)
            throw new InvalidOperationException("");
    }

    private void EndOperation()
    {
        var state = Volatile.Read(ref decodingState);
        if (state == (int)DecodingState.Disposed)
            return;
        _ = Interlocked.CompareExchange(ref decodingState, (int)DecodingState.None, state);

    }

    private void CheckAsync()
    {
        if (workQueue is null)
        {
            throw new InvalidOperationException(
                "Asynchronous decoding requires an FFmpegWorkQueue.");
        }
    }

    /// <summary>
    /// Releases all resources used by the animated image.
    /// </summary>
    /// <remarks>
    /// If automatic or asynchronous decoding is currently active, disposal cancels
    /// the operation and waits for it to finish before releasing the underlying
    /// FFmpeg and SkiaSharp resources.
    /// </remarks>
    public void Dispose()
    {
        var oldValue = (DecodingState)Interlocked.Exchange(ref decodingState, (int)DecodingState.Disposed);
        if (oldValue == DecodingState.Disposed)
            return;
        if (oldValue == DecodingState.Manual)
        {
            asyncOperation?.Cancel(); // cancel the last asyncOperation
            try
            {
                asyncOperation?.Task.Wait(); // wait for the task to finish, should return immediatly if not currently running.
            }
            catch { }
        }
        cts.Cancel();
        decodingTask.Wait();
        cts.Dispose();
        source.Dispose();
        frontBuffer.Dispose();
        backBuffer.Dispose();
    }

    /// <summary>
    /// Releases all resources used by the animated image.
    /// </summary>
    /// <remarks>
    /// If automatic or asynchronous decoding is currently active, disposal cancels
    /// the operation and waits for it to finish before releasing the underlying
    /// FFmpeg and SkiaSharp resources.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        var oldValue = (DecodingState)Interlocked.Exchange(ref decodingState, (int)DecodingState.Disposed);
        if (oldValue == DecodingState.Disposed)
            return;
        if (oldValue == DecodingState.Manual)
        {
            asyncOperation?.Cancel(); // cancel the last asyncOperation
            try
            {
                var task = asyncOperation?.Task;
                if (task != null)
                    await task.ConfigureAwait(false);// wait for the task to finish, should return immediatly if not currently running.
            }
            catch { }
        }
        cts.Cancel();
        await decodingTask.ConfigureAwait(false);
        cts.Dispose();
        source.Dispose();
        frontBuffer.Dispose();

    }
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