using System.Net.WebSockets;
using System.Runtime.InteropServices;
using AppRemote;
using AppRemote.Windows;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;

var hwnd = GetForegroundWindow();
if (hwnd == 0)
{
    Console.WriteLine("SKIP: no foreground window");
    return 0;
}

GetWindowThreadProcessId(hwnd, out var pid);
using var session = new RemoteSession();
session.CaptureFaulted += error => Console.WriteLine("CAPTURE ERROR: " + error);
await using var server = new RemoteServer(session);
try
{
    session.Select(new WindowEntry(hwnd, "Smoke test", pid));
    await server.StartAsync();
    var url = new Uri(server.Url!);
    if (!url.Query.StartsWith("?key=", StringComparison.Ordinal) || url.Query.Length != 13)
        throw new Exception("Pairing key is not eight characters");
    var localUrl = new UriBuilder(url) { Host = "127.0.0.1" }.Uri;
    using var http = new HttpClient();
    var html = await http.GetStringAsync(localUrl);
    if (!html.Contains("id=\"screen\"")) throw new Exception("Web page missing screen element");
    if (!html.Contains("apple-touch-icon.png")) throw new Exception("Home screen icon missing from page");
    var icon = await http.GetByteArrayAsync(new Uri(localUrl, "/apple-touch-icon.png"));
    if (icon.Length < 8 || !icon.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        throw new Exception("Home screen icon is not a PNG");
    var manifest = await http.GetStringAsync(new Uri(localUrl, "/manifest.json" + url.Query));
    if (!manifest.Contains("icon-192.png") || !manifest.Contains("icon-512.png"))
        throw new Exception("Manifest icons missing");
    var legacyKeyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppRemote", "pairing.key");
    var legacyKey = File.ReadAllText(legacyKeyPath).Trim();
    using var legacyResponse = await http.GetAsync(new Uri(localUrl, "/manifest.json?key=" + legacyKey));
    if (!legacyResponse.IsSuccessStatusCode) throw new Exception("Existing home screen key was rejected");
    var unauthenticated = await http.GetAsync($"http://127.0.0.1:{localUrl.Port}/manifest.json");
    if (unauthenticated.StatusCode != System.Net.HttpStatusCode.Unauthorized)
        throw new Exception("Unauthenticated manifest was accepted");

    using var socket = new ClientWebSocket();
    socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{localUrl.Port}");
    var wsUri = new UriBuilder(localUrl) { Scheme = "ws", Path = "/ws" }.Uri;
    await socket.ConnectAsync(wsUri, CancellationToken.None);
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var buffer = new byte[1024 * 1024];
    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        if (result.MessageType == WebSocketMessageType.Binary)
        {
            Console.WriteLine($"OK: page served, unauthorized request rejected, socket authenticated, JPEG bytes={result.Count}");
            break;
        }
    }
    using var receiver = new RTCPeerConnection(null);
    receiver.addTrack(new MediaStreamTrack(
        Helper.GetSupportedVideoFormats().Where(f => f.Codec == VideoCodecsEnum.H264).ToList(),
        MediaStreamStatusEnum.RecvOnly));
    var receivedFrames = 0;
    receiver.OnVideoFrameReceived += (endpoint, timestamp, frame, format) => Interlocked.Increment(ref receivedFrames);
    await receiver.setLocalDescription(receiver.createOffer());
    for (var i = 0; i < 100 && receiver.iceGatheringState != RTCIceGatheringState.complete; i++)
        await Task.Delay(30);
    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(localUrl, "/offer?key=" + url.Query.TrimStart('?').Split('=')[1]));
    request.Headers.TryAddWithoutValidation("Origin", $"http://127.0.0.1:{localUrl.Port}");
    request.Content = new StringContent(receiver.localDescription.sdp.ToString(), System.Text.Encoding.UTF8, "application/sdp");
    using var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();
    var answer = await response.Content.ReadAsStringAsync();
    var accepted = receiver.setRemoteDescription(new RTCSessionDescriptionInit { sdp = answer, type = RTCSdpType.answer });
    if (accepted != SetDescriptionResultEnum.OK) throw new Exception("WebRTC answer rejected: " + accepted);
    for (var i = 0; i < 150 && Volatile.Read(ref receivedFrames) == 0; i++) await Task.Delay(50);
    if (receivedFrames == 0) throw new Exception("WebRTC connected but no H.264 frames arrived");
    Console.WriteLine($"OK: H.264 WebRTC frames={receivedFrames}");
    receiver.Close("done");
    socket.Abort();
    return 0;
}
catch (Exception ex)
{
    Console.WriteLine(ex);
    return 1;
}

[DllImport("user32.dll")]
static extern nint GetForegroundWindow();
[DllImport("user32.dll")]
static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
