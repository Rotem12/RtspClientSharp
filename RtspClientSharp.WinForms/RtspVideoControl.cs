using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using RtspClientSharp.RawFrames;
using RtspClientSharp.RawFrames.Video;
using RtspClientSharp.Recording;
using System.Windows.Forms;

namespace RtspClientSharp.WinForms
{
    /// <summary>
    /// Low-latency WinForms video control for RTSP and direct UDP media.
    /// Direct UDP automatically detects RTP versus raw MPEG-TS when the connection
    /// parameters use <see cref="MediaTransportMode.Auto"/>.
    /// </summary>
    public class RtspVideoControl : Control
    {
        private const int DefaultRenderIntervalMs = 33;
        private const int MaxDisplayWidth = 1920;
        private const int MaxDisplayHeight = 1080;

        private readonly object _decoderSyncRoot = new object();
        private readonly object _frameSyncRoot = new object();
        private readonly object _recordingSyncRoot = new object();
        private readonly Dictionary<FfmpegVideoCodecId, FfmpegVideoDecoder> _videoDecoders =
            new Dictionary<FfmpegVideoCodecId, FfmpegVideoDecoder>();
        private readonly System.Windows.Forms.Timer _renderTimer;

        private ConnectionParameters _connectionParameters;
        private IRawFrameSource _rawFrameSource;
        private Bitmap _frontBitmap;
        private Bitmap _backBitmap;
        private byte[] _scaledBuffer = Array.Empty<byte>();
        private Size _lastBitmapSize;
        private DateTime _fpsWindowStart = DateTime.UtcNow;
        private int _fpsWindowFrames;
        private int _displayFps;
        private string _reportedDecoderStatus;
        private int _generation;
        private int _playing;
        private int _gpuDecodeFailureCount;
        private VideoPipelineMode _pipelineMode = VideoPipelineMode.Software;
        private VideoPipelineMode _effectivePipelineMode = VideoPipelineMode.Software;
        private bool _drawImage = true;
        private int _renderIntervalMs = DefaultRenderIntervalMs;
        private int _renderWidth;
        private int _renderHeight;
        private int _lastVideoWidth;
        private int _lastVideoHeight;
        private long _receivedFrameCount;
        private long _receivedVideoFrameCount;
        private long _nativeDecodedFrameCount;
        private long _decodeFailureCount;
        private long _decodedFrameCount;
        private long _droppedFrameCount;
        private long _presentedFrameCount;
        private long _publishedFrameSequence;
        private long _frontFrameSequence;
        private long _presentedFrameSequence;
        private int _pendingFrame;
        private int _waitForH264KeyFrame;
        private long _lastTransportDatagramCount;
        private long _lastTransportFrameCount;
        private long _lastTransportDroppedFrameCount;
        private CompressedVideoRollingBuffer _compressedPreRecord;
        private ICompressedVideoRecorder _compressedRecorder;

        public RtspVideoControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Black;
            _renderTimer = new System.Windows.Forms.Timer { Interval = DefaultRenderIntervalMs };
            _renderTimer.Tick += RenderTimerOnTick;
            UpdateRenderSize(ClientSize);
        }

        public ConnectionParameters ConnectionParameters
        {
            get => _connectionParameters;
            set
            {
                if (IsPlaying)
                    throw new InvalidOperationException("Stop the video control before changing ConnectionParameters.");
                _connectionParameters = value;
            }
        }

        public VideoSourceType SourceType { get; set; } = VideoSourceType.Auto;

        /// <summary>
        /// Optional override for direct UDP. Auto preserves the value already set on
        /// ConnectionParameters and otherwise sniffs RTP versus MPEG-TS.
        /// </summary>
        public MediaTransportMode TransportMode { get; set; } = MediaTransportMode.Auto;

        public bool IsH264 { get; set; } = true;

        /// <summary>
        /// Optional Annex-B SPS/PPS for a direct RTP H.264 sender that never transmits
        /// parameter sets in-band. When the sender includes SPS/PPS, leave this empty.
        /// </summary>
        public byte[] H264SpsPpsBytes { get; set; } = Array.Empty<byte>();

        public VideoPipelineMode PipelineMode
        {
            get => _pipelineMode;
            set
            {
                if (_pipelineMode == value)
                    return;

                _pipelineMode = value;
                _effectivePipelineMode = value;
                DropVideoDecoders();
            }
        }

