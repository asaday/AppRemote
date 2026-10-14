using System.IO;
using System.Net;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

namespace AppRemote;

internal sealed class VideoPeer : IDisposable
{
    private readonly RemoteSession _session;
    private readonly RTCPeerConnection _peer;
    private FFmpegVideoEncoder _encoder;
    private readonly VideoCodecsEnum _codec;
    private readonly object _gate = new();
    private (byte[] Bytes, int Width, int Height)? _pending;
    private readonly SemaphoreSlim _signal = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private bool _disposed;

    public event Action<bool>? ActiveChanged;
    public event Action<string>? Failed;
    public VideoPreference CodecMode { get; }

    public VideoPeer(RemoteSession session, VideoPreference codecMode, IPAddress? bindAddress = null)
    {
        if (codecMode is not (VideoPreference.H264 or VideoPreference.H265))
            throw new ArgumentOutOfRangeException(nameof(codecMode));
        _session = session;
        CodecMode = codecMode;
        _codec = codecMode == VideoPreference.H265 ? VideoCodecsEnum.H265 : VideoCodecsEnum.H264;
        FFmpegInit.Initialise(FfmpegLogLevelEnum.AV_LOG_ERROR, FindFfmpeg());
        _encoder = CreateEncoder();
        _peer = new RTCPeerConnection(new RTCConfiguration { X_BindAddress = bindAddress });
        var formats = _encoder.SupportedFormats.Where(f => f.Codec == _codec).ToList();
        if (formats.Count == 0) throw new InvalidOperationException($"{codecMode} is unavailable.");
        _peer.addTrack(new MediaStreamTrack(formats, MediaStreamStatusEnum.SendOnly));
        _peer.onconnectionstatechange += state =>
        {
            ActiveChanged?.Invoke(state == RTCPeerConnectionState.connected);
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed) Dispose();
        };
        _session.RawFrameReady += OnRawFrame;
        _worker = Task.Run(SendLoopAsync);
    }

    public async Task<string> AcceptOfferAsync(string offer)
    {
        var result = _peer.setRemoteDescription(new RTCSessionDescriptionInit { sdp = offer, type = RTCSdpType.offer });
        if (result != SetDescriptionResultEnum.OK) throw new InvalidOperationException($"WebRTC offer: {result}");
        await _peer.setLocalDescription(_peer.createAnswer());
        // Host candidates are gathered asynchronously after setting the local description.
        for (var i = 0; i < 100 && _peer.iceGatheringState != RTCIceGatheringState.complete; i++)
            await Task.Delay(30);
        return _peer.localDescription.sdp.ToString();
    }

    private void OnRawFrame(byte[] bytes, int width, int height)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = (bytes, width, height);
            if (_signal.CurrentCount == 0) _signal.Release();
        }
    }

    private async Task SendLoopAsync()
    {
        var emptyFrames = 0;
        var encodedWidth = 0;
        var encodedHeight = 0;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _signal.WaitAsync(_stop.Token);
                (byte[] Bytes, int Width, int Height)? frame;
                lock (_gate) { frame = _pending; _pending = null; }
                if (frame is null || _peer.connectionState != RTCPeerConnectionState.connected) continue;
                try
                {
                    var (pixels, width, height) = Scale(frame.Value.Bytes, frame.Value.Width, frame.Value.Height);
                    if (pixels.Length != checked(width * height * 4))
                        throw new InvalidOperationException("The captured frame has an invalid size.");
                    if (encodedWidth != 0 && (width != encodedWidth || height != encodedHeight))
                    {
                        // FFmpegVideoEncoder changes the dimensions of its existing native context
                        // without reopening it. A new context is required after a size change.
                        var next = CreateEncoder();
                        var previous = _encoder;
                        _encoder = next;
                        previous.Dispose();
                        emptyFrames = 0;
                    }
                    encodedWidth = width;
                    encodedHeight = height;
                    var encoded = _encoder.EncodeVideo(width, height, pixels, VideoPixelFormatsEnum.Bgra, _codec);
                    if (encoded is { Length: > 0 })
                    {
                        emptyFrames = 0;
                        _peer.SendVideo(80, encoded);
                    }
                    else if (++emptyFrames >= 30) throw new InvalidOperationException("The encoder returned no video frames.");
                }
                catch (Exception ex)
                {
                    Failed?.Invoke($"{CodecMode} encoding failed; using JPEG. {ex.Message}");
                    Dispose();
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static FFmpegVideoEncoder CreateEncoder() => new(new Dictionary<string, string>
    {
        ["preset"] = "ultrafast",
        ["tune"] = "zerolatency"
    });

    private static (byte[], int, int) Scale(byte[] source, int width, int height)
    {
        var factor = Math.Min(1.0, 1280.0 / Math.Max(width, height));
        var targetWidth = Math.Max(2, (int)(width * factor) & ~1);
        var targetHeight = Math.Max(2, (int)(height * factor) & ~1);
        if (targetWidth == width && targetHeight == height) return (source, width, height);
        var output = new byte[targetWidth * targetHeight * 4];
        for (var y = 0; y < targetHeight; y++)
        {
            var fromY = y * height / targetHeight;
            for (var x = 0; x < targetWidth; x++)
            {
                var from = (fromY * width + x * width / targetWidth) * 4;
                var to = (y * targetWidth + x) * 4;
                output[to] = source[from];
                output[to + 1] = source[from + 1];
                output[to + 2] = source[from + 2];
                output[to + 3] = source[from + 3];
            }
        }
        return (output, targetWidth, targetHeight);
    }

    private static string? FindFfmpeg()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WinGet", "Packages");
        if (!Directory.Exists(root)) return null;
        foreach (var package in Directory.EnumerateDirectories(root, "Gyan.FFmpeg.Shared_*"))
        foreach (var build in Directory.EnumerateDirectories(package, "ffmpeg-8.1*"))
        {
            var bin = Path.Combine(build, "bin");
            if (File.Exists(Path.Combine(bin, "avcodec-62.dll"))) return bin;
        }
        return null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _session.RawFrameReady -= OnRawFrame;
        _stop.Cancel();
        _peer.Close("closed");
        // The worker can still be encoding; release its encoder after it exits.
        _ = _worker.ContinueWith(_ => { _encoder.Dispose(); _signal.Dispose(); _stop.Dispose(); });
    }
}
