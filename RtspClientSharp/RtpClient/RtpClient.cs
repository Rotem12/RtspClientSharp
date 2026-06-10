using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using RtspClientSharp.MediaParsers;
using RtspClientSharp.RawFrames;
using RtspClientSharp.Rtp;
using RtspClientSharp.Rtsp;
using RtspClientSharp.Ts;
using RtspClientSharp.Utils;

namespace RtspClientSharp.RtpClient
{
    public sealed class RtpClient : IRtpClient
    {
        private bool _anyFrameReceived;
        private UdpClient _rtpClient;
        private int _disposed;
        public ConnectionParameters ConnectionParameters { get; }
        public int Timeout { get; set; } = 3000;

        public event Action<RawFrame> RawFrameReceived;
        public event EventHandler<RawFrame> FrameReceived;
        public bool IsH264 { get; set; } = true;

        private ITransportStream stream;
        private readonly SimpleHybridLock _hybridLock = new SimpleHybridLock();

        public RtpClient(ConnectionParameters connectionParameters)
        {
            ConnectionParameters = connectionParameters ??
                                   throw new ArgumentNullException(nameof(connectionParameters));

            RawFrameReceived = frame =>
            {
                Volatile.Write(ref _anyFrameReceived, true);
                FrameReceived?.Invoke(this, frame);
            };
        }

        ~RtpClient()
        {
            Dispose();
        }

        /// <summary>
        /// Connect to endpoint and start RTSP session
        /// </summary>
        /// <exception cref="OperationCanceledException"></exception>
        /// <exception cref="InvalidCredentialException"></exception>
        /// <exception cref="RtspClientException"></exception>
        public async Task ConnectAsync(CancellationToken token)
        {
            await Task.Run(async () =>
            {
                _rtpClient = CreateRtpClientInternal(ConnectionParameters.ConnectionUri.Host, ConnectionParameters.ConnectionUri.Port);

                try
                {
                    Task connectionTask = SetupConnection();

                    if (connectionTask.IsCompleted)
                    {
                        await connectionTask;
                        return;
                    }

                    var delayTaskCancelTokenSource = new CancellationTokenSource();
                    using (var linkedTokenSource =
                        CancellationTokenSource.CreateLinkedTokenSource(delayTaskCancelTokenSource.Token, token))
                    {
                        CancellationToken delayTaskToken = linkedTokenSource.Token;

                        Task delayTask = Task.Delay(Timeout, delayTaskToken);

                        if (connectionTask != await Task.WhenAny(connectionTask, delayTask))
                        {
                            connectionTask.IgnoreExceptions();

                            if (delayTask.IsCanceled)
                                throw new OperationCanceledException();

                            throw new TimeoutException();
                        }

                        delayTaskCancelTokenSource.Cancel();
                        await connectionTask;
                    }
                }
                catch (Exception e)
                {
                    _rtpClient.Dispose();
                    Volatile.Write(ref _rtpClient, null);

                    if (e is TimeoutException)
                        throw new RtspClientException("Connection timeout", e);

                    if (e is OperationCanceledException)
                        throw;

                    if (!(e is RtspClientException))
                        throw new RtspClientException("Connection error", e);

                    throw;
                }
            }, token).ConfigureAwait(false);
        }

        private async Task SetupConnection()
        {
            Codecs.CodecInfo info;
            if (IsH264)
            {
                info = new Codecs.Video.H264CodecInfo();
            }
            else
            {
                info = new Codecs.Video.MJPEGCodecInfo();
            }

            IMediaPayloadParser mediaPayloadParser = MediaPayloadParser.CreateFrom(info);

            IRtpSequenceAssembler rtpSequenceAssembler;

            ///      if (_connectionParameters.RtpTransport == RtpTransportProtocol.TCP)
            //      {
            //          rtpSequenceAssembler = null;
            //          mediaPayloadParser.FrameGenerated = OnFrameGeneratedLockfree;
            //      }
            //      else
            //      {
            rtpSequenceAssembler = new RtpSequenceAssembler(Constants.UdpReceiveBufferSize, 256);
            mediaPayloadParser.FrameGenerated = OnFrameGeneratedThreadSafe;
            //       }

            if(ConnectionParameters.UseTS)
            {
                stream = new TsStream(mediaPayloadParser);
            }
            else
            {
                stream = new RtpStream(mediaPayloadParser, 90000, rtpSequenceAssembler);
            }
        }

        private async Task ReceiveRtpFromUdpAsync()
        {
            UdpReceiveResult receiveResult = await _rtpClient.ReceiveAsync();
            ArraySegment<byte> payload = new ArraySegment<byte>(receiveResult.Buffer, 0, receiveResult.Buffer.Length);
            stream.Process(payload);
        }

