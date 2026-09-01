using System;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using RtspClientSharp.Codecs.Video;
using RtspClientSharp.RawFrames;
using RtspClientSharp.RawFrames.Video;
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
        CodecInfoType DetectedVideoCodec { get; }
        long TransportDatagramCount { get; }
        long TransportFrameCount { get; }
        long TransportDroppedFrameCount { get; }

        void SetRawFrameForwarding(bool enabled);
        void Start();
        void Stop();
    }

    internal interface IRawFrameClient : IDisposable
    {
        event EventHandler<RawFrame> RawFrameGenerated;
        event EventHandler<RawFrame> FrameReceived;

        MediaTransportMode DetectedTransportMode { get; }
        CodecInfoType DetectedVideoCodec { get; }
        long TransportDatagramCount { get; }
        long TransportFrameCount { get; }
        long TransportDroppedFrameCount { get; }

        void SetRawFrameForwarding(bool enabled);
        Task ConnectAsync(CancellationToken token);
        Task ReceiveAsync(CancellationToken token);
        Task ReceiveLoopAsync(CancellationToken token);
    }

    internal sealed class RawFrameSource : IRawFrameSource
    {
        private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);
        private readonly Func<IRawFrameClient> _clientFactory;
        private readonly object _syncRoot = new object();
        private CancellationTokenSource _cancellationTokenSource;
        private Task _workTask = Task.CompletedTask;
        private int _detectedTransportMode = (int)MediaTransportMode.Auto;
        private int _detectedVideoCodec = (int)CodecInfoType.Auto;
        private long _transportDatagramCount;
        private long _transportFrameCount;
        private long _transportDroppedFrameCount;
        private IRawFrameClient _currentClient;
        private int _rawFrameForwarding;
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
        public CodecInfoType DetectedVideoCodec =>
            (CodecInfoType)Volatile.Read(ref _detectedVideoCodec);
        public long TransportDatagramCount
        {
            get
            {
                CaptureCurrentClientMetrics();
                return Interlocked.Read(ref _transportDatagramCount);
            }
        }
        public long TransportFrameCount
        {
            get
            {
                CaptureCurrentClientMetrics();
                return Interlocked.Read(ref _transportFrameCount);
            }
        }
        public long TransportDroppedFrameCount
        {
            get
            {
                CaptureCurrentClientMetrics();
                return Interlocked.Read(ref _transportDroppedFrameCount);
            }
        }

        public void SetRawFrameForwarding(bool enabled)
        {
            lock (_syncRoot)
            {
                int value = enabled ? 1 : 0;
                if (_rawFrameForwarding == value)
                    return;

                _rawFrameForwarding = value;
                IRawFrameClient client = _currentClient;
                if (client == null)
                    return;

                if (enabled)
                {
                    client.RawFrameGenerated += ClientOnFrameGenerated;
                    client.SetRawFrameForwarding(true);
                }
                else
                {
                    client.SetRawFrameForwarding(false);
                    client.RawFrameGenerated -= ClientOnFrameGenerated;
                }
            }
        }

        public void Start()
        {
            Stop();

            lock (_syncRoot)
            {
                if (_disposed)
                    throw new ObjectDisposedException(nameof(RawFrameSource));

                Volatile.Write(ref _detectedTransportMode, (int)MediaTransportMode.Auto);
                Volatile.Write(ref _detectedVideoCodec, (int)CodecInfoType.Auto);
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
                        lock (_syncRoot)
                        {
                            _currentClient = client;
                            if (_rawFrameForwarding != 0)
                            {
                                client.RawFrameGenerated += ClientOnFrameGenerated;
                                client.SetRawFrameForwarding(true);
                            }
                            client.FrameReceived += ClientOnFrameReceived;
                        }
                        try
                        {
                            OnStatusChanged("Connecting...");
                            await client.ConnectAsync(token).ConfigureAwait(false);
                            Volatile.Write(ref _detectedTransportMode, (int)client.DetectedTransportMode);
                            UpdateDetectedVideoCodec(client, null);
                            UpdateTransportMetrics(client);
                            OnStatusChanged("Receiving frames...");

                            try
                            {
                                await client.ReceiveLoopAsync(token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException)
                            {
                                throw;
                            }
                            catch (RtspClientException exception)
                            {
                                OnStatusChanged(exception.Message);
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
                            lock (_syncRoot)
                            {
                                client.SetRawFrameForwarding(false);
                                client.RawFrameGenerated -= ClientOnFrameGenerated;
                                client.FrameReceived -= ClientOnFrameReceived;
                                if (ReferenceEquals(_currentClient, client))
                                    _currentClient = null;
                            }
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
            if (Volatile.Read(ref _detectedTransportMode) == (int)MediaTransportMode.Auto &&
                sender is IRawFrameClient client)
            {
                UpdateDetectedTransportMode(client);
            }
            UpdateDetectedVideoCodec(sender as IRawFrameClient, rawFrame);
            FrameReceived?.Invoke(this, rawFrame);
        }

        private void ClientOnFrameGenerated(object sender, RawFrame rawFrame)
        {
            if (Volatile.Read(ref _detectedTransportMode) == (int)MediaTransportMode.Auto &&
                sender is IRawFrameClient client)
                UpdateDetectedTransportMode(client);

            UpdateDetectedVideoCodec(sender as IRawFrameClient, rawFrame);
            RawFrameGenerated?.Invoke(this, rawFrame);
        }

        private void UpdateDetectedVideoCodec(IRawFrameClient client, RawFrame rawFrame)
        {
            CodecInfoType codec = client?.DetectedVideoCodec ?? CodecInfoType.Auto;
            if (codec == CodecInfoType.Auto)
            {
                if (rawFrame is RawH264Frame)
                    codec = CodecInfoType.H264;
                else if (rawFrame is RawH265Frame)
                    codec = CodecInfoType.H265;
                else if (rawFrame is RawJpegFrame)
                    codec = CodecInfoType.MJPEG;
            }

            if (codec != CodecInfoType.Auto)
                Volatile.Write(ref _detectedVideoCodec, (int)codec);
        }

        private void UpdateDetectedTransportMode(IRawFrameClient client)
        {
            int mode = (int)client.DetectedTransportMode;
            if (Volatile.Read(ref _detectedTransportMode) != mode)
                Volatile.Write(ref _detectedTransportMode, mode);
        }

        private void CaptureCurrentClientMetrics()
        {
            IRawFrameClient client = Volatile.Read(ref _currentClient);
            if (client == null)
                return;

            UpdateTransportMetrics(client);
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
        private bool _rawFrameForwarding;

        public RtspRawFrameClient(ConnectionParameters connectionParameters)
        {
            _client = new RtspClient(connectionParameters)
            {
                UseInlineFrameDelivery = true
            };
            _client.FrameReceived += ClientOnFrameReceived;
        }

        public event EventHandler<RawFrame> RawFrameGenerated;
        public event EventHandler<RawFrame> FrameReceived;
        public MediaTransportMode DetectedTransportMode => MediaTransportMode.Auto;
        public CodecInfoType DetectedVideoCodec => CodecInfoType.Auto;
        public long TransportDatagramCount => 0;
        public long TransportFrameCount => 0;
        public long TransportDroppedFrameCount => 0;

        public Task ConnectAsync(CancellationToken token) => _client.ConnectAsync(token);
        public Task ReceiveAsync(CancellationToken token) => _client.ReceiveAsync(token);
        public Task ReceiveLoopAsync(CancellationToken token) => _client.ReceiveAsync(token);

        public void SetRawFrameForwarding(bool enabled)
        {
            if (_rawFrameForwarding == enabled)
                return;

            _rawFrameForwarding = enabled;
            if (enabled)
                _client.RawFrameGenerated += ClientOnRawFrameGenerated;
            else
                _client.RawFrameGenerated -= ClientOnRawFrameGenerated;
        }

        public void Dispose()
        {
            SetRawFrameForwarding(false);
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
        private bool _rawFrameForwarding;

        public DirectUdpRawFrameClient(ConnectionParameters connectionParameters, CodecInfoType videoCodec,
            byte[] h264SpsPpsBytes, byte[] h265VpsSpsPpsBytes)
        {
            _client = new DirectRtpClient(connectionParameters)
            {
                VideoCodec = videoCodec,
                H264SpsPpsBytes = h264SpsPpsBytes ?? Array.Empty<byte>(),
                H265VpsSpsPpsBytes = h265VpsSpsPpsBytes ?? Array.Empty<byte>(),
                UseInlineFrameDelivery = true
            };
            _client.FrameReceived += ClientOnFrameReceived;
        }

        public event EventHandler<RawFrame> RawFrameGenerated;
        public event EventHandler<RawFrame> FrameReceived;
        public MediaTransportMode DetectedTransportMode => _client.DetectedTransportMode;
        public CodecInfoType DetectedVideoCodec => _client.DetectedVideoCodec;
        public long TransportDatagramCount => _client.ReceivedDatagramCount;
        public long TransportFrameCount => _client.GeneratedFrameCount;
        public long TransportDroppedFrameCount => _client.DispatcherDroppedFrameCount;

        public Task ConnectAsync(CancellationToken token) => _client.ConnectAsync(token);
        public Task ReceiveAsync(CancellationToken token) => _client.ReceiveAsync(token);
        public Task ReceiveLoopAsync(CancellationToken token) => _client.ReceiveLoopAsync(token);

        public void SetRawFrameForwarding(bool enabled)
        {
            if (_rawFrameForwarding == enabled)
                return;

            _rawFrameForwarding = enabled;
            if (enabled)
                _client.RawFrameGenerated += ClientOnRawFrameGenerated;
            else
                _client.RawFrameGenerated -= ClientOnRawFrameGenerated;
        }

        public void Dispose()
        {
            SetRawFrameForwarding(false);
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
