using System;
using System.Collections.Generic;
using RtspClientSharp.RawFrames;
using RtspClientSharp.RawFrames.Video;
using SimpleRtspPlayer.RawFramesDecoding.DecodedFrames;
using SimpleRtspPlayer.RawFramesDecoding.FFmpeg;
using SimpleRtspPlayer.RawFramesReceiving;

namespace SimpleRtspPlayer.GUI
{
    class RealtimeVideoSource : IVideoSource, IDisposable
    {
        private readonly object _syncRoot = new object();
        private IRawFramesSource _rawFramesSource;
        private bool _hardwareAccelerationEnabled = true;

        private readonly Dictionary<FFmpegVideoCodecId, FFmpegVideoDecoder> _videoDecodersMap =
            new Dictionary<FFmpegVideoCodecId, FFmpegVideoDecoder>();

        private readonly Dictionary<FFmpegVideoCodecId, bool> _hardwareAccelerationStatusMap =
            new Dictionary<FFmpegVideoCodecId, bool>();

        public event EventHandler<IDecodedVideoFrame> FrameReceived;
        public event EventHandler<string> DecoderStatusChanged;

        public bool HardwareAccelerationEnabled
        {
            get
            {
                lock (_syncRoot)
                    return _hardwareAccelerationEnabled;
            }
            set
            {
                lock (_syncRoot)
                {
                    if (_hardwareAccelerationEnabled == value)
                        return;

                    _hardwareAccelerationEnabled = value;
                    DropAllVideoDecoders();
                }

                string decoderMode = value ? "D3D11VA preferred" : "software only";
                DecoderStatusChanged?.Invoke(this, $"Video decoder mode: {decoderMode}");
            }
        }

        public void SetRawFramesSource(IRawFramesSource rawFramesSource)
        {
            if (_rawFramesSource != null)
            {
                _rawFramesSource.FrameReceived -= OnFrameReceived;
                lock (_syncRoot)
                    DropAllVideoDecoders();
            }

            _rawFramesSource = rawFramesSource;

            if (rawFramesSource == null)
                return;

            rawFramesSource.FrameReceived += OnFrameReceived;
        }

        public void Dispose()
        {
            lock (_syncRoot)
                DropAllVideoDecoders();
        }

        private void DropAllVideoDecoders()
        {
            foreach (FFmpegVideoDecoder decoder in _videoDecodersMap.Values)
                decoder.Dispose();

            _videoDecodersMap.Clear();
            _hardwareAccelerationStatusMap.Clear();
        }

        private void OnFrameReceived(object sender, RawFrame rawFrame)
        {
            if (!(rawFrame is RawVideoFrame rawVideoFrame))
                return;

            IDecodedVideoFrame decodedFrame;

            lock (_syncRoot)
            {
                FFmpegVideoCodecId codecId = DetectCodecId(rawVideoFrame);
                FFmpegVideoDecoder decoder = GetDecoderForFrame(codecId);

                decodedFrame = decoder.TryDecode(rawVideoFrame);
                ReportDecoderStatus(codecId, decoder);
            }

            if (decodedFrame != null)
                FrameReceived?.Invoke(this, decodedFrame);
        }

        private FFmpegVideoDecoder GetDecoderForFrame(FFmpegVideoCodecId codecId)
        {
            if (!_videoDecodersMap.TryGetValue(codecId, out FFmpegVideoDecoder decoder))
            {
                decoder = FFmpegVideoDecoder.CreateDecoder(codecId, _hardwareAccelerationEnabled);
                _videoDecodersMap.Add(codecId, decoder);
            }

            return decoder;
        }

        private FFmpegVideoCodecId DetectCodecId(RawVideoFrame videoFrame)
        {
            if (videoFrame is RawJpegFrame)
                return FFmpegVideoCodecId.MJPEG;
            if (videoFrame is RawH264Frame)
                return FFmpegVideoCodecId.H264;
            if (videoFrame is RawH265Frame)
                return FFmpegVideoCodecId.HEVC;

            throw new ArgumentOutOfRangeException(nameof(videoFrame));
        }

        private void ReportDecoderStatus(FFmpegVideoCodecId codecId, FFmpegVideoDecoder decoder)
        {
            bool isHardwareAccelerated = decoder.IsHardwareAccelerated;

            if (_hardwareAccelerationStatusMap.TryGetValue(codecId, out bool previousStatus) &&
                previousStatus == isHardwareAccelerated)
            {
                return;
            }

            _hardwareAccelerationStatusMap[codecId] = isHardwareAccelerated;

            string decoderMode = isHardwareAccelerated ? "D3D11VA hardware decode" : "software decode";
            DecoderStatusChanged?.Invoke(this, $"{codecId}: {decoderMode}");
        }
    }
}
