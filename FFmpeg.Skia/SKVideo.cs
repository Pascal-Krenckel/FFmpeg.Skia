using FFmpeg.Utils;
using System.Diagnostics;

namespace FFmpeg.Skia;

/// <summary>
/// Provides asynchronous video playback for SkiaSharp applications. This class has no audio support. Use <see cref="FFmpeg.MediaPlayer.PlaybackEngine"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SKVideo"/> decodes video frames using <see cref="FFCodec2Skia"/>
/// on a background thread and raises <see cref="FrameReadyToRender"/> whenever
/// a new frame should be displayed.
/// </para>
/// <para>
/// Decoded frames are reused internally to minimize memory allocations.
/// Applications must not dispose or retain the <see cref="SKBitmap"/> instances
/// provided by <see cref="FrameReadyToRender"/>, as ownership remains with
/// the <see cref="SKVideo"/> instance.
/// </para>
/// <para>
/// This class performs video decoding only. It does not decode audio or provide
/// audio/video synchronization.
/// </para>
/// </remarks>
public class SKVideo(FFCodec2Skia video) : IDisposable
{
    private SKBitmap? backbuffer = null;
    private SKBitmap? frame = null;

    private readonly object _lock = new();
    private static readonly TaskFactory factory = new(default, TaskCreationOptions.LongRunning, TaskContinuationOptions.None, TaskScheduler.Default);


    private readonly FFCodec2Skia video = video ?? throw new ArgumentNullException(nameof(video));
    private CancellationTokenSource cts = new();
    private Task decodingTask = Task.CompletedTask;
    private Task eventTask = Task.CompletedTask;

    /// <summary>Incremented by every seek; lets the decoding loop notice a seek made in an <see cref="Ended"/> handler.</summary>
    private int seekVersion;
    private bool renewTimer = false;
    private int disposed;

    #region Properties
    /// <summary>
    /// Gets whether this instance has been disposed.
    /// </summary>
    public bool Disposed => Volatile.Read(ref disposed) != 0;

    /// <summary>
    /// Gets the duration of the video.
    /// </summary>
    public TimeSpan Duration => video.Duration;

    /// <summary>
    /// Gets the image information of decoded video frames.
    /// </summary>
    public SKImageInfo Info => video.Info;

    /// <summary>
    /// Gets the total number of frames in the video stream.
    /// </summary>
    public long Frames => video.Frames;

    /// <summary>
    /// Gets the nominal frame rate of the video.
    /// </summary>
    public Rational FrameRate => video.FrameRate;

    /// <summary>
    /// Gets whether the decoding thread is currently running.
    /// </summary>
    public bool Running => !decodingTask.IsCompleted;

    /// <summary>
    /// Gets timing information for the most recently decoded frame.
    /// </summary>
    public FFCodecFrameInfo CurrentFrameInfo { get; private set; }
    #endregion

    #region Methods
    private void ThrowIfDisposed()
    {
        if (Disposed)
            throw new ObjectDisposedException(nameof(SKVideo));
    }

