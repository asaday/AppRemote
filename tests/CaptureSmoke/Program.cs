using System.Net.WebSockets;
using AppRemote;
using AppRemote.Windows;
using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorceryMedia.Abstractions;
using SIPSorceryMedia.FFmpeg;
using Forms = System.Windows.Forms;

if (args.Contains("--handoff-only", StringComparer.OrdinalIgnoreCase))
    return await RunHandoffSmokeAsync();
if (args.Contains("--idle-only", StringComparer.OrdinalIgnoreCase))
    return await RunIdleSmokeAsync();

using var fixture = new AnimatedWindow();
var hwnd = await fixture.StartAsync();
var pid = (uint)Environment.ProcessId;
var codecSettings = new AppSettings { VideoMode = VideoPreference.H265, TouchMode = TouchInputMode.Cursor,
    BackgroundInput = true, Port = 18765 };
var settingsRoundTrip = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
    System.Text.Json.JsonSerializer.Serialize(codecSettings));
if (settingsRoundTrip?.VideoMode != VideoPreference.H265 || settingsRoundTrip.TouchMode != TouchInputMode.Cursor ||
    settingsRoundTrip.BackgroundInput != true ||
    settingsRoundTrip.Port != 18765)
    throw new Exception("Video, phone input, or port was not preserved by settings serialization");
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
var rawFrames = 0;
session.RawFrameReady += (_, _, _) => Interlocked.Increment(ref rawFrames);
session.CaptureFaulted += error => Console.WriteLine("CAPTURE ERROR: " + error);
await using var server = new RemoteServer(session, 18765);
server.VideoFailed += message => Console.WriteLine("VIDEO ERROR: " + message);
server.VideoModeChanged += mode => Console.WriteLine("VIDEO MODE: " + mode);
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
    if (readyJson.RootElement.GetProperty("touchMode").GetString() != "Direct")
        throw new Exception("Initial phone input mode was not sent");
    if (readyJson.RootElement.GetProperty("backgroundInput").GetBoolean())
        throw new Exception("Initial background input mode was not sent");
    server.SetTouchMode(TouchInputMode.Cursor);
    using (var touchTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
    {
        while (true)
        {
            var update = await socket.ReceiveAsync(buffer, touchTimeout.Token);
            if (update.MessageType != WebSocketMessageType.Text) continue;
            using var modeJson = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, update.Count));
            if (modeJson.RootElement.GetProperty("type").GetString() != "touchMode") continue;
            if (modeJson.RootElement.GetProperty("mode").GetString() != "Cursor")
                throw new Exception("Phone input mode update was incorrect");
            break;
        }
    }
    server.SetTouchMode(TouchInputMode.Direct);
    Console.WriteLine("OK: phone input mode changes reach the connected browser");
    server.SetBackgroundInput(true);
    using (var backgroundTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
    {
        while (true)
        {
            var update = await socket.ReceiveAsync(buffer, backgroundTimeout.Token);
            if (update.MessageType != WebSocketMessageType.Text) continue;
            using var modeJson = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, update.Count));
            if (modeJson.RootElement.GetProperty("type").GetString() != "backgroundInput") continue;
            if (!modeJson.RootElement.GetProperty("enabled").GetBoolean())
                throw new Exception("Background input update was incorrect");
            break;
        }
    }
    if (!NativeWindows.TryGetCursorPosition(out var beforeBackgroundClick))
        throw new Exception("Could not read the cursor position");
    var foregroundBeforeClick = NativeWindows.IsForeground(hwnd);
    var down = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"down\",\"x\":0.5,\"y\":0.5}");
    var up = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"up\",\"x\":0.5,\"y\":0.5}");
    await socket.SendAsync(down, WebSocketMessageType.Text, true, CancellationToken.None);
    await socket.SendAsync(up, WebSocketMessageType.Text, true, CancellationToken.None);
    for (var i = 0; i < 20 && fixture.ClickCount == 0; i++) await Task.Delay(50);
    if (fixture.ClickCount == 0)
    {
        NativeWindows.TryGetInputBounds(hwnd, out var debugBounds);
        var hit = NativeWindows.TryGetClientTarget(hwnd,
            (debugBounds.Left + debugBounds.Right) / 2, (debugBounds.Top + debugBounds.Bottom) / 2,
            out var hitWindow, out var hitPoint);
        throw new Exception($"Background click did not reach the test button (hit={hit}, window={hitWindow}, button={fixture.ButtonHandle}, point={hitPoint.X},{hitPoint.Y})");
    }
    if (!NativeWindows.TryGetCursorPosition(out var afterBackgroundClick) ||
        beforeBackgroundClick.X != afterBackgroundClick.X || beforeBackgroundClick.Y != afterBackgroundClick.Y ||
        foregroundBeforeClick != NativeWindows.IsForeground(hwnd))
        throw new Exception("Background click changed the physical cursor or foreground window");
    var textDown = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"down\",\"x\":0.5,\"y\":0.95}");
    var textUp = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"up\",\"x\":0.5,\"y\":0.95}");
    var textInput = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"text\",\"value\":\"abc\"}");
    await socket.SendAsync(textDown, WebSocketMessageType.Text, true, CancellationToken.None);
    await socket.SendAsync(textUp, WebSocketMessageType.Text, true, CancellationToken.None);
    await socket.SendAsync(textInput, WebSocketMessageType.Text, true, CancellationToken.None);
    for (var i = 0; i < 20 && fixture.ReceivedText != "abc"; i++) await Task.Delay(50);
    if (fixture.ReceivedText != "abc") throw new Exception("Background text did not reach the test field");
    server.SetTouchMode(TouchInputMode.Cursor);
    var previousClicks = fixture.ClickCount;
    var cursorMove = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"cursorMove\",\"dx\":0.1,\"dy\":0.1}");
    var cursorClick = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"cursorClick\"}");
    await socket.SendAsync(cursorMove, WebSocketMessageType.Text, true, CancellationToken.None);
    await socket.SendAsync(cursorClick, WebSocketMessageType.Text, true, CancellationToken.None);
    for (var i = 0; i < 20 && fixture.ClickCount == previousClicks; i++) await Task.Delay(50);
    if (fixture.ClickCount == previousClicks) throw new Exception("Background virtual cursor did not click the test button");
    if (!NativeWindows.TryGetCursorPosition(out var afterVirtualClick) ||
        beforeBackgroundClick.X != afterVirtualClick.X || beforeBackgroundClick.Y != afterVirtualClick.Y)
        throw new Exception("Background virtual cursor moved the physical cursor");
    server.SetTouchMode(TouchInputMode.Direct);
    server.SetBackgroundInput(false);
    Console.WriteLine("OK: background click, text, and virtual cursor work without moving the physical cursor or foreground window");
    if (NativeWindows.IsForeground(hwnd) &&
        NativeWindows.TryGetCaptureBounds(hwnd, out var bounds) &&
        NativeWindows.TryGetCursorPosition(out var originalCursor))
    {
        try
        {
            var centerX = (bounds.Left + bounds.Right) / 2;
            var centerY = (bounds.Top + bounds.Bottom) / 2;
            NativeWindows.MoveCursor(centerX, centerY);
            var move = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"cursorMove\",\"dx\":0.1,\"dy\":0.1}");
            await socket.SendAsync(move, WebSocketMessageType.Text, true, CancellationToken.None);
            var moved = false;
            for (var i = 0; i < 20; i++)
            {
                await Task.Delay(50);
                if (NativeWindows.TryGetCursorPosition(out var cursor) &&
                    cursor.X > centerX && cursor.Y > centerY &&
                    cursor.X < bounds.Right && cursor.Y < bounds.Bottom)
                {
                    moved = true;
                    break;
                }
            }
            if (!moved) throw new Exception("Relative cursor movement was not applied");
            Console.WriteLine("OK: relative cursor movement stays inside the selected window");
        }
        finally { NativeWindows.MoveCursor(originalCursor.X, originalCursor.Y); }
    }
    else Console.WriteLine("SKIP: Windows did not bring the capture test window to the foreground for the pointer check");
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
    if (receivedFrames == 0) throw new Exception($"WebRTC connected but no H.264 frames arrived (raw={rawFrames})");
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
    if (!autoAnswer.Contains("H264/90000", StringComparison.OrdinalIgnoreCase))
        throw new Exception("Auto did not prefer H.264 when both codecs were offered");
    if (autoReceiver.setRemoteDescription(new RTCSessionDescriptionInit { sdp = autoAnswer, type = RTCSdpType.answer }) != SetDescriptionResultEnum.OK)
        throw new Exception("Auto WebRTC answer rejected");
    for (var i = 0; i < 150 && Volatile.Read(ref autoFrames) == 0; i++) await Task.Delay(50);
    if (autoFrames == 0) throw new Exception("Auto H.264 stream delivered no frames");
    Console.WriteLine("OK: Auto prefers H.264 when offered");
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

