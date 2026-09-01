# ![Logo](Images/package_icon.png) C# RTSP Client for .NET

[![NuGet version (RtspClientSharp)](https://img.shields.io/nuget/v/RtspClientSharp.svg?style=flat-square)](https://www.nuget.org/packages/RtspClientSharp/)
[![Build Status](https://travis-ci.org/BogdanovKirill/RtspClientSharp.svg?branch=master)](https://travis-ci.org/BogdanovKirill/RtspClientSharp.svg?branch=master)

This repo contains C# RTSP client implementation (called "RtspClientSharp") for .NET Standard 2.0
## Features
- Supported transport protocols: TCP/HTTP/UDP
- Supported media codecs: H.264/H.265/MJPEG/AAC/G711A/G711U/PCM/G726
- No external dependencies, pure C# code
- Asynchronous nature with cancellation tokens support
- Designed to be fast and scaleable
- Low GC pressure during receive stage (when RTSP session is established); low amount of freezes under high load

## Installation 

The easiest way to get started is to use the NuGet package for 
RtspClientSharp

You can install using NuGet like this:

```cmd
nuget install RtspClientSharp
```

Or select it from the NuGet packages UI on Visual Studio.

On Visual Studio, make sure that you are targeting .NET 4.6.1/.NET Core 2.0 or
later, as this package uses some features of newer .NETs.  Otherwise,
the package will not be added. Once you do this, you can just use the
RtspClientSharp nuget

Alternatively, you can [download it](https://www.nuget.org/packages/RtspClientSharp/) directly.

## Using RtspClientSharp
Something like this:

```csharp
var serverUri = new Uri("rtsp://192.168.1.77:554/ucast/11");
var credentials = new NetworkCredential("admin", "123456");
var connectionParameters = new ConnectionParameters(serverUri, credentials);
connectionParameters.RtpTransport = RtpTransportProtocol.TCP;
using(var rtspClient = new RtspClient(connectionParameters))
{
    rtspClient.FrameReceived += (sender, frame) =>
    {
        //process (e.g. decode/save to file) encoded frame here or 
        //make deep copy to use it later because frame buffer (see FrameSegment property) will be reused by client
        switch (frame)
        {
            case RawH264IFrame h264IFrame:
            case RawH264PFrame h264PFrame:
            case RawH265IFrame h265IFrame:
            case RawH265PFrame h265PFrame:
            case RawJpegFrame jpegFrame:
            case RawAACFrame aacFrame:
            case RawG711AFrame g711AFrame:
            case RawG711UFrame g711UFrame:
            case RawPCMFrame pcmFrame:
            case RawG726Frame g726Frame:
               break;
        }
    }
	
    await rtspClient.ConnectAsync(token);
    await rtspClient.ReceiveAsync(token);
}
```
You could find more complex samples here (e.g. simple [RTSP player](https://github.com/BogdanovKirill/RtspClientSharp/tree/master/Examples/SimpleRtspPlayer) with full frame decoding and rendering processes):
https://github.com/BogdanovKirill/RtspClientSharp/tree/master/Examples

## WinForms video control

The Windows-specific `RtspClientSharp.WinForms` project provides a reusable
`RtspVideoControl` for RTSP, direct RTP-over-UDP, and raw MPEG-TS-over-UDP.
Use `SourceType=Auto`, `TransportMode=MediaTransportMode.Auto`, and leave
`VideoCodec=CodecInfoType.Auto` to select the input, sniff RTP versus MPEG-TS,
and identify direct RTP H.264/H.265/MJPEG automatically. Raw MPEG-TS selects
H.264, H.265, or private-PES Motion JPEG from the program map. The control uses a bounded
latest-frame display buffer, supports software/D3D11 pipelines, and records
H.264/H.265 directly into video-only MPEG-TS without decoding or AForge. Use an
explicit `.h264` or `.h265` output path when a raw Annex-B elementary stream is
required. Set `VideoRecordingMode.BitmapFallback` with a
`BitmapRecorderFactory` only when compatibility with a bitmap encoder such as
AForge is required.

See [the WinForms control README](RtspClientSharp.WinForms/README.md) for
deployment and direct-RTP SPS/PPS details. The native
`Examples/libffmpeghelper/libffmpeghelper.dll` must be deployed beside the
application for decoding and must match its x86/x64 architecture.

## Donation
If this project help you reduce time to develop, you can give me a cup of coffee :) 
You could also make per-feature donations, write me to bogdanov.kv@bk.ru

[![paypal](https://www.paypalobjects.com/en_US/i/btn/btn_donateCC_LG.gif)](https://paypal.me/bogdanovkv)

## Backers
Thank you to all project backers! Especially for:
- Roman Minyaylov
- Dat Ta
- Michael Norman

## Sponsors
[![jetbrains](https://github.com/JetBrains/logos/blob/master/web/jetbrains/jetbrains.svg)](https://www.jetbrains.com/?from=RtspClientSharp)
