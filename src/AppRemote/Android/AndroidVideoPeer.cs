using System.Net;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;

namespace AppRemote.Android;

internal sealed class AndroidVideoPeer : IRemoteVideoPeer
{
    private readonly AndroidSource _source;
    private readonly RTCPeerConnection _peer;
    private long _lastPts = -1;
    private bool _disposed;

    public event Action<bool>? ActiveChanged;
    public event Action<string>? Failed;
    public VideoPreference CodecMode => _source.Codec;

    public AndroidVideoPeer(AndroidSource source, IPAddress? bindAddress)
    {
        _source = source;
        _peer = new RTCPeerConnection(new RTCConfiguration { X_BindAddress = bindAddress });
        var format = source.Codec == VideoPreference.H265
            ? new VideoFormat(VideoCodecsEnum.H265, 96, 90000)
            : new VideoFormat(VideoCodecsEnum.H264, 96, 90000, "packetization-mode=1;profile-level-id=42e01f");
        _peer.addTrack(new MediaStreamTrack(format, MediaStreamStatusEnum.SendOnly));
        _peer.onconnectionstatechange += state =>
        {
            ActiveChanged?.Invoke(state == RTCPeerConnectionState.connected);
            if (state is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed) Dispose();
        };
        _source.Frame += OnFrame;
        _source.Failed += OnFailed;
    }

    public async Task<string> AcceptOfferAsync(string offer)
    {
        var result = _peer.setRemoteDescription(new RTCSessionDescriptionInit { sdp = offer, type = RTCSdpType.offer });
        if (result != SetDescriptionResultEnum.OK) throw new InvalidOperationException($"WebRTC offer: {result}");
        await _peer.setLocalDescription(_peer.createAnswer());
        for (var i = 0; i < 100 && _peer.iceGatheringState != RTCIceGatheringState.complete; i++)
            await Task.Delay(30);
        return _peer.localDescription.sdp.ToString();
    }

    private void OnFrame(byte[] bytes, long pts, int width, int height)
    {
        if (_disposed || _peer.connectionState != RTCPeerConnectionState.connected) return;
        var duration = _lastPts < 0 ? 3000u : (uint)Math.Clamp((pts - _lastPts) * 90 / 1000, 1, 90000);
        _lastPts = pts;
        try { _peer.SendVideo(duration, bytes); }
        catch (Exception ex) { Failed?.Invoke(ex.Message); }
    }

    private void OnFailed(string message) => Failed?.Invoke(message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.Frame -= OnFrame;
        _source.Failed -= OnFailed;
        _peer.Close("closed");
    }
}