static async Task<int> RunHandoffSmokeAsync()
{
    await using var server = new RemoteServer(new RemoteSession(), 18766);
    try
    {
        await server.StartAsync();
        var localUrl = new UriBuilder(server.Url!) { Host = "127.0.0.1" }.Uri;
        var origin = localUrl.GetLeftPart(UriPartial.Authority);
        var wsUri = new UriBuilder(localUrl) { Scheme = "ws", Path = "/ws" }.Uri;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task<ClientWebSocket> ConnectAsync(bool resumeOnly)
        {
            var client = new ClientWebSocket();
            client.Options.SetRequestHeader("Origin", origin);
            var target = resumeOnly ? new Uri(wsUri + "&resume=1") : wsUri;
            await client.ConnectAsync(target, timeout.Token);
            return client;
        }
        async Task<string> ReadReadyAsync(ClientWebSocket client)
        {
            var buffer = new byte[4096];
            var message = await client.ReceiveAsync(buffer, timeout.Token);
            if (message.MessageType != WebSocketMessageType.Text)
                throw new Exception("Client-ready message missing");
            using var json = System.Text.Json.JsonDocument.Parse(buffer.AsMemory(0, message.Count));
            if (json.RootElement.GetProperty("type").GetString() != "ready")
                throw new Exception("Client-ready message missing");
            return json.RootElement.GetProperty("clientId").GetString()!;
        }

        using var first = await ConnectAsync(false);
        var firstId = await ReadReadyAsync(first);
        using var second = await ConnectAsync(false);
        var secondId = await ReadReadyAsync(second);
        if (firstId == secondId) throw new Exception("New client did not get a unique ID");
        var buffer = new byte[4096];
        while (true)
        {
            var message = await first.ReceiveAsync(buffer, timeout.Token);
            if (message.MessageType == WebSocketMessageType.Close)
                throw new Exception("Previous client closed without replacement notice");
            if (message.MessageType == WebSocketMessageType.Text &&
                System.Text.Encoding.UTF8.GetString(buffer, 0, message.Count).Contains("\"replaced\"", StringComparison.Ordinal)) break;
        }
        first.Abort();

        using var busyRetry = await ConnectAsync(true);
        var busyResponse = await busyRetry.ReceiveAsync(buffer, timeout.Token);
        if (busyResponse.MessageType != WebSocketMessageType.Close || busyResponse.CloseStatus != (WebSocketCloseStatus)4002)
            throw new Exception("Idle-only retry took over an active client");

        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ClientChanged += connected => { if (!connected) disconnected.TrySetResult(); };
        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        await disconnected.Task.WaitAsync(timeout.Token);
        using var restored = await ConnectAsync(true);
        var restoredId = await ReadReadyAsync(restored);
        if (restoredId == firstId || restoredId == secondId)
            throw new Exception("Restored client did not get a new ID");
        Console.WriteLine("OK: normal connection takes over, idle-only retry waits, then reconnects");
        restored.Abort();
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex);
        return 1;
    }
}