        public VideoScaleMode ScaleMode { get; set; } = VideoScaleMode.RespectAspectRatio;

        /// <summary>
        /// Selects the compressed recording output. Auto uses MPEG-TS for normal
        /// paths and preserves Annex-B only for explicit .h264/.h265 paths.
        /// </summary>
        public CompressedVideoRecordingFormat RecordingFormat { get; set; } =
            CompressedVideoRecordingFormat.Auto;

        public int RenderIntervalMs
        {
            get => _renderIntervalMs;
            set
            {
                _renderIntervalMs = Math.Max(1, value);
                if (_renderTimer != null)
                    _renderTimer.Interval = _renderIntervalMs;
            }
        }

        public bool DrawImage
        {
            get => _drawImage;
            set
            {
                _drawImage = value;
                if (!value)
                    ClearPresentedFrame();
                Invalidate(false);
            }
        }

        public bool ShowFPS { get; set; }

        public double DrawLeft { get; set; }
        public double DrawUp { get; set; }
        public double DrawRight { get; set; }
        public double DrawDown { get; set; }

        public int PreRecordSeconds { get; set; }

        public bool IsPlaying => Volatile.Read(ref _playing) != 0;

        public bool IsHardwareDecodeActive
        {
            get
            {
                lock (_decoderSyncRoot)
                {
                    foreach (FfmpegVideoDecoder decoder in _videoDecoders.Values)
                    {
                        if (decoder.IsHardwareAccelerated)
                            return true;
                    }
                }

                return false;
            }
        }

        public bool IsRecording
        {
            get
            {
                lock (_recordingSyncRoot)
                    return _compressedRecorder != null;
            }
        }
        public string LastStatus { get; private set; }
        public string LastDecoderStatus { get; private set; }
        public string LastRecordingStatus { get; private set; }
        public MediaTransportMode DetectedTransportMode { get; private set; } = MediaTransportMode.Auto;
        public int LastVideoWidth => Volatile.Read(ref _lastVideoWidth);
        public int LastVideoHeight => Volatile.Read(ref _lastVideoHeight);
        /// <summary>Number of complete raw frames delivered by the transport pipeline.</summary>
        public long ReceivedFrameCount => Interlocked.Read(ref _receivedFrameCount);
        /// <summary>Number of complete raw video frames delivered by the transport pipeline.</summary>
        public long ReceivedVideoFrameCount => Interlocked.Read(ref _receivedVideoFrameCount);
        /// <summary>Number of frames successfully produced by the native decoder.</summary>
        public long NativeDecodedFrameCount => Interlocked.Read(ref _nativeDecodedFrameCount);
        /// <summary>Number of native decode calls that did not produce a frame.</summary>
        public long DecodeFailureCount => Interlocked.Read(ref _decodeFailureCount);
        /// <summary>Number of decoded frames published to the managed display buffer.</summary>
        public long DecodedFrameCount => Interlocked.Read(ref _decodedFrameCount);
        public long DroppedFrameCount => Interlocked.Read(ref _droppedFrameCount);
        /// <summary>Number of distinct published frames painted by the control.</summary>
        public long PresentedFrameCount => Interlocked.Read(ref _presentedFrameCount);
        /// <summary>Number of non-empty UDP datagrams read by the direct transport.</summary>
        public long TransportDatagramCount => GetTransportMetric(
            source => source.TransportDatagramCount, ref _lastTransportDatagramCount);
        /// <summary>Number of complete media frames generated before display dispatch.</summary>
        public long TransportFrameCount => GetTransportMetric(
            source => source.TransportFrameCount, ref _lastTransportFrameCount);
        /// <summary>Number of complete frames rejected by the bounded transport dispatcher.</summary>
        public long TransportDroppedFrameCount => GetTransportMetric(
            source => source.TransportDroppedFrameCount, ref _lastTransportDroppedFrameCount);

        public event EventHandler<string> StatusChanged;
        public event EventHandler<VideoFrameEventArgs> FrameDecoded;

