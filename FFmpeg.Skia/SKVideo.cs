using FFmpeg.Threading;
using FFmpeg.Utils;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace FFmpeg.Skia
{
    public class SKVideo(FFCodec2Skia video) : IDisposable
    {
        readonly object _lock = new object();
        private readonly static LimitedConcurrencyLevelTaskScheduler scheduler = new Threading.LimitedConcurrencyLevelTaskScheduler(Environment.ProcessorCount - 1);
        private readonly static TaskFactory factory = new(default, TaskCreationOptions.LongRunning, TaskContinuationOptions.None, scheduler);
        public static int MaxConcurrencyLevel { get => scheduler.MaximumConcurrencyLevel; set => scheduler.SetMaxDegreeOfParallelism(value); }

        private readonly FFCodec2Skia video = video ?? throw new ArgumentNullException(nameof(video));
        private bool disposedValue;
        private CancellationTokenSource cts = new();
        private Task decodingTask = Task.CompletedTask;
        
        #region Properties
        public bool Disposed => disposedValue;
        public TimeSpan Duration => video.Duration;
        public SKImageInfo Info => video.Info;
        public long Frames => video.Frames;
        public Rational FrameRate => video.FrameRate;

        public FFCodecFrameInfo CurrentFrameInfo { get; private set; } = default;
        #endregion

        #region Methods

        public void Start()
        {
            if (disposedValue)
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

        public void Pause()
        {
            if (disposedValue)
                throw new ObjectDisposedException(nameof(SKVideo));
            if (decodingTask.IsCompleted)
                return; // Not running
            cts.Cancel();
            Paused?.Invoke(this, EventArgs.Empty);
        }

        public void Resume()
        {
            if (disposedValue)
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

        public void Stop()
        {
            if (disposedValue)
                throw new ObjectDisposedException(nameof(SKVideo));
            if (decodingTask.IsCompleted)
                return; // Not running
            lock (_lock)
            {
                if (!decodingTask.IsCompleted)
                    return; // Already running


            cts.Cancel();
            var source = cts;
            decodingTask.ContinueWith(t =>
            {
                source?.Dispose();
                Stopped?.Invoke(this, EventArgs.Empty);
            });
            cts.Dispose();
        }
        }

        public void Restart()
        {
            if (disposedValue)
                throw new ObjectDisposedException(nameof(SKVideo));
            if (decodingTask.IsCompleted)
            {
                _ = video.Restart();
                Start();
            }
            else
                _ = video.Restart(); // Restart the video without starting a new task
        }

        public AVResult32 Seek(TimeSpan timeSpan)
        {
            if (disposedValue)
                throw new ObjectDisposedException(nameof(SKVideo));
            lock (_lock)
            {
            renewTimer = true; // Reset the timer for the new seek position
            return video.Seek(timeSpan);
        }
        }

        public AVResult32 Seek(long frameIndex)
        {
            if (disposedValue)
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
            SKBitmap? backbuffer = null;
            SKBitmap? frame = null;
            Task eventTask = Task.CompletedTask;
            FFCodecFrameInfo frameInfo = default;
            try
            {
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

                    var timeToSleep = frameInfo.TimeStamp - timer.Elapsed - firstFrame;

                    if (timeToSleep > TimeSpan.Zero)
                        Thread.Sleep(timeToSleep);

                    if (FrameReadyToRender != null && eventTask.IsCompleted)
                    {
                        var f = backbuffer; // Capture the current frame
                        var info = frameInfo; // Capture frameInfo
                        eventTask = Task.Run(() =>
                                       {                                        
                                           FrameReadyToRender(this, (f, info));
                                       }, token);
                        
                        (frame, backbuffer) = (backbuffer, frame); // Swap buffers
                    }
                    CurrentFrameInfo = frameInfo;
                }
            }
            finally
            {
                backbuffer?.Dispose();
                frame?.Dispose();
            }
        }


        #endregion

        #region Events
        public event EventHandler? Ended;
        public event EventHandler? Started;
        public event EventHandler? Paused;
        public event EventHandler? Resumed;
        public event EventHandler? Stopped;

        /// <summary>
        /// Will be raised when the next frame should be drawn. DO NOT dispose frame!
        /// </summary>
        public event EventHandler<(SKBitmap frame, FFCodecFrameInfo frameInfo)>? FrameReadyToRender;
        #endregion

        #region Constructors
        public SKVideo(Stream stream) : this(FFCodec2Skia.Create(stream)!) { }
        public SKVideo(Stream stream, HW.DeviceType deviceType) : this(FFCodec2Skia.Create(stream, deviceType)!) { }
        public SKVideo(string url) : this(FFCodec2Skia.Create(url)!) { }
        public SKVideo(string url, HW.DeviceType deviceType) : this(FFCodec2Skia.Create(url, deviceType)!) { }

        // Constructor for Stream and SKImageInfo
        public SKVideo(Stream stream, SKImageInfo info) : this(FFCodec2Skia.Create(stream, info)!) { }

        // Constructor for Stream, SKImageInfo, and codecOptions
        public SKVideo(Stream stream, SKImageInfo info, IDictionary<string, string> codecOptions)
            : this(FFCodec2Skia.Create(stream, info, codecOptions)!) { }

        // Constructor for Stream and IOContext
        public SKVideo(IO.IOContext io) : this(FFCodec2Skia.Create(io)!) { }

        // Constructor for Stream, IOContext, and SKImageInfo
        public SKVideo(IO.IOContext io, SKImageInfo info)
            : this(FFCodec2Skia.Create(io, info)!) { }

        // Constructor for Stream, IOContext, and codecOptions
        public SKVideo(IO.IOContext io, IDictionary<string, string> codecOptions)
            : this(FFCodec2Skia.Create(io, codecOptions)!) { }

        // Constructor for Stream, IOContext, SKImageInfo, and codecOptions
        public SKVideo(IO.IOContext io, SKImageInfo info, IDictionary<string, string> codecOptions)
            : this(FFCodec2Skia.Create(io, info, codecOptions)!) { }

        // Constructor for URL and SKImageInfo
        public SKVideo(string url, SKImageInfo info) : this(FFCodec2Skia.Create(url, info)!) { }

        // Constructor for URL, SKImageInfo, and codecOptions
        public SKVideo(string url, SKImageInfo info, IDictionary<string, string> codecOptions)
            : this(FFCodec2Skia.Create(url, info, codecOptions)!) { }

        #endregion

        #region IDisposable

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    cts.Cancel();
                    cts.Dispose();
                    bool entered = Monitor.TryEnter(_lock, 1000);
                    video.Dispose();
                    if (entered)
                        Monitor.Exit(_lock);
                }

                // TODO: Nicht verwaltete Ressourcen (nicht verwaltete Objekte) freigeben und Finalizer überschreiben
                // TODO: Große Felder auf NULL setzen
                disposedValue = true;
            }
        }

        // // TODO: Finalizer nur überschreiben, wenn "Dispose(bool disposing)" Code für die Freigabe nicht verwalteter Ressourcen enthält
        // ~SKVideo()
        // {
        //     // Ändern Sie diesen Code nicht. Fügen Sie Bereinigungscode in der Methode "Dispose(bool disposing)" ein.
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Ändern Sie diesen Code nicht. Fügen Sie Bereinigungscode in der Methode "Dispose(bool disposing)" ein.
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        #endregion

    }
}
