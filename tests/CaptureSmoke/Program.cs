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
var codecSettings = new AppSettings { VideoMode = VideoPreference.H265, Port = 18765 };
var settingsRoundTrip = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
    System.Text.Json.JsonSerializer.Serialize(codecSettings));
if (settingsRoundTrip?.VideoMode != VideoPreference.H265 || settingsRoundTrip.Port != 18765)
    throw new Exception("Video preference or port was not preserved by settings serialization");
var settings = new AppSettings();
var remembered = new WindowEntry(hwnd, "Saved title", pid);
if (!settings.Remember(remembered)) throw new Exception("Could not identify the window for restore");
if (settings.FindLastWindow(new[] { remembered }) != remembered)
    throw new Exception("Saved window was not matched");
var renamed = remembered with { Title = "Renamed" };
if (settings.FindLastWindow(new[] { renamed }) != renamed)
    throw new Exception("Unique app window was not restored after a title change");
if (settings.FindLastWindow(new[] { renamed, remembered with { Title = "Another" } }) is not null)
    throw new Exception("Ambiguous app windows must not be restored");
settings.Forget();
if (settings.FindLastWindow(new[] { remembered }) is not null)
    throw new Exception("Stop must clear the restore target");
Console.WriteLine("OK: remembered window match and ambiguity check");
using var session = new RemoteSession();
session.CaptureFaulted += error => Console.WriteLine("CAPTURE ERROR: " + error);
await using var server = new RemoteServer(session, 18765);
try
{
    session.Select(new WindowEntry(hwnd, "Smoke test", pid));
    await server.StartAsync();
    if (server.ConnectionOptions.Count == 0 || server.ConnectionOptions[0].Url != server.Url)
        throw new Exception("Connection address menu is empty or has no default URL");
    var url = new Uri(server.Url!);
    if (!url.Query.StartsWith("?key=", StringComparison.Ordinal) || url.Query.Length != 13)
        throw new Exception("Pairing key is not eight characters");
    var localUrl = new UriBuilder(url) { Host = "127.0.0.1" }.Uri;
    using var http = new HttpClient();
    var html = await http.GetStringAsync(localUrl);
    if (!html.Contains("id=\"screen\"")) throw new Exception("Web page missing screen element");
    Uri? alternateUri = null;
    if (server.ConnectionOptions.Skip(1).FirstOrDefault() is { } alternate)
    {
        alternateUri = new Uri(alternate.Url);
        if (alternateUri.Query != url.Query) throw new Exception("Address menu URLs use different pairing keys");
        var alternateHtml = await http.GetStringAsync(alternateUri);
        if (!alternateHtml.Contains("id=\"screen\"")) throw new Exception("Alternate address did not serve the page");
        Console.WriteLine("OK: alternate address serves the page");
    }
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
    var ready = await socket.ReceiveAsync(buffer, timeout.Token);
    if (ready.MessageType != WebSocketMessageType.Text) throw new Exception("Client-ready message missing");
    using var readyJson = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, ready.Count));
    if (readyJson.RootElement.GetProperty("type").GetString() != "ready")
        throw new Exception("Client-ready message missing");
    var clientId = readyJson.RootElement.GetProperty("clientId").GetString();
    if (string.IsNullOrEmpty(clientId)) throw new Exception("Client ID missing");
    var modeVersion = readyJson.RootElement.GetProperty("version").GetInt32();
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
    var offerBase = alternateUri ?? localUrl;
    using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(offerBase, "/offer?key=" + url.Query.TrimStart('?').Split('=')[1] + "&client=" + clientId + "&version=" + modeVersion));
    request.Headers.TryAddWithoutValidation("Origin", offerBase.GetLeftPart(UriPartial.Authority));
    request.Content = new StringContent(receiver.localDescription.sdp.ToString(), System.Text.Encoding.UTF8, "application/sdp");
    using var response = await http.SendAsync(request);
    response.EnsureSuccessStatusCode();
    var answer = await response.Content.ReadAsStringAsync();
    if (alternateUri is not null && !answer.Contains($" {alternateUri.Host} ", StringComparison.Ordinal))
        throw new Exception("WebRTC answer is missing the selected address ICE candidate");
    var accepted = receiver.setRemoteDescription(new RTCSessionDescriptionInit { sdp = answer, type = RTCSdpType.answer });
    if (accepted != SetDescriptionResultEnum.OK) throw new Exception("WebRTC answer rejected: " + accepted);
    for (var i = 0; i < 150 && Volatile.Read(ref receivedFrames) == 0; i++) await Task.Delay(50);
    if (receivedFrames == 0) throw new Exception("WebRTC connected but no H.264 frames arrived");
    Console.WriteLine($"OK: H.264 WebRTC frames={receivedFrames}");
    var framesBeforeStop = Volatile.Read(ref receivedFrames);
    session.Stop();
    if (session.Selected is not null || session.LatestFrame is not null)
        throw new Exception("Stop left a selected window or captured frame");
    using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (true)
    {
        var result = await socket.ReceiveAsync(buffer, stopTimeout.Token);
        if (result.MessageType == WebSocketMessageType.Text &&
            System.Text.Encoding.UTF8.GetString(buffer, 0, result.Count).Contains("\"waiting\"", StringComparison.Ordinal)) break;
    }
    session.Select(new WindowEntry(hwnd, "Smoke test", pid));
    for (var i = 0; i < 150 && Volatile.Read(ref receivedFrames) == framesBeforeStop; i++) await Task.Delay(50);
    if (Volatile.Read(ref receivedFrames) == framesBeforeStop)
        throw new Exception("H.264 frames did not resume after selecting a window again");
    Console.WriteLine("OK: stop clears capture and selecting again resumes video");
    receiver.Close("done");
    using var second = new ClientWebSocket();
    second.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{localUrl.Port}");
    await second.ConnectAsync(wsUri, CancellationToken.None);
    using var replacementTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    var nextReady = await second.ReceiveAsync(buffer, replacementTimeout.Token);
    if (nextReady.MessageType != WebSocketMessageType.Text) throw new Exception("Replacement client did not receive its ID");
    using var nextReadyJson = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, nextReady.Count));
    if (nextReadyJson.RootElement.GetProperty("type").GetString() != "ready")
        throw new Exception("Replacement client did not receive its ID");
    var nextClientId = nextReadyJson.RootElement.GetProperty("clientId").GetString();
    var nextModeVersion = nextReadyJson.RootElement.GetProperty("version").GetInt32();
    if (string.IsNullOrEmpty(nextClientId) || nextClientId == clientId)
        throw new Exception("Replacement client did not receive a unique ID");
    while (true)
    {
        var replaced = await socket.ReceiveAsync(buffer, replacementTimeout.Token);
        if (replaced.MessageType == WebSocketMessageType.Close)
            throw new Exception("Previous client closed without replacement notice");
        if (replaced.MessageType == WebSocketMessageType.Text &&
            System.Text.Encoding.UTF8.GetString(buffer, 0, replaced.Count).Contains("\"replaced\"", StringComparison.Ordinal)) break;
    }
    while (true)
    {
        var frame = await second.ReceiveAsync(buffer, replacementTimeout.Token);
        if (frame.MessageType == WebSocketMessageType.Binary) break;
    }
    using var secondReceiver = new RTCPeerConnection(null);
    secondReceiver.addTrack(new MediaStreamTrack(
        Helper.GetSupportedVideoFormats().Where(f => f.Codec == VideoCodecsEnum.H264).ToList(),
        MediaStreamStatusEnum.RecvOnly));
    var replacementFrames = 0;
    secondReceiver.OnVideoFrameReceived += (endpoint, timestamp, frame, format) => Interlocked.Increment(ref replacementFrames);
    await secondReceiver.setLocalDescription(secondReceiver.createOffer());
    for (var i = 0; i < 100 && secondReceiver.iceGatheringState != RTCIceGatheringState.complete; i++)
        await Task.Delay(30);
    using var staleOffer = new HttpRequestMessage(HttpMethod.Post, new Uri(localUrl, "/offer?key=" + url.Query.TrimStart('?').Split('=')[1] + "&client=" + clientId + "&version=" + modeVersion));
    staleOffer.Headers.TryAddWithoutValidation("Origin", localUrl.GetLeftPart(UriPartial.Authority));
    staleOffer.Content = new StringContent(secondReceiver.localDescription.sdp.ToString(), System.Text.Encoding.UTF8, "application/sdp");
    using var staleResponse = await http.SendAsync(staleOffer);
    if (staleResponse.StatusCode != System.Net.HttpStatusCode.Forbidden)
        throw new Exception("Replaced client was allowed to start video");
    using var nextOffer = new HttpRequestMessage(HttpMethod.Post, new Uri(localUrl, "/offer?key=" + url.Query.TrimStart('?').Split('=')[1] + "&client=" + nextClientId + "&version=" + nextModeVersion));
    nextOffer.Headers.TryAddWithoutValidation("Origin", localUrl.GetLeftPart(UriPartial.Authority));
    nextOffer.Content = new StringContent(secondReceiver.localDescription.sdp.ToString(), System.Text.Encoding.UTF8, "application/sdp");
    using var nextResponse = await http.SendAsync(nextOffer);
    nextResponse.EnsureSuccessStatusCode();
    var nextAnswer = await nextResponse.Content.ReadAsStringAsync();
    if (secondReceiver.setRemoteDescription(new RTCSessionDescriptionInit { sdp = nextAnswer, type = RTCSdpType.answer }) != SetDescriptionResultEnum.OK)
        throw new Exception("Replacement WebRTC answer rejected");
    for (var i = 0; i < 150 && Volatile.Read(ref replacementFrames) == 0; i++) await Task.Delay(50);
    if (replacementFrames == 0) throw new Exception("Replacement client received no H.264 video");
    Console.WriteLine("OK: newer client takes over and receives JPEG and H.264 video");
    secondReceiver.Close("done");
    server.SetVideoPreference(VideoPreference.Jpeg);
    using var modeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
    int jpegVersion;
    while (true)
    {
        var update = await second.ReceiveAsync(buffer, modeTimeout.Token);
        if (update.MessageType != WebSocketMessageType.Text) continue;
        using var json = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, update.Count));
        if (json.RootElement.GetProperty("type").GetString() != "videoMode") continue;
        if (json.RootElement.GetProperty("mode").GetString() != "Jpeg")
            throw new Exception("JPEG mode update was not sent");
        jpegVersion = json.RootElement.GetProperty("version").GetInt32();
        break;
    }
    while (true)
    {
        var jpeg = await second.ReceiveAsync(buffer, modeTimeout.Token);
        if (jpeg.MessageType == WebSocketMessageType.Binary) break;
    }
    Console.WriteLine("OK: switching to JPEG resumes JPEG frames");
    server.SetVideoPreference(VideoPreference.H265);
    int h265Version;
    while (true)
    {
        var update = await second.ReceiveAsync(buffer, modeTimeout.Token);
        if (update.MessageType != WebSocketMessageType.Text) continue;
        using var json = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, update.Count));
        if (json.RootElement.GetProperty("type").GetString() != "videoMode") continue;
        if (json.RootElement.GetProperty("mode").GetString() != "H265")
            throw new Exception("H.265 mode update was not sent");
        h265Version = json.RootElement.GetProperty("version").GetInt32();
        if (h265Version <= jpegVersion) throw new Exception("Video mode version did not advance");
        break;
    }
    using var h265Receiver = new RTCPeerConnection(null);
    using var unsupportedOffer = new HttpRequestMessage(HttpMethod.Post, new Uri(localUrl, "/offer?key=" + url.Query.TrimStart('?').Split('=')[1] + "&client=" + nextClientId + "&version=" + h265Version));
    unsupportedOffer.Headers.TryAddWithoutValidation("Origin", localUrl.GetLeftPart(UriPartial.Authority));
    unsupportedOffer.Content = new StringContent(secondReceiver.localDescription.sdp.ToString(), System.Text.Encoding.UTF8, "application/sdp");
    using var unsupportedResponse = await http.SendAsync(unsupportedOffer);
    if (unsupportedResponse.StatusCode != System.Net.HttpStatusCode.UnsupportedMediaType)
        throw new Exception("Forced H.265 accepted a browser offer without H.265");
    var h265Formats = Helper.GetSupportedVideoFormats().Where(f => f.Codec == VideoCodecsEnum.H265).ToList();
    if (h265Formats.Count == 0) throw new Exception("Test receiver has no H.265 format");
    h265Receiver.addTrack(new MediaStreamTrack(h265Formats, MediaStreamStatusEnum.RecvOnly));
    var h265Frames = 0;
    h265Receiver.OnVideoFrameReceived += (endpoint, timestamp, frame, format) => Interlocked.Increment(ref h265Frames);
    await h265Receiver.setLocalDescription(h265Receiver.createOffer());
    for (var i = 0; i < 100 && h265Receiver.iceGatheringState != RTCIceGatheringState.complete; i++)
        await Task.Delay(30);
    using var h265Offer = new HttpRequestMessage(HttpMethod.Post, new Uri(localUrl, "/offer?key=" + url.Query.TrimStart('?').Split('=')[1] + "&client=" + nextClientId + "&version=" + h265Version));
    h265Offer.Headers.TryAddWithoutValidation("Origin", localUrl.GetLeftPart(UriPartial.Authority));
    h265Offer.Content = new StringContent(h265Receiver.localDescription.sdp.ToString(), System.Text.Encoding.UTF8, "application/sdp");
    using var h265Response = await http.SendAsync(h265Offer);
    h265Response.EnsureSuccessStatusCode();
    var h265Answer = await h265Response.Content.ReadAsStringAsync();
    if (!h265Answer.Contains("H265/90000", StringComparison.OrdinalIgnoreCase))
        throw new Exception("H.265 was not negotiated");
    if (h265Receiver.setRemoteDescription(new RTCSessionDescriptionInit { sdp = h265Answer, type = RTCSdpType.answer }) != SetDescriptionResultEnum.OK)
        throw new Exception("H.265 WebRTC answer rejected");
    for (var i = 0; i < 150 && Volatile.Read(ref h265Frames) == 0; i++) await Task.Delay(50);
    if (h265Frames == 0) throw new Exception("H.265 WebRTC connected but no frames arrived");
    Console.WriteLine($"OK: H.265 WebRTC frames={h265Frames}");
    h265Receiver.Close("done");
    server.SetVideoPreference(VideoPreference.Auto);
    int autoVersion;
    while (true)
    {
        var update = await second.ReceiveAsync(buffer, modeTimeout.Token);
        if (update.MessageType != WebSocketMessageType.Text) continue;
        using var json = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, update.Count));
        if (json.RootElement.GetProperty("type").GetString() != "videoMode") continue;
        if (json.RootElement.GetProperty("mode").GetString() != "Auto")
            throw new Exception("Auto mode update was not sent");
        autoVersion = json.RootElement.GetProperty("version").GetInt32();
        break;
    }
    using var autoReceiver = new RTCPeerConnection(null);
    autoReceiver.addTrack(new MediaStreamTrack(
        Helper.GetSupportedVideoFormats().Where(f => f.Codec is VideoCodecsEnum.H264 or VideoCodecsEnum.H265).ToList(),
        MediaStreamStatusEnum.RecvOnly));
    var autoFrames = 0;
    autoReceiver.OnVideoFrameReceived += (endpoint, timestamp, frame, format) => Interlocked.Increment(ref autoFrames);
    await autoReceiver.setLocalDescription(autoReceiver.createOffer());
    for (var i = 0; i < 100 && autoReceiver.iceGatheringState != RTCIceGatheringState.complete; i++)
        await Task.Delay(30);
    using var autoOffer = new HttpRequestMessage(HttpMethod.Post, new Uri(localUrl, "/offer?key=" + url.Query.TrimStart('?').Split('=')[1] + "&client=" + nextClientId + "&version=" + autoVersion));
    autoOffer.Headers.TryAddWithoutValidation("Origin", localUrl.GetLeftPart(UriPartial.Authority));
    autoOffer.Content = new StringContent(autoReceiver.localDescription.sdp.ToString(), System.Text.Encoding.UTF8, "application/sdp");
    using var autoResponse = await http.SendAsync(autoOffer);
    autoResponse.EnsureSuccessStatusCode();
    var autoAnswer = await autoResponse.Content.ReadAsStringAsync();
    if (!autoAnswer.Contains("H265/90000", StringComparison.OrdinalIgnoreCase))
        throw new Exception("Auto did not prefer H.265 when both codecs were offered");
    if (autoReceiver.setRemoteDescription(new RTCSessionDescriptionInit { sdp = autoAnswer, type = RTCSdpType.answer }) != SetDescriptionResultEnum.OK)
        throw new Exception("Auto WebRTC answer rejected");
    for (var i = 0; i < 150 && Volatile.Read(ref autoFrames) == 0; i++) await Task.Delay(50);
    if (autoFrames == 0) throw new Exception("Auto H.265 stream delivered no frames");
    Console.WriteLine("OK: Auto prefers H.265 when offered");
    autoReceiver.Close("done");
    second.Abort();
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