        public void Start()
        {
            if (IsPlaying)
                Stop();

            ConnectionParameters connectionParameters = _connectionParameters;
            if (connectionParameters == null)
                throw new InvalidOperationException("ConnectionParameters must be set before starting the video control.");

            if (!IsHandleCreated)
                CreateControl();

            Interlocked.Increment(ref _generation);
            Interlocked.Exchange(ref _receivedFrameCount, 0);
            Interlocked.Exchange(ref _receivedVideoFrameCount, 0);
            Interlocked.Exchange(ref _nativeDecodedFrameCount, 0);
            Interlocked.Exchange(ref _decodeFailureCount, 0);
            Interlocked.Exchange(ref _decodedFrameCount, 0);
            Interlocked.Exchange(ref _droppedFrameCount, 0);
            Interlocked.Exchange(ref _presentedFrameCount, 0);
            Interlocked.Exchange(ref _publishedFrameSequence, 0);
            Interlocked.Exchange(ref _frontFrameSequence, 0);
            Interlocked.Exchange(ref _presentedFrameSequence, 0);
            Interlocked.Exchange(ref _pendingFrame, 0);
            Interlocked.Exchange(ref _waitForH264KeyFrame, 0);
            Interlocked.Exchange(ref _lastTransportDatagramCount, 0);
            Interlocked.Exchange(ref _lastTransportFrameCount, 0);
            Interlocked.Exchange(ref _lastTransportDroppedFrameCount, 0);
            _effectivePipelineMode = _pipelineMode;
            _gpuDecodeFailureCount = 0;
            _lastVideoWidth = 0;
            _lastVideoHeight = 0;
            LastDecoderStatus = null;
            _reportedDecoderStatus = null;
            DetectedTransportMode = MediaTransportMode.Auto;
            ClearPresentedFrame();
            DropVideoDecoders();

            lock (_recordingSyncRoot)
            {
                _compressedPreRecord = PreRecordSeconds > 0
                    ? new CompressedVideoRollingBuffer(TimeSpan.FromSeconds(PreRecordSeconds))
                    : null;
            }

            IRawFrameSource source = CreateRawFrameSource(connectionParameters);
            source.RawFrameGenerated += RawFrameSourceOnFrameGenerated;
            source.FrameReceived += RawFrameSourceOnFrameReceived;
            source.StatusChanged += RawFrameSourceOnStatusChanged;

            _rawFrameSource = source;
            Volatile.Write(ref _playing, 1);
            _renderTimer.Interval = _renderIntervalMs;
            _renderTimer.Start();
            SetStatus("Starting stream...");

            try
            {
                source.Start();
            }
            catch
            {
                Volatile.Write(ref _playing, 0);
                source.RawFrameGenerated -= RawFrameSourceOnFrameGenerated;
                source.FrameReceived -= RawFrameSourceOnFrameReceived;
                source.StatusChanged -= RawFrameSourceOnStatusChanged;
                source.Dispose();
                _rawFrameSource = null;
                throw;
            }
        }

        public void Stop()
        {
            Interlocked.Increment(ref _generation);
            bool wasPlaying = Interlocked.Exchange(ref _playing, 0) != 0;
            _renderTimer.Stop();

            IRawFrameSource source = _rawFrameSource;
            _rawFrameSource = null;
            if (source != null)
            {
                source.RawFrameGenerated -= RawFrameSourceOnFrameGenerated;
                source.FrameReceived -= RawFrameSourceOnFrameReceived;
                source.StatusChanged -= RawFrameSourceOnStatusChanged;
                source.Stop();
                CaptureTransportMetrics(source);
                source.Dispose();
            }

            StopRecording();
            lock (_recordingSyncRoot)
            {
                _compressedPreRecord?.Clear();
                _compressedPreRecord = null;
            }
            DropVideoDecoders();
            ClearPresentedFrame();
            if (wasPlaying || source != null)
                SetStatus("Stopped");
            Invalidate(false);
        }

        public void SetSize()
        {
            UpdateRenderSize(ClientSize);
            DropVideoDecoders();
            ClearPresentedFrame();
            Invalidate(false);
        }

        public void GetResolution()
        {
            Console.WriteLine("resolution=" + LastVideoWidth + "x" + LastVideoHeight);
        }

