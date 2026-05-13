using System;
using RtspClientSharp;
using RtspClientSharp.RawFrames;
using RtspClientSharp.RawFrames.Audio;
using SimpleRtspPlayer.RawFramesReceiving;

namespace SimpleRtspPlayer.GUI.Models
{
    class MainWindowModel : IMainWindowModel
    {
        private readonly RealtimeVideoSource _realtimeVideoSource = new RealtimeVideoSource();
        private readonly RealtimeAudioSource _realtimeAudioSource = new RealtimeAudioSource();

        private IRawFramesSource _rawFramesSource;
        private bool _audioSourceAttached;

        public event EventHandler<string> StatusChanged;

        public IVideoSource VideoSource => _realtimeVideoSource;
        public bool HardwareAccelerationEnabled
        {
            get => _realtimeVideoSource.HardwareAccelerationEnabled;
            set => _realtimeVideoSource.HardwareAccelerationEnabled = value;
        }

        public MainWindowModel()
        {
            _realtimeVideoSource.DecoderStatusChanged += DecoderStatusChanged;
        }

        public void Start(ConnectionParameters connectionParameters)
        {
            if (_rawFramesSource != null)
                return;

            _audioSourceAttached = false;
            _rawFramesSource = new RawFramesSource(connectionParameters);
            _rawFramesSource.ConnectionStatusChanged += ConnectionStatusChanged;
            _rawFramesSource.FrameReceived += DetectAudioFrame;

            _realtimeVideoSource.SetRawFramesSource(_rawFramesSource);

            _rawFramesSource.Start();
        }

        public void Stop()
        {
            if (_rawFramesSource == null)
                return;

            _rawFramesSource.Stop();
            _rawFramesSource.FrameReceived -= DetectAudioFrame;
            _realtimeVideoSource.SetRawFramesSource(null);
            _realtimeAudioSource.SetRawFramesSource(null);
            _audioSourceAttached = false;
            _rawFramesSource = null;
        }

        private void DetectAudioFrame(object sender, RawFrame rawFrame)
        {
            if (_audioSourceAttached || !(rawFrame is RawAudioFrame))
                return;

            _audioSourceAttached = true;
            _rawFramesSource.FrameReceived -= DetectAudioFrame;
            _realtimeAudioSource.SetRawFramesSource(_rawFramesSource);
            StatusChanged?.Invoke(this, "Audio detected; audio decoding enabled");
        }

        private void ConnectionStatusChanged(object sender, string s)
        {
            StatusChanged?.Invoke(this, s);
        }

        private void DecoderStatusChanged(object sender, string s)
        {
            StatusChanged?.Invoke(this, s);
        }
    }
}