static async Task<int> RunIdleSmokeAsync()
{
    using var fixture = new AnimatedWindow();
    var hwnd = await fixture.StartAsync();
    using var session = new RemoteSession();
    var rawFrames = 0;
    session.RawFrameReady += (_, _, _) => Interlocked.Increment(ref rawFrames);
    session.CaptureFaulted += message => Console.WriteLine("CAPTURE ERROR: " + message);
    await using var server = new RemoteServer(session, 18767);
    try
    {
        session.Select(new WindowEntry(hwnd, "Idle smoke", (uint)Environment.ProcessId));
        await server.StartAsync();
        await Task.Delay(350);
        if (rawFrames != 0 || session.LatestFrame is not null)
            throw new Exception("Window capture ran without a browser");

        var localUrl = new UriBuilder(server.Url!) { Host = "127.0.0.1" }.Uri;
        var wsUri = new UriBuilder(localUrl) { Scheme = "ws", Path = "/ws" }.Uri;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task<ClientWebSocket> ConnectAsync()
        {
            var client = new ClientWebSocket();
            client.Options.SetRequestHeader("Origin", localUrl.GetLeftPart(UriPartial.Authority));
            await client.ConnectAsync(wsUri, timeout.Token);
            var buffer = new byte[4096];
            var ready = await client.ReceiveAsync(buffer, timeout.Token);
            if (ready.MessageType != WebSocketMessageType.Text ||
                !System.Text.Encoding.UTF8.GetString(buffer, 0, ready.Count).Contains("\"ready\"", StringComparison.Ordinal))
                throw new Exception("Browser was not admitted");
            return client;
        }

        using (var first = await ConnectAsync())
        {
            for (var i = 0; i < 100 && (Volatile.Read(ref rawFrames) == 0 || session.LatestFrame is null); i++) await Task.Delay(50);
            if (rawFrames == 0 || session.LatestFrame is null)
                throw new Exception($"Window capture did not start for the browser (raw={rawFrames}, jpeg={session.LatestFrame is not null}, selected={session.Selected is not null})");
            var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            server.ClientChanged += connected => { if (!connected) disconnected.TrySetResult(); };
            await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
            await disconnected.Task.WaitAsync(timeout.Token);
        }
        var stoppedAt = Volatile.Read(ref rawFrames);
        await Task.Delay(350);
        if (rawFrames != stoppedAt || session.LatestFrame is not null || session.Selected is null)
            throw new Exception("Window capture did not pause while keeping the selected window");
        using var resumed = await ConnectAsync();
        for (var i = 0; i < 100 && (Volatile.Read(ref rawFrames) == stoppedAt || session.LatestFrame is null); i++) await Task.Delay(50);
        if (rawFrames == stoppedAt || session.LatestFrame is null)
            throw new Exception("Window capture did not resume");
        resumed.Abort();
        Console.WriteLine("OK: window capture pauses without browsers and resumes on connection");
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex);
        return 1;
    }
}

