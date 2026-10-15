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
if (args.Contains("--idle-only", StringComparer.OrdinalIgnoreCase))
{
    await RunIdleAndroidSmokeAsync(adb, serial);
    return;
}
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

static async Task RunIdleAndroidSmokeAsync(string adb, string serial)
{
    var serverFile = await ScrcpyServerFile.GetAsync();
    using var session = new RemoteSession();
    await using var server = new RemoteServer(session, 18768);
    var starts = 0;
    server.SetAndroidSelection(serial, codec =>
    {
        Interlocked.Increment(ref starts);
        return AndroidSource.StartAsync(adb, serial, serverFile, codec);
    });
    await server.StartAsync();
    if (starts != 0 || server.CurrentAndroidSource is not null)
        throw new Exception("Android encoder started without a browser");

    var url = new UriBuilder(server.Url!) { Host = "127.0.0.1" }.Uri;
    var origin = url.GetLeftPart(UriPartial.Authority);
    var wsUri = new UriBuilder(url) { Scheme = "ws", Path = "/ws" }.Uri;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    using var http = new HttpClient();
    async Task ConnectAndReceiveAsync()
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", origin);
        await socket.ConnectAsync(wsUri, timeout.Token);
        var buffer = new byte[4096];
        var ready = await socket.ReceiveAsync(buffer, timeout.Token);
        using var json = JsonDocument.Parse(buffer.AsMemory(0, ready.Count));
        if (json.RootElement.GetProperty("source").GetString() != "Android")
            throw new Exception("Selected Android source was not announced");
        var clientId = json.RootElement.GetProperty("clientId").GetString();
        var version = json.RootElement.GetProperty("version").GetInt32();
        using var receiver = new RTCPeerConnection(null);
        receiver.addTrack(new MediaStreamTrack(
            Helper.GetSupportedVideoFormats().Where(f => f.Codec == VideoCodecsEnum.H264).ToList(),
            MediaStreamStatusEnum.RecvOnly));
        var frames = 0;
        receiver.OnVideoFrameReceived += (_, _, _, _) => Interlocked.Increment(ref frames);
        await receiver.setLocalDescription(receiver.createOffer());
        for (var i = 0; i < 100 && receiver.iceGatheringState != RTCIceGatheringState.complete; i++)
            await Task.Delay(30);
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(url, "/offer" + url.Query + "&client=" + clientId + "&version=" + version));
        request.Headers.TryAddWithoutValidation("Origin", origin);
        request.Content = new StringContent(receiver.localDescription.sdp.ToString(), Encoding.UTF8, "application/sdp");
        using var response = await http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        var answer = await response.Content.ReadAsStringAsync(timeout.Token);
        if (receiver.setRemoteDescription(new RTCSessionDescriptionInit { sdp = answer, type = RTCSdpType.answer })
            != SetDescriptionResultEnum.OK) throw new Exception("WebRTC answer rejected");
        for (var i = 0; i < 160 && Volatile.Read(ref frames) == 0; i++) await Task.Delay(50);
        if (frames == 0 || server.CurrentAndroidSource?.Codec != VideoPreference.H264)
            throw new Exception("Android video did not start on connection");
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnClientChanged(bool connected) { if (!connected) disconnected.TrySetResult(); }
        server.ClientChanged += OnClientChanged;
        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        await disconnected.Task.WaitAsync(timeout.Token);
        server.ClientChanged -= OnClientChanged;
        receiver.Close("done");
        if (server.CurrentAndroidSource is not null)
            throw new Exception("Android encoder kept running without a browser");
    }

    await ConnectAndReceiveAsync();
    await ConnectAndReceiveAsync();
    if (starts != 2) throw new Exception($"Android encoder started {starts} times instead of twice");
    Console.WriteLine("OK: Android encoder starts on connection and stops on disconnect");
}
