# RtspClientSharp.WinForms

`RtspVideoControl` is the maintained WinForms video surface for
`RtspClientSharp`. It owns the receive, decode, latest-frame presentation, and
recording path for:

- RTSP (`rtsp://...` and RTSP-over-HTTP URLs)
- direct RTP over UDP
- raw MPEG-TS over UDP

For direct UDP, leave `TransportMode` as `Auto` to classify the first datagram
as RTP or MPEG-TS. Set it explicitly only when a sender needs a deterministic
format. `SourceType=Auto` selects RTSP for RTSP URLs and direct UDP otherwise.

```csharp
var video = new RtspVideoControl
{
    Dock = DockStyle.Fill,
    SourceType = VideoSourceType.Auto,
    TransportMode = MediaTransportMode.Auto,
    PipelineMode = VideoPipelineMode.HardwareDecodeWithCpuReadback,
    ConnectionParameters = new ConnectionParameters(
        new Uri("udp://239.1.1.10:24000"))
};

Controls.Add(video);
video.Start();
```

`PipelineMode.GpuD3D11EndToEnd` uses the native helper's D3D11 path and falls
back to a clean software decoder if the adapter/codec cannot use the complete
D3D11 path. Select `HardwareDecodeWithCpuReadback` explicitly when that
trade-off is preferred. The control keeps
the native decoder consuming every H.264 frame while limiting expensive
managed scaling and bitmap copies to the newest frame available for painting.

Recording is independent of rendering. `StartRecording("capture.wma")` selects
the low-copy video-only MPEG-TS recorder and creates `capture.ts`; it writes the
compressed H.264/H.265 access units directly, without decoding, bitmap
allocation, or AForge/FFmpeg re-encoding. Use an explicit `.h264` or `.h265`
path, or set `RecordingFormat=CompressedVideoRecordingFormat.AnnexB`, when a
raw Annex-B file is preferred. The optional `PreRecordSeconds` buffer stores
compressed frames and starts at a decodable keyframe.

The native `libffmpeghelper.dll` must be deployed beside the application and
must match the process architecture (`x86` or `x64`). The WinForms project
copies it from `Examples/libffmpeghelper` when that helper has been built.

Direct RTP has no SDP exchange. If an H.264 sender never sends SPS/PPS in-band,
provide Annex-B SPS/PPS through `H264SpsPpsBytes` before `Start`; there is no
metadata in the RTP payload from which a missing parameter set can be
reconstructed. When the stream repeats SPS/PPS at an IDR, leave the property
empty.

`FrameDecoded` is raised from the receive/decoder worker, not the WinForms UI
thread. Marshal to the UI thread when updating controls. The diagnostic
counters (`TransportDatagramCount`, `TransportFrameCount`,
`TransportDroppedFrameCount`, `ReceivedVideoFrameCount`,
`NativeDecodedFrameCount`, `DecodedFrameCount`, `DroppedFrameCount`, and
`PresentedFrameCount`) can be used to separate socket/parser delivery,
dispatcher loss, decoder failure, and intentional display throttling.