        /// <summary>
        /// Starts the optimized compressed recorder. Normal paths produce a
        /// video-only MPEG-TS file; explicit .h264/.h265 paths keep Annex-B output.
        /// </summary>
        public void StartRecording(string outputFilePath)
        {
            if (string.IsNullOrWhiteSpace(outputFilePath))
                throw new ArgumentException("An output file path is required.", nameof(outputFilePath));

            StartRecording(outputFilePath, new CompressedVideoRecorderOptions
            {
                Format = RecordingFormat
            });
        }

        /// <summary>
        /// Starts compressed recording with optional output and timestamp options.
        /// The recorder consumes raw frames directly when the built-in recorder is used.
        /// </summary>
        public void StartRecording(string outputFilePath, CompressedVideoRecorderOptions options)
        {
            if (string.IsNullOrWhiteSpace(outputFilePath))
                throw new ArgumentException("An output file path is required.", nameof(outputFilePath));

            lock (_recordingSyncRoot)
            {
                if (_compressedRecorder != null)
                    return;

                var recorder = CompressedVideoRecorderFactory.Create(outputFilePath, options ??
                    new CompressedVideoRecorderOptions { Format = RecordingFormat });
                IReadOnlyList<EncodedVideoFrame> preRecordFrames = null;
                if (_compressedPreRecord != null)
                    preRecordFrames = _compressedPreRecord.GetFramesForRecording(
                        DateTime.UtcNow.AddSeconds(-PreRecordSeconds));

                recorder.Start(outputFilePath, preRecordFrames, options);
                _compressedRecorder = recorder;
                LastRecordingStatus = preRecordFrames == null
                    ? "Compressed recording waiting for first keyframe"
                    : $"Compressed recording started with {preRecordFrames.Count} pre-record frames";
            }
        }

        public void StopRecording()
        {
            lock (_recordingSyncRoot)
            {
                ICompressedVideoRecorder recorder = _compressedRecorder;
                _compressedRecorder = null;
                if (recorder == null)
                    return;

                string outputPath = recorder.OutputFilePath;
                recorder.Stop();
                recorder.Dispose();
                LastRecordingStatus = outputPath == null
                    ? "Compressed recording stopped before first keyframe"
                    : $"Compressed recording stopped: {outputPath}";
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRenderSize(ClientSize);
            if (IsPlaying)
            {
                DropVideoDecoders();
                ClearPresentedFrame();
            }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // OnPaint clears the exact client area. Avoid a second erase pass.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);

            if (_effectivePipelineMode != VideoPipelineMode.GpuD3D11EndToEnd && DrawImage)
            {
                lock (_frameSyncRoot)
                {
                    if (_frontBitmap != null)
                    {
                        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                        e.Graphics.CompositingQuality = CompositingQuality.HighSpeed;

                        int left = (ClientSize.Width - _frontBitmap.Width) / 2;
                        int top = (ClientSize.Height - _frontBitmap.Height) / 2;
                        e.Graphics.DrawImageUnscaled(_frontBitmap, left, top);
                        if (_presentedFrameSequence != _frontFrameSequence)
                        {
                            _presentedFrameSequence = _frontFrameSequence;
                            Interlocked.Increment(ref _presentedFrameCount);
                        }
                        Interlocked.Exchange(ref _pendingFrame, 0);
                    }
                }
            }

            if (ShowFPS)
            {
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
                using (var brush = new SolidBrush(Color.LimeGreen))
                    e.Graphics.DrawString(_displayFps.ToString(), Font, brush, 8, 8);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Stop();
                _renderTimer.Dispose();
            }

            base.Dispose(disposing);
        }

        private IRawFrameSource CreateRawFrameSource(ConnectionParameters connectionParameters)
        {
            VideoSourceType sourceType = SourceType;
            if (sourceType == VideoSourceType.Auto)
            {
                string scheme = connectionParameters.ConnectionUri.Scheme;
                sourceType = scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase) ||
                             scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    ? VideoSourceType.Rtsp
                    : VideoSourceType.DirectUdp;
            }

            if (sourceType == VideoSourceType.Rtsp)
                return new RawFrameSource(() => new RtspRawFrameClient(connectionParameters));

            if (sourceType != VideoSourceType.DirectUdp)
                throw new ArgumentOutOfRangeException(nameof(SourceType));

            if (TransportMode != MediaTransportMode.Auto ||
                connectionParameters.TransportMode == MediaTransportMode.Auto)
                connectionParameters.TransportMode = TransportMode;

            return new RawFrameSource(() => new DirectUdpRawFrameClient(connectionParameters, IsH264,
                H264SpsPpsBytes));
        }

