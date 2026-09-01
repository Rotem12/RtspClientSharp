# RtspClientSharp.WinForms

`RtspVideoControl` is the maintained WinForms video surface for
`RtspClientSharp`. It owns the receive, decode, latest-frame presentation, and
recording path for:

- RTSP (`rtsp://...` and RTSP-over-HTTP URLs)
- direct RTP over UDP
- raw MPEG-TS over UDP

For direct UDP, leave `TransportMode` as `Auto` to classify the first datagram
as RTP or MPEG-TS. Set it explicitly only when a sender needs a deterministic
format. Leave `VideoCodec` as `CodecInfoType.Auto` to identify direct RTP
H.264, H.265, and RFC 2435 MJPEG, or to read H.264/H.265/private-PES MJPEG
from an MPEG-TS program map. `SourceType=Auto` selects RTSP for RTSP URLs and
direct UDP otherwise.

```csharp
var video = new RtspVideoControl
{
    Dock = DockStyle.Fill,
    SourceType = VideoSourceType.Auto,
    TransportMode = MediaTransportMode.Auto,
    VideoCodec = RtspClientSharp.Codecs.Video.CodecInfoType.Auto,
    NoVideoImage = Properties.Resources.novideo,
    PipelineMode = VideoPipelineMode.HardwareDecodeWithCpuReadback,
    ConnectionParameters = new ConnectionParameters(
        new Uri("udp://239.1.1.10:24000"))
};

Controls.Add(video);
video.Start();
```

`NoVideoImage` is drawn on the empty/stopped surface and before the first
frame is available. After a successfully decoded frame, `NoVideoTimeout` defaults
to three seconds; a stalled stream then clears the stale surface, resets the
decoder, and waits for the next keyframe. The control does not dispose the assigned
image, so a resource image can be shared safely and the caller remains responsible
for disposing images it creates. `IsNoVideoActive` exposes the timeout state.

`PipelineMode.GpuD3D11EndToEnd` uses the native helper's D3D11 path and falls
back to a clean software decoder if the adapter/codec cannot use the complete
D3D11 path. Select `HardwareDecodeWithCpuReadback` explicitly when that
trade-off is preferred. The control keeps
the native decoder consuming every H.264 frame while limiting expensive
managed scaling and bitmap copies to the newest frame available for painting.
For cameras that signal constrained-baseline H.264 as ordinary Baseline, the
hardware path applies FFmpeg's constrained-baseline compatibility fallback
automatically. `IsHardwareDecodeActive`, `IsEndToEndGpuActive`, and
`GpuRenderedFrameCount` expose whether decoding and actual native GPU
presentation succeeded; `PresentedFrameCount` counts only WinForms/GDI paints.

Recording is independent of rendering. `StartRecording("capture.wma")` uses
`RecordingMode=Auto`, which selects the low-copy video-only MPEG-TS recorder and
creates `capture.ts`; it writes the compressed H.264/H.265 access units directly,
without decoding, bitmap allocation, or AForge/FFmpeg re-encoding. Use
`RecordingMode=VideoRecordingMode.CompressedRemux` to make that choice explicit.
Use an explicit `.h264` or `.h265` path, or set
`RecordingFormat=CompressedVideoRecordingFormat.AnnexB`, when a raw Annex-B file
is preferred. Set `PreRecordSeconds` before `Start()` to keep a rolling history
while the control is playing. `StartRecording` prepends that history:
compressed remux uses copied encoded access units and starts at the nearest
decodable keyframe; `BitmapFallback` uses decoded bitmap frames. Setting it to
zero disables the rolling buffer, and changing it while playing takes effect
on the next start.

For compatibility with bitmap encoders such as AForge, set
`RecordingMode=VideoRecordingMode.BitmapFallback` and provide
`BitmapRecorderFactory`. The factory receives the decoded frame size, frame rate,
and bit rate, and its recorder receives each bitmap synchronously. Set
`RecordNoVideoImage=true` to insert the configured `NoVideoImage` at a timeout
gap and when recording stops during that gap. This mode is intentionally more
expensive and is not used by default; the control itself has no AForge dependency.
For legacy applications that generated a frame programmatically, assign
`NoVideoFrameFactory`; returned bitmaps are disposed after the recorder consumes
them.

The native `libffmpeghelper.dll` must be deployed beside the application and
must match the process architecture (`x86` or `x64`). The WinForms project
copies it from `Examples/libffmpeghelper` when that helper has been built.

Direct RTP has no SDP exchange. If an H.264 sender never sends SPS/PPS in-band,
provide Annex-B SPS/PPS through `H264SpsPpsBytes` before `Start`; for the same
case with H.265, use `H265VpsSpsPpsBytes`. There is no metadata in the RTP
payload from which missing parameter sets can be reconstructed. When the stream
repeats its parameter sets at an IDR, leave the corresponding property empty.
`DetectedVideoCodec` reports the selected codec after the first direct RTP
probe or MPEG-TS PMT.

`FrameDecoded` is raised from the receive/decoder worker, not the WinForms UI
thread. Marshal to the UI thread when updating controls. The diagnostic
counters (`TransportDatagramCount`, `TransportFrameCount`,
`TransportDroppedFrameCount`, `ReceivedVideoFrameCount`,
`NativeDecodedFrameCount`, `DecodedFrameCount`, `DroppedFrameCount`, and
`PresentedFrameCount`, plus `IsHardwareDecodeActive`, `IsEndToEndGpuActive`,
`GpuRenderedFrameCount`, and `GpuSkippedFrameCount`) can be used to separate
socket/parser delivery, dispatcher loss, decoder failure, GPU presentation,
and intentional display throttling. GPU rendering honors `RenderIntervalMs`;
decoded frames skipped by that pacing are counted by `GpuSkippedFrameCount`.

For an intermittent display or GPU presentation issue, enable the opt-in trace
before starting the application. Set `RTSPCLIENTSHARP_TRACE` to a writable log
file path, or set it to `1` to use `%TEMP%\RtspClientSharp\rtsp-video-trace.log`.
The trace records the loaded WinForms assembly, pipeline transitions, control and
GPU-surface sizes/visibility, resize and paint-state transitions, decoded/GPU
counters, and native decode/render result codes. It is disabled by default and
diagnostic failures are ignored so tracing cannot affect playback.
