using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using RtspClientSharp.RawFrames.Video;

namespace RtspClientSharp.WinForms
{
    internal enum FfmpegVideoCodecId
    {
        Mjpeg = 7,
        H264 = 27,
        Hevc = 173
    }

    [Flags]
    internal enum FfmpegScalingQuality
    {
        FastBilinear = 1,
        Bilinear = 2,
        Bicubic = 4,
        Point = 0x10,
        Area = 0x20
    }

    internal enum FfmpegPixelFormat
    {
        None = -1,
        Yuv420P = 0,
        Yuyv422 = 1,
        Rgb24 = 2,
        Bgr24 = 3,
        Yuv422P = 4,
        Yuv444P = 5,
        Yuv410P = 6,
        Yuv411P = 7,
        Gray8 = 8,
        Argb = 27,
        Rgba = 28,
        Abgr = 29,
        Bgra = 30
    }

    internal static class FfmpegVideoPInvoke
    {
        private const string LibraryName = "libffmpeghelper.dll";

        [DllImport(LibraryName, EntryPoint = "create_video_decoder", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CreateVideoDecoder(FfmpegVideoCodecId codecId, out IntPtr handle);

        [DllImport(LibraryName, EntryPoint = "create_video_decoder_with_options", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CreateVideoDecoderWithOptions(FfmpegVideoCodecId codecId,
            int preferHardwareAcceleration, out IntPtr handle);

        [DllImport(LibraryName, EntryPoint = "remove_video_decoder", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void RemoveVideoDecoder(IntPtr handle);

        [DllImport(LibraryName, EntryPoint = "set_video_decoder_extradata", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetVideoDecoderExtraData(IntPtr handle, IntPtr extraData, int extraDataLength);

        [DllImport(LibraryName, EntryPoint = "decode_video_frame", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int DecodeFrame(IntPtr handle, IntPtr rawBuffer, int rawBufferLength,
            out int frameWidth, out int frameHeight, out FfmpegPixelFormat framePixelFormat);

        [DllImport(LibraryName, EntryPoint = "decode_video_frame_padded", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int DecodeFramePadded(IntPtr handle, IntPtr rawBuffer, int rawBufferLength,
            out int frameWidth, out int frameHeight, out FfmpegPixelFormat framePixelFormat);

        [DllImport(LibraryName, EntryPoint = "is_video_decoder_hardware_accelerated", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int IsVideoDecoderHardwareAccelerated(IntPtr handle);

        [DllImport(LibraryName, EntryPoint = "set_video_decoder_render_target", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int SetVideoDecoderRenderTarget(IntPtr handle, IntPtr hwnd);

        [DllImport(LibraryName, EntryPoint = "decode_video_frame_to_gpu", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int DecodeFrameToGpu(IntPtr handle, IntPtr rawBuffer, int rawBufferLength,
            out int frameWidth, out int frameHeight, out FfmpegPixelFormat framePixelFormat);

        [DllImport(LibraryName, EntryPoint = "decode_video_frame_to_gpu_padded", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int DecodeFrameToGpuPadded(IntPtr handle, IntPtr rawBuffer, int rawBufferLength,
            out int frameWidth, out int frameHeight, out FfmpegPixelFormat framePixelFormat);

        [DllImport(LibraryName, EntryPoint = "render_gpu_decoded_video_frame", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int RenderGpuDecodedFrame(IntPtr handle, double cropLeft, double cropTop,
            double cropRight, double cropBottom);

        [DllImport(LibraryName, EntryPoint = "scale_decoded_video_frame", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int ScaleDecodedVideoFrame(IntPtr handle, IntPtr scalerHandle, IntPtr scaledBuffer,
            int scaledBufferStride);

        [DllImport(LibraryName, EntryPoint = "create_video_scaler", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CreateVideoScaler(int sourceLeft, int sourceTop, int sourceWidth, int sourceHeight,
            FfmpegPixelFormat sourcePixelFormat, int scaledWidth, int scaledHeight,
            FfmpegPixelFormat scaledPixelFormat, FfmpegScalingQuality qualityFlags, out IntPtr handle);

        [DllImport(LibraryName, EntryPoint = "remove_video_scaler", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void RemoveVideoScaler(IntPtr handle);
    }

    internal sealed class DecodedVideoFrameParameters
    {
        public DecodedVideoFrameParameters(int width, int height, FfmpegPixelFormat pixelFormat)
        {
            Width = width;
            Height = height;
            PixelFormat = pixelFormat;
        }

        public int Width { get; }
        public int Height { get; }
        public FfmpegPixelFormat PixelFormat { get; }
    }

    internal sealed class VideoTransformParameters : IEquatable<VideoTransformParameters>
    {
        public VideoTransformParameters(RectangleF regionOfInterest, Size targetFrameSize,
            VideoScaleMode scaleMode)
        {
            RegionOfInterest = regionOfInterest;
            TargetFrameSize = targetFrameSize;
            ScaleMode = scaleMode;
        }

        public RectangleF RegionOfInterest { get; }
        public Size TargetFrameSize { get; }
        public VideoScaleMode ScaleMode { get; }

        public bool Equals(VideoTransformParameters other)
        {
            if (ReferenceEquals(other, null))
                return false;

            return RegionOfInterest.Equals(other.RegionOfInterest) &&
                   TargetFrameSize.Equals(other.TargetFrameSize) &&
                   ScaleMode == other.ScaleMode;
        }

        public override bool Equals(object obj)
        {
            return Equals(obj as VideoTransformParameters);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = RegionOfInterest.GetHashCode();
                hash = (hash * 397) ^ TargetFrameSize.GetHashCode();
                hash = (hash * 397) ^ (int)ScaleMode;
                return hash;
            }
        }
    }

    internal sealed class FfmpegVideoScaler : IDisposable
    {
        private const double MaxAspectRatioError = 0.1;
        private bool _disposed;

        private FfmpegVideoScaler(IntPtr handle, int scaledWidth, int scaledHeight)
        {
            Handle = handle;
            ScaledWidth = scaledWidth;
            ScaledHeight = scaledHeight;
            ScaledStride = GetStride(scaledWidth);
        }

        public IntPtr Handle { get; }
        public int ScaledWidth { get; }
        public int ScaledHeight { get; }
        public int ScaledStride { get; }

        public static FfmpegVideoScaler Create(DecodedVideoFrameParameters source,
            VideoTransformParameters transform)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));
            if (transform == null)
                throw new ArgumentNullException(nameof(transform));

            int sourceLeft = 0;
            int sourceTop = 0;
            int sourceWidth = source.Width;
            int sourceHeight = source.Height;

            if (!transform.RegionOfInterest.IsEmpty)
            {
                sourceLeft = Clamp((int)(source.Width * transform.RegionOfInterest.Left), 0, source.Width - 1);
                sourceTop = Clamp((int)(source.Height * transform.RegionOfInterest.Top), 0, source.Height - 1);
                sourceWidth = Clamp((int)(source.Width * transform.RegionOfInterest.Width), 1,
                    source.Width - sourceLeft);
                sourceHeight = Clamp((int)(source.Height * transform.RegionOfInterest.Height), 1,
                    source.Height - sourceTop);
            }

            int scaledWidth = sourceWidth;
            int scaledHeight = sourceHeight;
            if (!transform.TargetFrameSize.IsEmpty)
            {
                scaledWidth = Math.Max(2, transform.TargetFrameSize.Width);
                scaledHeight = Math.Max(2, transform.TargetFrameSize.Height);

                if (transform.ScaleMode == VideoScaleMode.RespectAspectRatio)
                {
                    float sourceAspect = (float)sourceWidth / sourceHeight;
                    float targetAspect = (float)scaledWidth / scaledHeight;
                    if (Math.Abs(sourceAspect - targetAspect) / sourceAspect > MaxAspectRatioError)
                    {
                        if (targetAspect < sourceAspect)
                            scaledHeight = Math.Max(2, sourceHeight * scaledWidth / sourceWidth);
                        else
                            scaledWidth = Math.Max(2, sourceWidth * scaledHeight / sourceHeight);
                    }
                }
            }

            scaledWidth = MakeEven(scaledWidth);
            scaledHeight = MakeEven(scaledHeight);

            int resultCode = FfmpegVideoPInvoke.CreateVideoScaler(sourceLeft, sourceTop, sourceWidth, sourceHeight,
                source.PixelFormat, scaledWidth, scaledHeight, FfmpegPixelFormat.Bgr24,
                FfmpegScalingQuality.FastBilinear, out IntPtr handle);
            if (resultCode != 0)
                throw new DecoderException($"An error occurred while creating video scaler, code: {resultCode}");

            return new FfmpegVideoScaler(handle, scaledWidth, scaledHeight);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            FfmpegVideoPInvoke.RemoveVideoScaler(Handle);
            GC.SuppressFinalize(this);
        }

        ~FfmpegVideoScaler()
        {
            Dispose();
        }

        internal static int GetStride(int width)
        {
            return ((width * 24 + 31) & ~31) >> 3;
        }

        private static int MakeEven(int value)
        {
            return Math.Max(2, value & ~1);
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }

    internal sealed class FfmpegVideoDecoder : IDisposable
    {
        private readonly IntPtr _decoderHandle;
        private readonly FfmpegVideoCodecId _codecId;
        private readonly Dictionary<VideoTransformParameters, FfmpegVideoScaler> _scalers =
            new Dictionary<VideoTransformParameters, FfmpegVideoScaler>();
        private byte[] _extraData = Array.Empty<byte>();
        private DecodedVideoFrameParameters _currentFrameParameters;
        private bool _isHardwareAccelerated;
        private bool _disposed;

        private FfmpegVideoDecoder(FfmpegVideoCodecId codecId, IntPtr decoderHandle)
        {
            _codecId = codecId;
            _decoderHandle = decoderHandle;
            RefreshHardwareAcceleration();
        }

        public int LastGpuDecodeResult { get; private set; }
        public int LastGpuRenderResult { get; private set; }
        public bool IsHardwareAccelerated => _isHardwareAccelerated;

        public static FfmpegVideoDecoder Create(FfmpegVideoCodecId codecId, bool preferHardwareAcceleration)
        {
            int resultCode;
            IntPtr decoderHandle;
            if (preferHardwareAcceleration)
                resultCode = FfmpegVideoPInvoke.CreateVideoDecoderWithOptions(codecId, 1, out decoderHandle);
            else
                resultCode = FfmpegVideoPInvoke.CreateVideoDecoder(codecId, out decoderHandle);

            if (resultCode != 0)
                throw new DecoderException($"An error occurred while creating video decoder for {codecId}, code: {resultCode}");

            return new FfmpegVideoDecoder(codecId, decoderHandle);
        }

        public unsafe bool TryDecode(RawVideoFrame rawVideoFrame,
            out DecodedVideoFrameParameters frameParameters)
        {
            SetExtraDataIfNeeded(rawVideoFrame);
            ArraySegment<byte> segment = rawVideoFrame?.FrameSegment ?? default(ArraySegment<byte>);
            if (segment.Array == null || segment.Count <= 0)
            {
                frameParameters = null;
                return false;
            }

            int resultCode;
            int width;
            int height;
            FfmpegPixelFormat pixelFormat;
            fixed (byte* rawBufferPointer = &segment.Array[segment.Offset])
            {
                resultCode = rawVideoFrame.HasDecoderInputPadding
                    ? FfmpegVideoPInvoke.DecodeFramePadded(_decoderHandle, (IntPtr)rawBufferPointer,
                        segment.Count, out width, out height, out pixelFormat)
                    : FfmpegVideoPInvoke.DecodeFrame(_decoderHandle, (IntPtr)rawBufferPointer,
                        segment.Count, out width, out height, out pixelFormat);
            }

            if (resultCode != 0)
            {
                RefreshHardwareAcceleration();
                frameParameters = null;
                return false;
            }

            UpdateFrameParameters(width, height, pixelFormat);
            frameParameters = _currentFrameParameters;
            return true;
        }

        public unsafe bool TryDecodeToGpu(RawVideoFrame rawVideoFrame,
            out DecodedVideoFrameParameters frameParameters)
        {
            SetExtraDataIfNeeded(rawVideoFrame);
            ArraySegment<byte> segment = rawVideoFrame?.FrameSegment ?? default(ArraySegment<byte>);
            if (segment.Array == null || segment.Count <= 0)
            {
                LastGpuDecodeResult = -1;
                frameParameters = null;
                return false;
            }

            int resultCode;
            int width;
            int height;
            FfmpegPixelFormat pixelFormat;
            fixed (byte* rawBufferPointer = &segment.Array[segment.Offset])
            {
                resultCode = rawVideoFrame.HasDecoderInputPadding
                    ? FfmpegVideoPInvoke.DecodeFrameToGpuPadded(_decoderHandle, (IntPtr)rawBufferPointer,
                        segment.Count, out width, out height, out pixelFormat)
                    : FfmpegVideoPInvoke.DecodeFrameToGpu(_decoderHandle, (IntPtr)rawBufferPointer,
                        segment.Count, out width, out height, out pixelFormat);
            }

            LastGpuDecodeResult = resultCode;
            if (resultCode != 0)
            {
                RefreshHardwareAcceleration();
                frameParameters = null;
                return false;
            }

            UpdateFrameParameters(width, height, pixelFormat);
            frameParameters = _currentFrameParameters;
            return true;
        }

        public void SetRenderTarget(IntPtr hwnd)
        {
            int resultCode = FfmpegVideoPInvoke.SetVideoDecoderRenderTarget(_decoderHandle, hwnd);
            if (resultCode != 0)
                throw new DecoderException($"An error occurred while setting video render target for {_codecId}, code: {resultCode}");
        }

        public void RenderGpuFrame(double cropLeft, double cropTop, double cropRight, double cropBottom)
        {
            int resultCode = FfmpegVideoPInvoke.RenderGpuDecodedFrame(_decoderHandle, cropLeft, cropTop,
                cropRight, cropBottom);
            LastGpuRenderResult = resultCode;
            if (resultCode != 0)
                throw new DecoderException($"An error occurred while rendering GPU video frame for {_codecId}, code: {resultCode}");
        }

        public FfmpegVideoScaler GetScaler(VideoTransformParameters transform)
        {
            if (!_scalers.TryGetValue(transform, out FfmpegVideoScaler scaler))
            {
                scaler = FfmpegVideoScaler.Create(_currentFrameParameters, transform);
                _scalers.Add(transform, scaler);
            }

            return scaler;
        }

        public void ScaleTo(FfmpegVideoScaler scaler, byte[] destination)
        {
            if (scaler == null)
                throw new ArgumentNullException(nameof(scaler));
            if (destination == null || destination.Length < scaler.ScaledStride * scaler.ScaledHeight)
                throw new ArgumentException("The destination buffer is too small.", nameof(destination));

            unsafe
            {
                fixed (byte* destinationPointer = destination)
                {
                    ScaleTo(scaler, (IntPtr)destinationPointer, scaler.ScaledStride);
                }
            }
        }

        public void ScaleTo(FfmpegVideoScaler scaler, IntPtr destination, int destinationStride)
        {
            if (scaler == null)
                throw new ArgumentNullException(nameof(scaler));
            if (destination == IntPtr.Zero || destinationStride == 0)
                throw new ArgumentException("A valid destination buffer is required.", nameof(destination));

            int resultCode = FfmpegVideoPInvoke.ScaleDecodedVideoFrame(_decoderHandle, scaler.Handle,
                destination, destinationStride);
            if (resultCode != 0)
                throw new DecoderException($"An error occurred while scaling video frame for {_codecId}, code: {resultCode}");
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            foreach (FfmpegVideoScaler scaler in _scalers.Values)
                scaler.Dispose();
            _scalers.Clear();
            FfmpegVideoPInvoke.RemoveVideoDecoder(_decoderHandle);
            GC.SuppressFinalize(this);
        }

        ~FfmpegVideoDecoder()
        {
            Dispose();
        }

        private unsafe void SetExtraDataIfNeeded(RawVideoFrame rawVideoFrame)
        {
            ArraySegment<byte> extraDataSegment = default(ArraySegment<byte>);
            if (rawVideoFrame is RawH264IFrame h264IFrame)
                extraDataSegment = h264IFrame.SpsPpsSegment;
            else if (rawVideoFrame is RawH265IFrame h265IFrame)
                extraDataSegment = h265IFrame.ParametersBytesSegment;

            if (extraDataSegment.Array == null || extraDataSegment.Count == 0 ||
                SegmentEquals(_extraData, extraDataSegment))
                return;

            _extraData = new byte[extraDataSegment.Count];
            Buffer.BlockCopy(extraDataSegment.Array, extraDataSegment.Offset, _extraData, 0, extraDataSegment.Count);

            fixed (byte* extraDataPointer = _extraData)
            {
                int resultCode = FfmpegVideoPInvoke.SetVideoDecoderExtraData(_decoderHandle,
                    (IntPtr)extraDataPointer, _extraData.Length);
                if (resultCode != 0)
                    throw new DecoderException($"An error occurred while setting video extra data for {_codecId}, code: {resultCode}");

                RefreshHardwareAcceleration();
            }
        }

        private void RefreshHardwareAcceleration()
        {
            _isHardwareAccelerated = FfmpegVideoPInvoke.IsVideoDecoderHardwareAccelerated(_decoderHandle) != 0;
        }

        private void UpdateFrameParameters(int width, int height, FfmpegPixelFormat pixelFormat)
        {
            if (_currentFrameParameters == null || _currentFrameParameters.Width != width ||
                _currentFrameParameters.Height != height || _currentFrameParameters.PixelFormat != pixelFormat)
            {
                _currentFrameParameters = new DecodedVideoFrameParameters(width, height, pixelFormat);
                foreach (FfmpegVideoScaler scaler in _scalers.Values)
                    scaler.Dispose();
                _scalers.Clear();
            }
        }

        private static bool SegmentEquals(byte[] left, ArraySegment<byte> right)
        {
            if (left == null || right.Array == null || left.Length != right.Count)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right.Array[right.Offset + i])
                    return false;
            }

            return true;
        }
    }
}