internal sealed class AnimatedWindow : IDisposable
{
    private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private Forms.Form? _form;
    public nint ButtonHandle { get; private set; }
    private int _clicks;
    public int ClickCount => Volatile.Read(ref _clicks);
    private string _receivedText = "";
    public string ReceivedText => Volatile.Read(ref _receivedText);

    public AnimatedWindow()
    {
        _thread = new Thread(Run) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<nint> StartAsync() => _ready.Task.WaitAsync(TimeSpan.FromSeconds(5));

    private void Run()
    {
        try
        {
            using var form = new Forms.Form { Text = "App Remote capture test", Width = 640, Height = 480 };
            _form = form;
            var button = new Forms.Button { Dock = Forms.DockStyle.Fill };
            button.Click += (_, _) => Interlocked.Increment(ref _clicks);
            form.Controls.Add(button);
            var textBox = new Forms.TextBox { Dock = Forms.DockStyle.Bottom, Height = 50 };
            textBox.TextChanged += (_, _) => Volatile.Write(ref _receivedText, textBox.Text);
            form.Controls.Add(textBox);
            using var timer = new Forms.Timer { Interval = 100 };
            var counter = 0;
            timer.Tick += (_, _) => button.Text = $"Frame {++counter}";
            form.Shown += (_, _) => { ButtonHandle = button.Handle; timer.Start(); _ready.TrySetResult(form.Handle); };
            Forms.Application.Run(form);
        }
        catch (Exception ex) { _ready.TrySetException(ex); }
    }

    public void Dispose()
    {
        if (_form is { IsHandleCreated: true } form)
            form.BeginInvoke(new Action(form.Close));
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