        private void RawFrameSourceOnFrameGenerated(object sender, RawFrame rawFrame)
        {
            if (!IsPlaying || !ReferenceEquals(sender, _rawFrameSource) || rawFrame == null)
                return;

            if (sender is IRawFrameSource source)
            {
                DetectedTransportMode = source.DetectedTransportMode;
                CaptureTransportMetrics(source);
            }

            try
            {
                // This callback runs before the bounded display dispatcher. The
                // built-in recorder consumes the parser-owned raw segments here,
                // so a busy decoder/UI cannot cause recording to lose frames.
                RecordCompressedFrame(rawFrame);
            }
            catch (Exception exception)
            {
                StopRecording();
                LastRecordingStatus = $"Compressed recording failed: {exception.Message}";
            }
        }

        private void RawFrameSourceOnFrameReceived(object sender, RawFrame rawFrame)
        {
            if (!IsPlaying || !ReferenceEquals(sender, _rawFrameSource))
                return;

            if (sender is IRawFrameSource source)
            {
                DetectedTransportMode = source.DetectedTransportMode;
                CaptureTransportMetrics(source);
            }

            if (rawFrame == null)
                return;

            Interlocked.Increment(ref _receivedFrameCount);

            if (!(rawFrame is RawVideoFrame rawVideoFrame))
                return;

            Interlocked.Increment(ref _receivedVideoFrameCount);

            int generation = Volatile.Read(ref _generation);
            try
            {
                if (_effectivePipelineMode == VideoPipelineMode.GpuD3D11EndToEnd && DrawImage)
                {
                    if (TryRenderGpuFrame(generation, rawVideoFrame))
                        return;
                }

                if (!DrawImage)
                    return;

                DecodeAndPublishCpuFrame(generation, rawVideoFrame);
            }
            catch (Exception exception)
            {
                if (rawVideoFrame is RawH264Frame)
                    Interlocked.Exchange(ref _waitForH264KeyFrame, 1);
                Interlocked.Increment(ref _decodeFailureCount);
                SetDecoderStatus($"Video decode failed: {exception.Message}");
            }
        }

        private bool TryRenderGpuFrame(int generation, RawVideoFrame rawVideoFrame)
        {
            if (!IsPlaying || generation != Volatile.Read(ref _generation))
                return true;

            if (ShouldWaitForH264KeyFrame(rawVideoFrame))
            {
                Interlocked.Increment(ref _droppedFrameCount);
                return true;
            }

            FfmpegVideoCodecId codecId = DetectCodecId(rawVideoFrame);
            bool fallbackToCpu = false;
            int fallbackResultCode = 0;
            VideoFrameEventArgs decodedEvent = null;

            try
            {
                lock (_decoderSyncRoot)
                {
                    if (!_videoDecoders.TryGetValue(codecId, out FfmpegVideoDecoder decoder))
                    {
                        decoder = FfmpegVideoDecoder.Create(codecId, true);
                        decoder.SetRenderTarget(Handle);
                        _videoDecoders.Add(codecId, decoder);
                    }

                    if (!decoder.TryDecodeToGpu(rawVideoFrame,
                        out DecodedVideoFrameParameters parameters))
                    {
                        Interlocked.Increment(ref _decodeFailureCount);
                        Interlocked.Increment(ref _droppedFrameCount);
                        int failures = Interlocked.Increment(ref _gpuDecodeFailureCount);
                        int resultCode = decoder.LastGpuDecodeResult;
                        if (!decoder.IsHardwareAccelerated || failures >= 3 || resultCode == -5)
                        {
                            fallbackToCpu = true;
                            fallbackResultCode = resultCode;
                        }
                    }
                    else
                    {
                        Interlocked.Exchange(ref _gpuDecodeFailureCount, 0);
                        Volatile.Write(ref _lastVideoWidth, parameters.Width);
                        Volatile.Write(ref _lastVideoHeight, parameters.Height);
                        if (rawVideoFrame is RawH264IFrame)
                            Interlocked.Exchange(ref _waitForH264KeyFrame, 0);
                        Interlocked.Increment(ref _nativeDecodedFrameCount);
                        Interlocked.Increment(ref _decodedFrameCount);
                        SetDecoderStatus($"{codecId}: D3D11 end-to-end GPU render active");
                        decoder.RenderGpuFrame(ClampCrop(DrawLeft), ClampCrop(DrawUp),
                            ClampCrop(DrawRight), ClampCrop(DrawDown));
                        decodedEvent = new VideoFrameEventArgs(rawVideoFrame.Timestamp, parameters.Width,
                            parameters.Height, decoder.IsHardwareAccelerated, DetectedTransportMode);
                    }
                }
            }
            catch (Exception exception)
            {
                fallbackToCpu = true;
                SetDecoderStatus($"D3D11 render setup failed; falling back to CPU readback: {exception.Message}");
            }

            if (fallbackToCpu)
            {
                // End-to-end rendering failed, so do not immediately retry the same
                // hardware path through a second decoder. The explicit
                // HardwareDecodeWithCpuReadback mode remains available when that is
                // the desired fallback; this mode uses a clean software decoder.
                _effectivePipelineMode = VideoPipelineMode.Software;
                DropVideoDecoders();
                if (fallbackResultCode != 0)
                    SetDecoderStatus($"D3D11VA unavailable (decode code {fallbackResultCode}); falling back to software decode");
                DecodeAndPublishCpuFrame(generation, rawVideoFrame);
                return true;
            }

            if (decodedEvent != null)
                FrameDecoded?.Invoke(this, decodedEvent);
            return true;
        }