        /// <summary>
        /// Receive frames. 
        /// Should be called after successful connection to endpoint or InvalidOperationException will be thrown
        /// </summary>
        /// <exception cref="OperationCanceledException"></exception>
        /// <exception cref="RtspClientException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        public async Task ReceiveAsync(CancellationToken token)
        {
            if (_rtpClient == null)
                throw new InvalidOperationException("Client should be connected first");

            try
            {
                Task receiveInternalTask = ReceiveRtpFromUdpAsync();

                if (receiveInternalTask.IsCompleted)
                {
                    await receiveInternalTask;
                    return;
                }

                var delayTaskCancelTokenSource = new CancellationTokenSource();
                using (var linkedTokenSource =
                    CancellationTokenSource.CreateLinkedTokenSource(delayTaskCancelTokenSource.Token, token))
                {
                    CancellationToken delayTaskToken = linkedTokenSource.Token;

                    while (true)
                    {
                        _anyFrameReceived = false;

                        Task result = await Task.WhenAny(receiveInternalTask,
                            Task.Delay(Timeout, delayTaskToken)).ConfigureAwait(false);

                        if (result == receiveInternalTask)
                        {
                            delayTaskCancelTokenSource.Cancel();
                            await receiveInternalTask;
                            break;
                        }

                        if (result.IsCanceled)
                        {
                            TimeSpan cancelTimeout = new TimeSpan(0, 0, 0, 0, Timeout);
                            if (cancelTimeout == TimeSpan.Zero ||
                                await Task.WhenAny(receiveInternalTask,
                                    Task.Delay(cancelTimeout, CancellationToken.None)) != receiveInternalTask)
                                _rtpClient.Dispose();

                            await Task.WhenAny(receiveInternalTask);
                            throw new OperationCanceledException();
                        }

                        if (!Volatile.Read(ref _anyFrameReceived))
                        {
                            receiveInternalTask.IgnoreExceptions();
                            throw new RtspClientException("Receive timeout", new TimeoutException());
                        }
                    }
                }
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new RtspClientException("Receive error", e);
            }
            //finally
            //{
            //    _rtpClient.Dispose();
            //    Volatile.Write(ref _rtpClient, null);
            //}
        }

        /// <summary>
        /// Clean up unmanaged resources
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
                return;

            _rtpClient?.Dispose();

            GC.SuppressFinalize(this);
        }

        private UdpClient CreateRtpClientInternal(string ip, int port)
        {
            var client = new UdpClient();
            if (ip != null)
            {
                IPAddress address = IPAddress.Parse(ip);

                try
                {
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseUnicastPort, true);
                }
                catch
                {
                    client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                }

                client.Client.Bind(new IPEndPoint(IPAddress.Any, port));

                if (IsMulticast(address))
                {
                    JoinMulticastGroup(client, address);
                }

                //if(address.Address == IPAddress.Broadcast.Address)
                //{
                //    client.EnableBroadcast = true;
                //}
            }

            return client;
        }

        private void JoinMulticastGroup(UdpClient client, IPAddress multicastAddress)
        {
            IPAddress multicastInterfaceAddress = ConnectionParameters.MulticastInterfaceAddress;
            if (multicastInterfaceAddress != null)
            {
                client.JoinMulticastGroup(multicastAddress, multicastInterfaceAddress);
                return;
            }

            if (multicastAddress.AddressFamily == AddressFamily.InterNetworkV6)
            {
                foreach (int i in GetMulticastCapableInterfaceIndex())
                    client.JoinMulticastGroup(i, multicastAddress);

                return;
            }

            IPAddress[] interfaces = GetMulticastCapableInterface();
            if (interfaces.Length == 0)
            {
                client.JoinMulticastGroup(multicastAddress);
                return;
            }

            Exception lastException = null;
            bool joined = false;

            foreach (IPAddress localAddress in interfaces)
            {
                try
                {
                    client.JoinMulticastGroup(multicastAddress, localAddress);
                    joined = true;
                }
                catch (Exception e) when (e is SocketException || e is ArgumentException)
                {
                    lastException = e;
                }
            }

            if (!joined && lastException != null)
                throw lastException;
        }


        private void OnFrameGeneratedThreadSafe(RawFrame frame)
        {
            if (RawFrameReceived == null)
                return;

            _hybridLock.Enter();

            try
            {
                RawFrameReceived.Invoke(frame);
            }
            finally
            {
                _hybridLock.Leave();
            }
        }

        static bool IsMulticast(IPAddress ip)
        {
            if(ip.AddressFamily == AddressFamily.InterNetwork)
            {
                byte firstOctet = ip.GetAddressBytes()[0];
                return firstOctet >= 224 && firstOctet <= 239;
            }
            else if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                return ip.IsIPv6Multicast;
            }

            return false;
        }

        private static IPAddress[] GetMulticastCapableInterface()
        {
            List<IPAddress> list = new List<IPAddress>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                   ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                if (!ni.SupportsMulticast) continue;

                if (ni.Description.ToLower().Contains("virtual") || ni.Name.ToLower().Contains("vmware")) continue;

                var ipProps = ni.GetIPProperties();
                foreach (var ip in ipProps.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip.Address))
                    {
                        list.Add(ip.Address);
                    }
                }
            }

            return list.ToArray();
        }

        private static int[] GetMulticastCapableInterfaceIndex()
        {
            List<int> list = new List<int>();
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                   ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;

                if (!ni.SupportsMulticast) continue;

                string desc = ni.Description.ToLower();
                string name = ni.Name.ToLower();
                if (desc.Contains("virtual") ||
                   name.Contains("vmware")) continue;

                var ipProps = ni.GetIPProperties();
                IPv4InterfaceProperties ipv4Props = ipProps.GetIPv4Properties();
                if (ipv4Props == null) continue;

                foreach (var ip in ipProps.UnicastAddresses)
                {
                    if (ip.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ip.Address)) continue;

                    list.Add(ipv4Props.Index);
                    break;
                }
            }

            return list.ToArray();
        }
    }
}
