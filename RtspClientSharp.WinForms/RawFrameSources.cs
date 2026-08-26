using System;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using RtspClientSharp.RawFrames;
using RtspClientSharp.Rtsp;
using DirectRtpClient = RtspClientSharp.RtpClient.RtpClient;

namespace RtspClientSharp.WinForms
{
    internal interface IRawFrameSource : IDisposable
    {
        event EventHandler<RawFrame> RawFrameGenerated;
        event EventHandler<RawFrame> FrameReceived;
        event EventHandler<string> StatusChanged;

        MediaTransportMode DetectedTransportMode { get; }
        long TransportDatagramCount { get; }
        long TransportFrameCount { get; }
        long TransportDroppedFrameCount { get; }

        void Start();
        void Stop();
    }

    internal interface IRawFrameClient : IDisposable
    {
        event EventHandler<RawFrame> RawFrameGenerated;
        event EventHandler<RawFrame> FrameReceived;

        MediaTransportMode DetectedTransportMode { get; }
        long TransportDatagramCount { get; }
        long TransportFrameCount { get; }
        long TransportDroppedFrameCount { get; }

        Task ConnectAsync(CancellationToken token);
        Task ReceiveAsync(CancellationToken token);
    }

    internal sealed class RawFrameSource : IRawFrameSource
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);
        private readonly Func<IRawFrameClient> _clientFactory;
        private readonly object _syncRoot = new object();
        private CancellationTokenSource _cancellationTokenSource;
        private Task _workTask = Task.CompletedTask;
        private int _detectedTransportMode = (int)MediaTransportMode.Auto;
        private long _transportDatagramCount;
        private long _transportFrameCount;
        private long _transportDroppedFrameCount;
        private bool _disposed;

        public RawFrameSource(Func<IRawFrameClient> clientFactory)
        {
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        }

        public event EventHandler<RawFrame> RawFrameGenerated;
        public event EventHandler<RawFrame> FrameReceived;
        public event EventHandler<string> StatusChanged;
        public MediaTransportMode DetectedTransportMode =>
            (MediaTransportMode)Volatile.Read(ref _detectedTransportMode);
        public long TransportDatagramCount => Interlocked.Read(ref _transportDatagramCount);
        public long TransportFrameCount => Interlocked.Read(ref _transportFrameCount);
        public long TransportDroppedFrameCount => Interlocked.Read(ref _transportDroppedFrameCount);

        public void Start()
        {
            Stop();

            lock (_syncRoot)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(RawFrameSource));

                _cancellationTokenSource = new CancellationTokenSource();
                CancellationToken token = _cancellationTokenSource.Token;
                _workTask = Task.Run(() => ReceiveLoopAsync(token), token);
            }
        }

        public void Stop()
        {
            CancellationTokenSource cancellationTokenSource;
            Task workTask;

            lock (_syncRoot)
            {
                cancellationTokenSource = _cancellationTokenSource;
                workTask = _workTask;
                _cancellationTokenSource = null;
                _workTask = Task.CompletedTask;
            }

            if (cancellationTokenSource == null)
                return;

            cancellationTokenSource.Cancel();
            try
            {
                if (workTask != null && !workTask.Wait(TimeSpan.FromSeconds(2)))
                    OnStatusChanged("Stopping previous stream timed out");
            }
            catch (AggregateException exception)
            {
                exception.Handle(error => error is OperationCanceledException || error is TaskCanceledException);
            }
            finally
            {
                cancellationTokenSource.Dispose();
            }
        }

        public void Dispose()
        {
            lock (_syncRoot)
                _disposed = true;
            Stop();
        }

        private async Task ReceiveLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    using (IRawFrameClient client = _clientFactory())
                    {
                        client.RawFrameGenerated += ClientOnFrameGenerated;
                        client.FrameReceived += ClientOnFrameReceived;
                        try
                        {
                            OnStatusChanged("Connecting...");
                            await client.ConnectAsync(token).ConfigureAwait(false);
                            Volatile.Write(ref _detectedTransportMode, (int)client.DetectedTransportMode);
                            UpdateTransportMetrics(client);
                            OnStatusChanged("Receiving frames...");

                            while (!token.IsCancellationRequested)
                            {
                                try
                                {
                                    await client.ReceiveAsync(token).ConfigureAwait(false);
                                    Volatile.Write(ref _detectedTransportMode,
                                        (int)client.DetectedTransportMode);
                                    UpdateTransportMetrics(client);
                                }
                                catch (OperationCanceledException)
                                {
                                    throw;
                                }
                                catch (RtspClientException exception)
                                {
                                    OnStatusChanged(exception.Message);
                                    break;
                                }
                            }
                        }
                        catch (InvalidCredentialException)
                        {
                            OnStatusChanged("Invalid login and/or password");
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exception)
                        {
                            OnStatusChanged(exception.Message);
                        }
                        finally
                        {
                            client.RawFrameGenerated -= ClientOnFrameGenerated;
                            client.FrameReceived -= ClientOnFrameReceived;
                        }
                    }

                    if (!token.IsCancellationRequested)
                        await Task.Delay(RetryDelay, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                OnStatusChanged($"Stream stopped: {exception.Message}");
            }
        }

        private void ClientOnFrameReceived(object sender, RawFrame rawFrame)
        {
            if (sender is IRawFrameClient client)
            {
                Volatile.Write(ref _detectedTransportMode, (int)client.DetectedTransportMode);
                // RTSP ReceiveAsync owns a long-running read loop, so it may not
                // return to ReceiveLoopAsync between frames. Keep the source
                // counters current here for live diagnostics.
                UpdateTransportMetrics(client);
            }
            FrameReceived?.Invoke(this, rawFrame);
        }

        private void ClientOnFrameGenerated(object sender, RawFrame rawFrame)
        {
            RawFrameGenerated?.Invoke(this, rawFrame);
        }

        private void UpdateTransportMetrics(IRawFrameClient client)
        {
            Volatile.Write(ref _transportDatagramCount, client.TransportDatagramCount);
            Volatile.Write(ref _transportFrameCount, client.TransportFrameCount);
            Volatile.Write(ref _transportDroppedFrameCount, client.TransportDroppedFrameCount);
        }

        private void OnStatusChanged(string status)
        {
            StatusChanged?.Invoke(this, status);
        }
    }

    internal sealed class RtspRawFrameClient : IRawFrameClient
    {
        private readonly RtspClient _client;

        public RtspRawFrameClient(ConnectionParameters connectionParameters)
        {
            _client = new RtspClient(connectionParameters);
            _client.RawFrameGenerated += ClientOnRawFrameGenerated;
            _client.FrameReceived += ClientOnFrameReceived;
        }

        public event EventHandler<RawFrame> RawFrameGenerated;
        public event EventHandler<RawFrame> FrameReceived;
        public MediaTransportMode DetectedTransportMode => MediaTransportMode.Auto;
        public long TransportDatagramCount => 0;
        public long TransportFrameCount => 0;
        public long TransportDroppedFrameCount => 0;

        public Task ConnectAsync(CancellationToken token) => _client.ConnectAsync(token);
        public Task ReceiveAsync(CancellationToken token) => _client.ReceiveAsync(token);

        public void Dispose()
        {
            _client.RawFrameGenerated -= ClientOnRawFrameGenerated;
            _client.FrameReceived -= ClientOnFrameReceived;
            _client.Dispose();
        }

        private void ClientOnFrameReceived(object sender, RawFrame frame)
        {
            FrameReceived?.Invoke(this, frame);
        }

        private void ClientOnRawFrameGenerated(object sender, RawFrame frame)
        {
            RawFrameGenerated?.Invoke(this, frame);
        }
    }

    internal sealed class DirectUdpRawFrameClient : IRawFrameClient
    {
        private readonly DirectRtpClient _client;

        public DirectUdpRawFrameClient(ConnectionParameters connectionParameters, bool isH264,
            byte[] h264SpsPpsBytes)
        {
            _client = new DirectRtpClient(connectionParameters)
            {
                IsH264 = isH264,
                H264SpsPpsBytes = h264SpsPpsBytes ?? Array.Empty<byte>()
            };
            _client.RawFrameGenerated += ClientOnRawFrameGenerated;
            _client.FrameReceived += ClientOnFrameReceived;
        }

        public event EventHandler<RawFrame> RawFrameGenerated;
        public event EventHandler<RawFrame> FrameReceived;
        public MediaTransportMode DetectedTransportMode => _client.DetectedTransportMode;
        public long TransportDatagramCount => _client.ReceivedDatagramCount;
        public long TransportFrameCount => _client.GeneratedFrameCount;
        public long TransportDroppedFrameCount => _client.DispatcherDroppedFrameCount;

        public Task ConnectAsync(CancellationToken token) => _client.ConnectAsync(token);
        public Task ReceiveAsync(CancellationToken token) => _client.ReceiveAsync(token);

        public void Dispose()
        {
            _client.RawFrameGenerated -= ClientOnRawFrameGenerated;
            _client.FrameReceived -= ClientOnFrameReceived;
            _client.Dispose();
        }

        private void ClientOnFrameReceived(object sender, RawFrame frame)
        {
            FrameReceived?.Invoke(this, frame);
        }

        private void ClientOnRawFrameGenerated(object sender, RawFrame frame)
        {
            RawFrameGenerated?.Invoke(this, frame);
        }
    }
}
