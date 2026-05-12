using System;
using RtspClientSharp;

namespace SimpleRtspPlayer.GUI.Models
{
    interface IMainWindowModel
    {
        event EventHandler<string> StatusChanged;

        IVideoSource VideoSource { get; }
        bool HardwareAccelerationEnabled { get; set; }

        void Start(ConnectionParameters connectionParameters);
        void Stop();
    }
}