        private void DecodeAndPublishCpuFrame(int generation, RawVideoFrame rawVideoFrame)
        {
            if (!IsPlaying || generation != Volatile.Read(ref _generation))
                return;

            if (ShouldWaitForH264KeyFrame(rawVideoFrame))
            {
                Interlocked.Increment(ref _droppedFrameCount);
                return;
            }

            FfmpegVideoCodecId codecId = DetectCodecId(rawVideoFrame);
            FfmpegVideoDecoder decoder;
            DecodedVideoFrameParameters parameters;
            lock (_decoderSyncRoot)
            {
                if (!_videoDecoders.TryGetValue(codecId, out decoder))
                {
                    bool preferHardware = _effectivePipelineMode == VideoPipelineMode.HardwareDecodeWithCpuReadback;
                    decoder = FfmpegVideoDecoder.Create(codecId, preferHardware);
                    _videoDecoders.Add(codecId, decoder);
                }

                if (!decoder.TryDecode(rawVideoFrame, out parameters))
                {
                    if (rawVideoFrame is RawH264Frame)
                        Interlocked.Exchange(ref _waitForH264KeyFrame, 1);
                    Interlocked.Increment(ref _decodeFailureCount);
                    Interlocked.Increment(ref _droppedFrameCount);
                    return;
                }

                if (rawVideoFrame is RawH264IFrame)
                    Interlocked.Exchange(ref _waitForH264KeyFrame, 0);
                Interlocked.Increment(ref _nativeDecodedFrameCount);

                // The decoder must consume every frame to keep its reference chain valid,
                // but the UI only needs the newest frame. Do not spend another scaler copy
                // while the previous display frame is still waiting for the next paint tick.
                if (Interlocked.CompareExchange(ref _pendingFrame, 1, 0) != 0)
                {
                    Interlocked.Increment(ref _droppedFrameCount);
                    return;
                }

                bool published = false;
                VideoTransformParameters transform = CreateTransform(parameters);
                try
                {
                    FfmpegVideoScaler scaler = decoder.GetScaler(transform);
                    int requiredSize = checked(scaler.ScaledStride * scaler.ScaledHeight);
                    if (_scaledBuffer.Length != requiredSize)
                        _scaledBuffer = new byte[requiredSize];

                    decoder.ScaleTo(scaler, _scaledBuffer);
                    lock (_frameSyncRoot)
                    {
                        EnsureBitmapBuffers(scaler.ScaledWidth, scaler.ScaledHeight);
                        CopyBgr24ToBitmap(_scaledBuffer, scaler.ScaledStride, _backBitmap,
                            scaler.ScaledWidth, scaler.ScaledHeight);

                        Bitmap oldFront = _frontBitmap;
                        _frontBitmap = _backBitmap;
                        _backBitmap = oldFront;
                        _frontFrameSequence = Interlocked.Increment(ref _publishedFrameSequence);
                    }

                    published = true;
                }
                finally
                {
                    if (!published)
                        Interlocked.Exchange(ref _pendingFrame, 0);
                }

                Volatile.Write(ref _lastVideoWidth, parameters.Width);
                Volatile.Write(ref _lastVideoHeight, parameters.Height);
                Interlocked.Increment(ref _decodedFrameCount);
                UpdateFps();
                ReportDecoderStatus(codecId, decoder);
            }

            FrameDecoded?.Invoke(this, new VideoFrameEventArgs(rawVideoFrame.Timestamp,
                LastVideoWidth, LastVideoHeight, decoder.IsHardwareAccelerated, DetectedTransportMode));
        }

