using FFmpeg.Threading;
using FFmpeg.Utils;
using System.Diagnostics;

namespace FFmpeg.Skia;

/// <summary>
/// Provides asynchronous video playback for SkiaSharp applications.
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
    private static readonly LimitedConcurrencyLevelTaskScheduler scheduler = new(Environment.ProcessorCount - 1);
    private static readonly TaskFactory factory = new(default, TaskCreationOptions.LongRunning, TaskContinuationOptions.None, scheduler);

    /// <summary>
    /// Gets or sets the maximum number of decoding tasks that may execute concurrently.
    /// </summary>
    /// <remarks>
    /// This setting affects all <see cref="SKVideo"/> instances because they share
    /// a common task scheduler.
    /// </remarks>
    public static int MaxConcurrencyLevel { get => scheduler.MaximumConcurrencyLevel; set => scheduler.SetMaxDegreeOfParallelism(value); }

    private readonly FFCodec2Skia video = video ?? throw new ArgumentNullException(nameof(video));
    private CancellationTokenSource cts = new();
    private Task decodingTask = Task.CompletedTask;

    #region Properties
    /// <summary>
    /// Gets whether this instance has been disposed.
    /// </summary>
    public bool Disposed { get; private set; }

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
    /// <summary>
    /// Starts playback from the beginning of the video.
    /// </summary>
    /// <remarks>
    /// If playback is already running, this method has no effect.
    /// </remarks>
    public void Start()
    {
        if (Disposed)
            throw new ObjectDisposedException(nameof(SKVideo));
        if (!decodingTask.IsCompleted)
            return; // Already running
        lock (_lock)
        {
            if (!decodingTask.IsCompleted)
                return; // Already running
            cts?.Cancel();
            cts?.Dispose();
            cts = new CancellationTokenSource();
            _ = video.Restart();
            decodingTask = factory.StartNew(() => DecodingTask(cts.Token), cts.Token);
            Started?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Pauses playback.
    /// </summary>
    /// <remarks>
    /// Playback can later be resumed by calling <see cref="Resume"/>.
    /// </remarks>
    public void Pause()
    {
        if (Disposed)
            throw new ObjectDisposedException(nameof(SKVideo));
        if (decodingTask.IsCompleted)
            return; // Not running
        cts.Cancel();
        Paused?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Resumes playback after it has been paused.
    /// </summary>
    public void Resume()
    {
        if (Disposed)
            throw new ObjectDisposedException(nameof(SKVideo));
        if (!decodingTask.IsCompleted)
            return; // Already running
        lock (_lock)
        {
            if (!decodingTask.IsCompleted)
                return; // Already running
            cts?.Dispose();
            cts = new CancellationTokenSource();
            decodingTask = factory.StartNew(() => DecodingTask(cts.Token), cts.Token);
            Resumed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Stops playback.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="Pause"/>, playback cannot be resumed from the current
    /// position. Use <see cref="Start"/> or <see cref="Restart"/> to begin playback again.
    /// </remarks>
    public void Stop()
    {
        if (Disposed)
            throw new ObjectDisposedException(nameof(SKVideo));
        if (decodingTask.IsCompleted)
            return; // Not running
        lock (_lock)
        {
            if (decodingTask.IsCompleted)
                return; // Already stopped


            cts.Cancel();
            CancellationTokenSource source = cts;
            _ = decodingTask.ContinueWith(t =>
            {
                source?.Dispose();
                Stopped?.Invoke(this, EventArgs.Empty);
            });
            cts.Dispose();
        }
    }

    /// <summary>
    /// Restarts playback from the beginning of the video.
    /// </summary>
    public void Restart()
    {
        if (Disposed)
            throw new ObjectDisposedException(nameof(SKVideo));
        if (decodingTask.IsCompleted)
        {
            _ = video.Restart();
            Start();
        }
        else
            _ = video.Restart(); // Restart the video without starting a new task
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
    public AVResult32 Seek(TimeSpan timeSpan)
    {
        if (Disposed)
            throw new ObjectDisposedException(nameof(SKVideo));
        lock (_lock)
        {
            renewTimer = true; // Reset the timer for the new seek position
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
    public AVResult32 Seek(long frameIndex)
    {
        if (Disposed)
            throw new ObjectDisposedException(nameof(SKVideo));
        lock (_lock)
        {
            renewTimer = true; // Reset the timer for the new seek position
            return video.Seek(frameIndex);
        }
    }
    private bool renewTimer = false;
    private void DecodingTask(CancellationToken token)
    {
        renewTimer = true;
        TimeSpan firstFrame = TimeSpan.Zero;
        Stopwatch timer = Stopwatch.StartNew();
        Task eventTask = Task.CompletedTask;
        FFCodecFrameInfo frameInfo = default;

        while (!token.IsCancellationRequested)
        {
            lock (_lock)
            {

                if (backbuffer != null)
                {
                    if (!video.NextImage(backbuffer, out frameInfo))
                    {
                        backbuffer.Dispose();
                        backbuffer = null;
                    }
                }
                else
                {
                    backbuffer = video.NextImage(out frameInfo);
                }

                if (backbuffer == null)
                {
                    Ended?.Invoke(this, EventArgs.Empty);
                    break;
                }
                if (renewTimer)
                {
                    firstFrame = frameInfo.TimeStamp;
                    timer.Restart();
                    renewTimer = false;
                }
            }

            TimeSpan timeToSleep = frameInfo.TimeStamp - timer.Elapsed - firstFrame;

            if (timeToSleep > TimeSpan.Zero)
                Thread.Sleep(timeToSleep);

            if (FrameReadyToRender != null && eventTask.IsCompleted)
            {
                SKBitmap f = backbuffer; // Capture the current frame
                FFCodecFrameInfo info = frameInfo; // Capture frameInfo
                eventTask = Task.Run(() =>
                               {
                                   FrameReadyToRender(this, (f, info));
                               }, token);

                (frame, backbuffer) = (backbuffer, frame); // Swap buffers
            }
            CurrentFrameInfo = frameInfo;
        }
    }



    #endregion

    #region Events
    /// <summary>
    /// Occurs when playback reaches the end of the video.
    /// </summary>
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
    /// Occurs when the next video frame is ready to be rendered.
    /// </summary>
    /// <remarks>
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
    /// The media could not be opened, no suitable video stream was found, or
    /// <paramref name="info"/> specifies an unsupported <see cref="SKColorType"/>.
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
    /// The media could not be opened, no suitable video stream was found, or
    /// <paramref name="info"/> specifies an unsupported <see cref="SKColorType"/>.
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
    /// When disposing, playback is stopped and any internally allocated frame buffers
    /// are released. Once disposed, the instance cannot be used again.
    /// </remarks>
    protected virtual void Dispose(bool disposing)
    {
        if (!Disposed)
        {
            if (disposing)
            {
                cts.Cancel();
                cts.Dispose();
                backbuffer?.Dispose();
                frame?.Dispose();
                bool entered = Monitor.TryEnter(_lock, 1000);
                video.Dispose();
                if (entered)
                    Monitor.Exit(_lock);
            }
            Disposed = true;
        }
    }

    /// <summary>
    /// Releases all resources used by the current <see cref="SKVideo"/>.
    /// </summary>
    public void Dispose()
    {
        // Ändern Sie diesen Code nicht. Fügen Sie Bereinigungscode in der Methode "Dispose(bool disposing)" ein.
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    #endregion

}