    /// <summary>
    /// Starts playback from the beginning of the video.
    /// </summary>
    /// <remarks>
    /// If playback is already running, this method has no effect.
    /// </remarks>
    public void Start()
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            if (!decodingTask.IsCompleted)
                return; // Already running
            _ = video.Restart();
            seekVersion++;
            StartDecoding();
        }
        Started?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Pauses playback.
    /// </summary>
    /// <remarks>
    /// Playback can later be resumed by calling <see cref="Resume"/>.
    /// </remarks>
    public void Pause()
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            if (decodingTask.IsCompleted)
                return; // Not running
            cts.Cancel();
        }
        Paused?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Resumes playback after it has been paused.
    /// </summary>
    public void Resume()
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            if (!decodingTask.IsCompleted)
                return; // Already running
            StartDecoding();
        }
        Resumed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts the decoding loop; called under the lock while no loop is running.</summary>
    private void StartDecoding()
    {
        cts.Dispose(); // the previous loop has completed
        cts = new CancellationTokenSource();
        CancellationToken token = cts.Token;
        decodingTask = factory.StartNew(() => DecodingLoop(token), token);
    }

    /// <summary>
    /// Stops playback.
    /// </summary>
    /// <remarks>
    /// <see cref="Stopped"/> is raised once the decoding thread has finished.
    /// Use <see cref="Start"/> or <see cref="Restart"/> to begin playback again.
    /// </remarks>
    public void Stop()
    {
        ThrowIfDisposed();
        Task task;
        lock (_lock)
        {
            if (decodingTask.IsCompleted)
                return; // Not running
            cts.Cancel();
            task = decodingTask;
        }
        _ = task.ContinueWith(_ => Stopped?.Invoke(this, EventArgs.Empty), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>
    /// Restarts playback from the beginning of the video.
    /// </summary>
    /// <remarks>
    /// If playback is running, it continues from the beginning; otherwise it is started.
    /// </remarks>
    public void Restart()
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            if (!decodingTask.IsCompleted)
            {
                _ = video.Restart();
                renewTimer = true;
                seekVersion++;
                return;
            }
        }
        Start();
    }

    /// <summary>
    /// Seeks to the specified playback position.
    /// </summary>
    /// <param name="timeSpan">
    /// The position to seek to.
    /// </param>
    /// <returns>
    /// An <see cref="AVResult32"/> indicating whether the seek operation succeeded.
    /// </returns>
    /// <remarks>
    /// Seeking from an <see cref="Ended"/> handler continues playback from the new position.
    /// </remarks>
    public AVResult32 Seek(TimeSpan timeSpan)
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            renewTimer = true; // Reset the timer for the new seek position
            seekVersion++;
            return video.Seek(timeSpan);
        }
    }

    /// <summary>
    /// Seeks to the specified frame number.
    /// </summary>
    /// <param name="frameIndex">
    /// The zero-based frame index.
    /// </param>
    /// <returns>
    /// An <see cref="AVResult32"/> indicating whether the seek operation succeeded.
    /// </returns>
    /// <remarks>
    /// See <see cref="FFCodec2Skia.Seek(long)"/>. Seeking from an <see cref="Ended"/> handler continues playback
    /// from the new position.
    /// </remarks>
    public AVResult32 Seek(long frameIndex)
    {
        ThrowIfDisposed();
        lock (_lock)
        {
            renewTimer = true; // Reset the timer for the new seek position
            seekVersion++;
            return video.Seek(frameIndex);
        }
    }

    private void DecodingLoop(CancellationToken token)
    {
        lock (_lock)
            renewTimer = true;
        TimeSpan firstFrame = TimeSpan.Zero;
        Stopwatch timer = Stopwatch.StartNew();

        try
        {
            while (!token.IsCancellationRequested)
            {
                FFCodecFrameInfo frameInfo;
                bool decoded;
                int version;
                lock (_lock)
                {
                    if (token.IsCancellationRequested)
                        break;
                    backbuffer ??= new SKBitmap();
                    decoded = video.NextImage(backbuffer, out frameInfo);
                    version = seekVersion;
                    if (decoded && renewTimer)
                    {
                        firstFrame = frameInfo.TimeStamp;
                        timer.Restart();
                        renewTimer = false;
                    }
                }

                if (!decoded)
                {
                    // Raised on another thread so that a handler can block (e.g. Dispatcher.Invoke) without
                    // dead-locking Dispose, which cancels the wait.
                    EventHandler? ended = Ended;
                    if (ended != null)
                    {
                        try
                        {
                            Task.Run(() => ended(this, EventArgs.Empty)).Wait(token);
                        }
                        catch (AggregateException) { /* an exception in a handler does not stop the player */ }
                    }
                    lock (_lock)
                    {
                        if (seekVersion != version && !token.IsCancellationRequested)
                            continue; // the handler seeked (e.g. to loop the video)
                    }
                    break;
                }

                TimeSpan timeToSleep = frameInfo.TimeStamp - firstFrame - timer.Elapsed;
                if (timeToSleep > TimeSpan.Zero && token.WaitHandle.WaitOne(timeToSleep))
                    break; // cancelled while waiting

                EventHandler<(SKBitmap frame, FFCodecFrameInfo frameInfo)>? handler = FrameReadyToRender;
                if (handler != null && eventTask.IsCompleted)
                {
                    SKBitmap f = backbuffer!; // Capture the current frame
                    FFCodecFrameInfo info = frameInfo; // Capture frameInfo
                    eventTask = Task.Run(() =>
                    {
                        try { handler(this, (f, info)); }
                        catch { /* an exception in a render handler does not stop the player */ }
                    });
                    (frame, backbuffer) = (backbuffer, frame); // Swap buffers; the handler's bitmap is not decoded into until it returns
                }
                CurrentFrameInfo = frameInfo;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            EventHandler<Exception>? faulted = Faulted;
            if (faulted != null)
                _ = Task.Run(() => faulted(this, ex));
        }
    }



    #endregion

    #region Events
    /// <summary>
    /// Occurs when playback reaches the end of the video.
    /// </summary>
    /// <remarks>
    /// The event is raised on a thread-pool thread. If a handler seeks (<see cref="Seek(TimeSpan)"/>,
    /// <see cref="Seek(long)"/> or <see cref="Restart"/>), playback continues from the new position, e.g. to loop.
    /// </remarks>
    public event EventHandler? Ended;
    /// <summary>
    /// Occurs when playback starts.
    /// </summary>
    public event EventHandler? Started;
    /// <summary>
    /// Occurs when playback is paused.
    /// </summary>
    public event EventHandler? Paused;
    /// <summary>
    /// Occurs when playback resumes after being paused.
    /// </summary>
    public event EventHandler? Resumed;
    /// <summary>
    /// Occurs when playback stops.
    /// </summary>
    public event EventHandler? Stopped;
    /// <summary>
    /// Occurs when decoding fails. Playback stops.
    /// </summary>
    public event EventHandler<Exception>? Faulted;

    /// <summary>
    /// Occurs when the next video frame is ready to be rendered.
    /// </summary>
    /// <remarks>
    /// The event is raised on a thread-pool thread. While a handler runs, newer frames are skipped.
    /// The supplied <see cref="SKBitmap"/> is owned and reused by the
    /// <see cref="SKVideo"/> instance. Event handlers must not dispose it or
    /// retain a reference after the event returns. Clone the bitmap if it
    /// needs to be stored.
    /// </remarks>
    public event EventHandler<(SKBitmap frame, FFCodecFrameInfo frameInfo)>? FrameReadyToRender;
    #endregion

    #region Constructors
    /// <inheritdoc cref="SKVideo(Stream, SKImageInfo, IDictionary{string, string})"/>
    public SKVideo(Stream stream) : this(FFCodec2Skia.Create(stream)!) { }

    /// <inheritdoc cref="SKVideo(Stream, SKImageInfo, IDictionary{string, string})"/>
    public SKVideo(Stream stream, HW.DeviceType deviceType) : this(FFCodec2Skia.Create(stream, deviceType)!) { }

    /// <inheritdoc cref="SKVideo(Stream, SKImageInfo, IDictionary{string, string})"/>
    public SKVideo(Stream stream, SKImageInfo info) : this(FFCodec2Skia.Create(stream, info)!) { }

    /// <summary>
    /// Initializes a new <see cref="SKVideo"/> instance that decodes video frames from the specified
    /// <see cref="Stream"/> using the requested output image format and codec options.
    /// </summary>
    /// <param name="stream">
    /// The input stream containing the media to decode.
    /// </param>
    /// <param name="info">
    /// The desired output image information. The decoded frames are converted to the specified
    /// dimensions and <see cref="SKColorType"/> when necessary.
    /// </param>
    /// <param name="codecOptions">
    /// A collection of codec and format options passed to FFmpeg when opening the media source.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> or <paramref name="codecOptions"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// No suitable video stream was found, or
    /// <paramref name="info"/> specifies an unsupported <see cref="SKColorType"/>.
    /// </exception>
    /// <exception cref="Exceptions.FFmpegException">
    /// The media could not be opened.
    /// </exception>
    public SKVideo(Stream stream, SKImageInfo info, IDictionary<string, string> codecOptions)
        : this(FFCodec2Skia.Create(stream, info, codecOptions)!) { }

    /// <inheritdoc cref="SKVideo(IO.IOContext, SKImageInfo, IDictionary{string, string})"/>
    public SKVideo(IO.IOContext io) : this(FFCodec2Skia.Create(io)!) { }

    /// <inheritdoc cref="SKVideo(IO.IOContext, SKImageInfo, IDictionary{string, string})"/>
    public SKVideo(IO.IOContext io, SKImageInfo info)
        : this(FFCodec2Skia.Create(io, info)!) { }

    /// <inheritdoc cref="SKVideo(IO.IOContext, SKImageInfo, IDictionary{string, string})"/>
    public SKVideo(IO.IOContext io, IDictionary<string, string> codecOptions)
        : this(FFCodec2Skia.Create(io, codecOptions)!) { }

    /// <summary>
    /// Initializes a new <see cref="SKVideo"/> instance that decodes video frames from the specified
    /// <see cref="IO.IOContext"/> using the requested output image format and codec options.
    /// </summary>
    /// <param name="io">
    /// The custom FFmpeg I/O context used to read the media.
    /// </param>
    /// <param name="info">
    /// The desired output image information. The decoded frames are converted to the specified
    /// dimensions and <see cref="SKColorType"/> when necessary.
    /// </param>
    /// <param name="codecOptions">
    /// A collection of codec and format options passed to FFmpeg when opening the media source.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="io"/> or <paramref name="codecOptions"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// No suitable video stream was found, or
    /// <paramref name="info"/> specifies an unsupported <see cref="SKColorType"/>.
    /// </exception>
    /// <exception cref="Exceptions.FFmpegException">
    /// The media could not be opened.
    /// </exception>
    public SKVideo(IO.IOContext io, SKImageInfo info, IDictionary<string, string> codecOptions)
        : this(FFCodec2Skia.Create(io, info, codecOptions)!) { }

    /// <inheritdoc cref="SKVideo(string, SKImageInfo, IDictionary{string, string})"/>
    public SKVideo(string url) : this(FFCodec2Skia.Create(url)!) { }

    /// <inheritdoc cref="SKVideo(string, SKImageInfo, IDictionary{string, string})"/>
    public SKVideo(string url, HW.DeviceType deviceType) : this(FFCodec2Skia.Create(url, deviceType)!) { }

    /// <inheritdoc cref="SKVideo(string, SKImageInfo, IDictionary{string, string})"/>
    public SKVideo(string url, SKImageInfo info) : this(FFCodec2Skia.Create(url, info)!) { }

    /// <summary>
    /// Opens a video from the specified path or URL using the specified decoding options.
    /// </summary>
    /// <param name="url">
    /// The path or URL of the video.
    /// </param>
    /// <param name="info">
    /// Specifies the dimensions and color type of decoded frames.
    /// </param>
    /// <param name="codecOptions">
    /// A collection of codec and format options passed to FFmpeg.
    /// </param>
    public SKVideo(string url, SKImageInfo info, IDictionary<string, string> codecOptions) : this(FFCodec2Skia.Create(url, info, codecOptions)!) { }

    #endregion

    #region IDisposable
    /// <summary>
    /// Releases the resources used by the <see cref="SKVideo"/> instance.
    /// </summary>
    /// <param name="disposing">
    /// <see langword="true"/> to release managed resources; otherwise, <see langword="false"/>.
    /// </param>
    /// <remarks>
    /// When disposing, playback is stopped and the decoder is released once the decoding thread has finished.
    /// The frame buffers are released once a running <see cref="FrameReadyToRender"/> handler has returned.
    /// Dispose may be called from any thread, including event handlers.
    /// </remarks>
    protected virtual void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        if (!disposing)
            return;

        Task worker;
        lock (_lock)
        {
            cts.Cancel();
            worker = decodingTask;
        }

        if (Task.CurrentId == worker.Id)
            _ = worker.ContinueWith(_ => ReleaseResources(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        else
        {
            try { worker.Wait(); } catch { /* the loop reports its own errors */ }
            ReleaseResources();
        }
    }

    private void ReleaseResources()
    {
        lock (_lock)
        {
            video.Dispose();
            cts.Dispose();
        }
        SKBitmap? back = backbuffer, front = frame;
        backbuffer = frame = null;
        // A FrameReadyToRender handler may still be drawing one of the buffers.
        _ = eventTask.ContinueWith(_ =>
        {
            back?.Dispose();
            front?.Dispose();
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Releases all resources used by the current <see cref="SKVideo"/>.
    /// </summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    #endregion

}