        private VideoTransformParameters CreateTransform(DecodedVideoFrameParameters parameters)
        {
            int targetWidth = _renderWidth > 0 ? _renderWidth : parameters.Width;
            int targetHeight = _renderHeight > 0 ? _renderHeight : parameters.Height;
            targetWidth = Math.Min(parameters.Width, Math.Min(MaxDisplayWidth, Math.Max(2, targetWidth)));
            targetHeight = Math.Min(parameters.Height, Math.Min(MaxDisplayHeight, Math.Max(2, targetHeight)));

            double left = ClampCrop(DrawLeft);
            double top = ClampCrop(DrawUp);
            double right = ClampCrop(DrawRight);
            double bottom = ClampCrop(DrawDown);
            if (left + right >= 0.99)
                right = 0;
            if (top + bottom >= 0.99)
                bottom = 0;

            return new VideoTransformParameters(
                new RectangleF((float)left, (float)top, (float)(1 - left - right),
                    (float)(1 - top - bottom)),
                new Size(targetWidth, targetHeight), ScaleMode);
        }

        private void EnsureBitmapBuffers(int width, int height)
        {
            lock (_frameSyncRoot)
            {
                if (_lastBitmapSize.Width == width && _lastBitmapSize.Height == height &&
                    _frontBitmap != null && _backBitmap != null)
                    return;

                Bitmap oldFront = _frontBitmap;
                Bitmap oldBack = _backBitmap;
                _frontBitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                _backBitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                _lastBitmapSize = new Size(width, height);
                oldFront?.Dispose();
                oldBack?.Dispose();
            }
        }

        private void ClearPresentedFrame()
        {
            lock (_frameSyncRoot)
            {
                _frontBitmap?.Dispose();
                _backBitmap?.Dispose();
                _frontBitmap = null;
                _backBitmap = null;
                _lastBitmapSize = Size.Empty;
                Interlocked.Exchange(ref _frontFrameSequence, 0);
                Interlocked.Exchange(ref _presentedFrameSequence, 0);
                Interlocked.Exchange(ref _pendingFrame, 0);
            }
        }

