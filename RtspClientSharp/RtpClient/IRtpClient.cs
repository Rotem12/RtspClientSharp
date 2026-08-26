using System;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using RtspClientSharp.RawFrames;

namespace RtspClientSharp.RtpClient
{
    public interface IRtpClient : IDisposable
    {
        ConnectionParameters ConnectionParameters { get; }

        /// <summary>
        /// Raised on the parser/receive path before the bounded display dispatcher
        /// copies or drops the frame. Subscribers must consume the frame before
        /// returning if they do not own another copy of its payload.
        /// </summary>
        event EventHandler<RawFrame> RawFrameGenerated;

        event EventHandler<RawFrame> FrameReceived;

        /// <summary>
        /// Connect to endpoint and start RTSP session
        /// </summary>
        /// <exception cref="OperationCanceledException"></exception>
        /// <exception cref="InvalidCredentialException"></exception>
        /// <exception cref="RtspClientException"></exception>
        Task ConnectAsync(CancellationToken token);

        /// <summary>
        /// Receive frames. 
        /// Should be called after successful connection to endpoint or <exception cref="InvalidOperationException"></exception> will be thrown
        /// </summary>
        /// <exception cref="OperationCanceledException"></exception>
        /// <exception cref="RtspClientException"></exception>
        /// <exception cref="InvalidOperationException"></exception>
        Task ReceiveAsync(CancellationToken token);
    }
}
