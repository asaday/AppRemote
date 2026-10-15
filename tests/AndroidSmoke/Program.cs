using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AppRemote;
using AppRemote.Android;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

if (args.Length < 2) throw new ArgumentException("Usage: AndroidSmoke <adb.exe> <serial>");
var adb = args[0];
var serial = args[1];
var serverFile = await ScrcpyServerFile.GetAsync();
using var session = new RemoteSession();
await using var server = new RemoteServer(session, 18766);
AndroidSource? source = await AndroidSource.StartAsync(adb, serial, serverFile, VideoPreference.H264);
server.AndroidSourceChanged += next => source = next;
server.SetAndroidSource(source, codec => AndroidSource.StartAsync(adb, serial, serverFile, codec));
server.SetVideoPreference(VideoPreference.Auto);
try
{
    await server.StartAsync();
    var url = new UriBuilder(server.Url!) { Host = "127.0.0.1" }.Uri;
    using var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Origin", url.GetLeftPart(UriPartial.Authority));
    await socket.ConnectAsync(new UriBuilder(url) { Scheme = "ws", Path = "/ws" }.Uri, CancellationToken.None);
    var buffer = new byte[4096];
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var ready = await socket.ReceiveAsync(buffer, timeout.Token);
    using var readyJson = JsonDocument.Parse(buffer.AsMemory(0, ready.Count));
    var clientId = readyJson.RootElement.GetProperty("clientId").GetString();
    var version = readyJson.RootElement.GetProperty("version").GetInt32();
    using var http = new HttpClient();
    foreach (var codec in new[] { VideoCodecsEnum.H265, VideoCodecsEnum.H264 })
    {
        using var receiver = new RTCPeerConnection(null);
        receiver.addTrack(new MediaStreamTrack(
            Helper.GetSupportedVideoFormats().Where(f => f.Codec == codec).ToList(),
            MediaStreamStatusEnum.RecvOnly));
        var frames = 0;
        receiver.OnVideoFrameReceived += (_, _, _, _) => Interlocked.Increment(ref frames);
        await receiver.setLocalDescription(receiver.createOffer());
        for (var i = 0; i < 100 && receiver.iceGatheringState != RTCIceGatheringState.complete; i++)
            await Task.Delay(30);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(url, "/offer" + url.Query + "&client=" + clientId + "&version=" + version));
        request.Headers.TryAddWithoutValidation("Origin", url.GetLeftPart(UriPartial.Authority));
        request.Content = new StringContent(receiver.localDescription.sdp.ToString(), Encoding.UTF8, "application/sdp");
        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadAsStringAsync();
        if (receiver.setRemoteDescription(new RTCSessionDescriptionInit { sdp = answer, type = RTCSdpType.answer })
            != SetDescriptionResultEnum.OK) throw new Exception("WebRTC answer rejected.");
        for (var i = 0; i < 160 && Volatile.Read(ref frames) == 0; i++) await Task.Delay(50);
        if (frames == 0 || source?.Codec != (codec == VideoCodecsEnum.H265 ? VideoPreference.H265 : VideoPreference.H264))
            throw new Exception($"{codec} did not stream from the matching device encoder.");
        Console.WriteLine($"OK: Android {codec} WebRTC frames={frames}");
    }
    await socket.SendAsync(Encoding.UTF8.GetBytes("{\"type\":\"noop\"}"),
        WebSocketMessageType.Text, true, CancellationToken.None);
    if (source is not null)
    {
        await source.HandleInputAsync("{\"type\":\"key\",\"value\":\"Tab\"}");
        Console.WriteLine("OK: Android control channel accepted a key event");
    }
    if (args.Contains("--serve"))
    {
        Console.WriteLine("Browser check: " + url);
        Console.WriteLine("Press Enter to stop.");
        Console.ReadLine();
    }
}
finally
{
    server.SetAndroidSource(null);
    if (source is not null) await source.DisposeAsync();
}