        private static void CopyBgr24ToBitmap(byte[] source, int sourceStride, Bitmap target,
            int width, int height)
        {
            BitmapData data = null;
            int rowBytes = checked(width * 3);
            try
            {
                data = target.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly,
                    PixelFormat.Format24bppRgb);
                for (int row = 0; row < height; row++)
                {
                    IntPtr destination = IntPtr.Add(data.Scan0, row * data.Stride);
                    System.Runtime.InteropServices.Marshal.Copy(source, row * sourceStride, destination, rowBytes);
                }
            }
            finally
            {
                if (data != null)
                    target.UnlockBits(data);
            }
        }

        private void RecordCompressedFrame(RawFrame rawFrame)
        {
            lock (_recordingSyncRoot)
            {
                if (_compressedPreRecord == null && _compressedRecorder == null)
                    return;

                EncodedVideoFrame encodedFrame = null;
                if (_compressedPreRecord != null &&
                    EncodedVideoFrameFactory.TryCreate(rawFrame, out encodedFrame))
                    _compressedPreRecord.Add(encodedFrame);

                if (_compressedRecorder is IRawFrameCompressedVideoRecorder rawRecorder)
                {
                    // The built-in recorder writes from the RawFrame segments before
                    // RawFrameDispatcher returns those pooled buffers. This avoids the
                    // previous per-frame EncodedVideoFrame allocation and copy.
                    rawRecorder.WriteRawFrame(rawFrame);
                }
                else if (_compressedRecorder != null)
                {
                    if (encodedFrame == null &&
                        !EncodedVideoFrameFactory.TryCreate(rawFrame, out encodedFrame))
                        return;

                    _compressedRecorder.Write(encodedFrame);
                }

                if (_compressedRecorder?.OutputFilePath != null)
                    LastRecordingStatus = $"Compressed recording: {_compressedRecorder.OutputFilePath}";
            }
        }

        private void RawFrameSourceOnStatusChanged(object sender, string status)
        {
            if (ReferenceEquals(sender, _rawFrameSource))
                SetStatus(status);
        }

        private void CaptureTransportMetrics(IRawFrameSource source)
        {
            Interlocked.Exchange(ref _lastTransportDatagramCount, source.TransportDatagramCount);
            Interlocked.Exchange(ref _lastTransportFrameCount, source.TransportFrameCount);
            Interlocked.Exchange(ref _lastTransportDroppedFrameCount, source.TransportDroppedFrameCount);
        }

        private long GetTransportMetric(Func<IRawFrameSource, long> selector,
            ref long lastValue)
        {
            IRawFrameSource source = _rawFrameSource;
            if (source != null)
            {
                long value = selector(source);
                Interlocked.Exchange(ref lastValue, value);
                return value;
            }

            return Interlocked.Read(ref lastValue);
        }

        private void ReportDecoderStatus(FfmpegVideoCodecId codecId, FfmpegVideoDecoder decoder)
        {
            string mode = decoder.IsHardwareAccelerated
                ? "D3D11VA hardware decode active"
                : _effectivePipelineMode == VideoPipelineMode.HardwareDecodeWithCpuReadback
                    ? "D3D11VA unavailable; using software decode"
                    : "software decode active";
            SetDecoderStatus($"{codecId}: {mode}");
        }

        private void DropVideoDecoders()
        {
            lock (_decoderSyncRoot)
            {
                foreach (FfmpegVideoDecoder decoder in _videoDecoders.Values)
                    decoder.Dispose();
                _videoDecoders.Clear();
                _scaledBuffer = Array.Empty<byte>();
            }

            Interlocked.Exchange(ref _waitForH264KeyFrame, 1);
        }

        private void UpdateRenderSize(Size clientSize)
        {
            _renderWidth = Math.Max(0, clientSize.Width);
            _renderHeight = Math.Max(0, clientSize.Height);
        }

        private void RenderTimerOnTick(object sender, EventArgs e)
        {
            if (!IsPlaying)
                return;
            if (DrawImage || ShowFPS)
                Invalidate(false);
        }

        private void UpdateFps()
        {
            _fpsWindowFrames++;
            DateTime now = DateTime.UtcNow;
            if ((now - _fpsWindowStart).TotalSeconds < 1)
                return;

            _displayFps = _fpsWindowFrames;
            _fpsWindowFrames = 0;
            _fpsWindowStart = now;
        }

        private void SetStatus(string status)
        {
            LastStatus = status;
            StatusChanged?.Invoke(this, status);
        }

        private void SetDecoderStatus(string status)
        {
            LastDecoderStatus = status;
            if (string.Equals(_reportedDecoderStatus, status, StringComparison.Ordinal))
                return;

            _reportedDecoderStatus = status;
            StatusChanged?.Invoke(this, status);
        }

        private static FfmpegVideoCodecId DetectCodecId(RawVideoFrame videoFrame)
        {
            if (videoFrame is RawJpegFrame)
                return FfmpegVideoCodecId.Mjpeg;
            if (videoFrame is RawH264Frame)
                return FfmpegVideoCodecId.H264;
            if (videoFrame is RawH265Frame)
                return FfmpegVideoCodecId.Hevc;
            throw new ArgumentOutOfRangeException(nameof(videoFrame));
        }

        private bool ShouldWaitForH264KeyFrame(RawVideoFrame videoFrame)
        {
            return Volatile.Read(ref _waitForH264KeyFrame) != 0 &&
                   videoFrame is RawH264Frame && !(videoFrame is RawH264IFrame);
        }

        private static double ClampCrop(double value)
        {
            return Math.Max(0, Math.Min(0.95, value));
        }
    }
}
