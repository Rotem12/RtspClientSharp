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

        private readonly BlockingCollection<RawFrame> _queue = new BlockingCollection<RawFrame>(
            new ConcurrentQueue<RawFrame>(), MaxQueuedFrames);
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

            RawFrame copiedFrame = RawFrameCopier.Copy(frame);

            lock (_enqueueLock)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return false;

                try
                {
                    if (_waitForH264IFrame && copiedFrame is RawH264Frame &&
                        !(copiedFrame is RawH264IFrame))
                        return false;

                    if (_queue.TryAdd(copiedFrame))
                    {
                        if (copiedFrame is RawH264IFrame)
                            _waitForH264IFrame = false;

                        return true;
                    }

                    if (copiedFrame is RawH264Frame)
                    {
                        // A dropped H.264 frame invalidates all dependent P-frames already
                        // queued. Flush them and wait for a fresh IDR instead of displaying
                        // a damaged chain with increasing latency.
                        _waitForH264IFrame = true;
                        DrainQueue();

                        if (!(copiedFrame is RawH264IFrame))
                            return false;

                        _waitForH264IFrame = false;
                        return _queue.TryAdd(copiedFrame);
                    }

                    if (_queue.TryTake(out RawFrame droppedFrame) && droppedFrame is RawH264Frame)
                    {
                        _waitForH264IFrame = true;
                        DrainQueue();
                        return false;
                    }

                    return _queue.TryAdd(copiedFrame);
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

            foreach (RawFrame frame in _queue.GetConsumingEnumerable())
            {
                try
                {
                    _frameHandler(frame);
                }
                catch (Exception e)
                {
                    // A consumer callback must not terminate the receive/dispatch pipeline.
                    Debug.WriteLine(e);
                }
            }
        }

        private void DrainQueue()
        {
            while (_queue.TryTake(out RawFrame ignored))
            {
            }
        }
    }
}
