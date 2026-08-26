using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RtspClientSharp.RawFrames;
using RtspClientSharp.RawFrames.Video;

namespace RtspClientSharp.Utils
{
    sealed class RawFrameDispatcher : IDisposable
    {
        private const int MaxQueuedFrames = 4;

        private readonly BlockingCollection<RawFrameCopier.RawFrameCopy> _queue =
            new BlockingCollection<RawFrameCopier.RawFrameCopy>(
            new ConcurrentQueue<RawFrameCopier.RawFrameCopy>(), MaxQueuedFrames);
        private readonly Action<RawFrame> _frameHandler;
        private readonly object _enqueueLock = new object();
        private readonly Task _dispatchTask;
        private int _disposed;
        private int _dispatchThreadId;
        private bool _waitForH264IFrame;

        public RawFrameDispatcher(Action<RawFrame> frameHandler)
        {
            _frameHandler = frameHandler ?? throw new ArgumentNullException(nameof(frameHandler));
            _dispatchTask = Task.Factory.StartNew(DispatchLoop, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public bool TryEnqueue(RawFrame frame)
        {
            if (frame == null || Volatile.Read(ref _disposed) != 0)
                return false;

            lock (_enqueueLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return false;

                try
                {
                    bool isH264Frame = frame is RawH264Frame;
                    bool isH264IFrame = frame is RawH264IFrame;

                    if (_waitForH264IFrame && isH264Frame && !isH264IFrame)
                        return false;

                    // Decide whether the frame will be accepted before copying its payload. This
                    // avoids a large memcpy for every dependent H.264 frame discarded under load.
                    if (_queue.Count >= MaxQueuedFrames && isH264Frame)
                    {
                        // A dropped H.264 frame invalidates all dependent P-frames already
                        // queued. Flush them and wait for a fresh IDR instead of displaying
                        // a damaged chain with increasing latency.
                        _waitForH264IFrame = true;
                        DrainQueue();

                        if (!isH264IFrame)
                            return false;

                        _waitForH264IFrame = false;
                    }
                    else if (_queue.Count >= MaxQueuedFrames &&
                             _queue.TryTake(out RawFrameCopier.RawFrameCopy droppedFrame))
                    {
                        try
                        {
                            if (droppedFrame.Frame is RawH264Frame)
                            {
                                _waitForH264IFrame = true;
                                DrainQueue();
                                return false;
                            }
                        }
                        finally
                        {
                            droppedFrame.Dispose();
                        }
                    }

                    return TryEnqueueCopied(frame, isH264IFrame);
                }
                catch (InvalidOperationException)
                {
                    // CompleteAdding raced with this enqueue.
                    return false;
                }
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            lock (_enqueueLock)
            {
                _queue.CompleteAdding();
            }

            if (Thread.CurrentThread.ManagedThreadId == Volatile.Read(ref _dispatchThreadId))
                return;

            try
            {
                _dispatchTask.Wait(TimeSpan.FromSeconds(1));
            }
            catch (AggregateException)
            {
                // The dispatch loop logs individual callback failures and should normally
                // complete cleanly. Never let cleanup mask the original shutdown path.
            }
        }

        private void DispatchLoop()
        {
            Volatile.Write(ref _dispatchThreadId, Thread.CurrentThread.ManagedThreadId);

            foreach (RawFrameCopier.RawFrameCopy copiedFrame in _queue.GetConsumingEnumerable())
            {
                try
                {
                    _frameHandler(copiedFrame.Frame);
                }
                catch (Exception e)
                {
                    // A consumer callback must not terminate the receive/dispatch pipeline.
                    Debug.WriteLine(e);
                }
                finally
                {
                    copiedFrame.Dispose();
                }
            }
        }

        private void DrainQueue()
        {
            while (_queue.TryTake(out RawFrameCopier.RawFrameCopy ignored))
                ignored.Dispose();
        }

        private bool TryEnqueueCopied(RawFrame frame, bool isH264IFrame)
        {
            RawFrameCopier.RawFrameCopy copiedFrame = RawFrameCopier.Copy(frame);

            try
            {
                if (_queue.TryAdd(copiedFrame))
                {
                    if (isH264IFrame)
                        _waitForH264IFrame = false;

                    return true;
                }
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding raced with this enqueue.
            }

            copiedFrame.Dispose();
            return false;
        }
    }
}
