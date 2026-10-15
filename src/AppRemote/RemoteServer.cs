using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppRemote.Windows;
using AppRemote.Android;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AppRemote;

internal sealed record ConnectionOption(string Label, string Address, string Url);

internal sealed class RemoteServer : IAsyncDisposable
{
    private const int DefaultPort = 8765;
    private readonly int _port;
    private readonly RemoteSession _session;
    private readonly InputController _input;
    private readonly string _legacyKey;
    private readonly string _key;
    private readonly object _clientGate = new();
    private readonly SemaphoreSlim _clientTransition = new(1, 1);
    private readonly object _videoGate = new();
    private ClientConnection? _activeClient;
    private WebApplication? _app;
    private IRemoteVideoPeer? _videoPeer;
    private AndroidSource? _androidSource;
    private string? _androidSerial;
    private Func<VideoPreference, Task<AndroidSource>>? _androidFactory;
    private readonly SemaphoreSlim _androidSwitch = new(1, 1);
    private volatile bool _videoActive;
    private VideoPreference? _activeCodec;
    private VideoPreference _videoPreference = VideoPreference.Auto;
    private TouchInputMode _touchMode = TouchInputMode.Direct;
    private bool _backgroundInput;
    private int _videoModeVersion;

    public event Action<bool>? ClientChanged;
    public event Action<VideoPreference?>? VideoModeChanged;
    public event Action<string>? VideoFailed;
    public event Action<AndroidSource?>? AndroidSourceChanged;
    public string? Url { get; private set; }
    public AndroidSource? CurrentAndroidSource => Volatile.Read(ref _androidSource);
    public string? SelectedAndroidSerial { get { lock (_clientGate) return _androidSerial; } }
    public IReadOnlyList<ConnectionOption> ConnectionOptions { get; private set; } = [];

    private sealed class ClientConnection(WebSocket socket)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public WebSocket Socket { get; } = socket;
        public volatile bool Replaced;
    }

    public RemoteServer(RemoteSession session, int port = DefaultPort)
    {
        _session = session;
        _port = port;
        _input = new InputController(session);
        _legacyKey = LoadOrCreateKey();
        _key = CreateShortKey(_legacyKey);
    }

    public void SetVideoPreference(VideoPreference preference)
    {
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        lock (_clientGate)
        {
            if (_videoPreference == preference) return;
            _videoPreference = preference;
            _videoModeVersion++;
        }
        Interlocked.Exchange(ref _videoPeer, null)?.Dispose();
        SetVideoMode(null);
    }

    public AndroidSource? SetAndroidSource(AndroidSource? source, Func<VideoPreference, Task<AndroidSource>>? factory = null)
    {
        AndroidSource? previous;
        lock (_clientGate)
        {
            previous = _androidSource;
            _androidSource = source;
            _androidSerial = source?.Serial;
            _androidFactory = factory;
            _videoModeVersion++;
        }
        Interlocked.Exchange(ref _videoPeer, null)?.Dispose();
        SetVideoMode(null);
        return previous;
    }

    public AndroidSource? SetAndroidSelection(string? serial, Func<VideoPreference, Task<AndroidSource>>? factory = null)
    {
        AndroidSource? previous;
        lock (_clientGate)
        {
            previous = _androidSource;
            _androidSource = null;
            _androidSerial = serial;
            _androidFactory = factory;
            _videoModeVersion++;
        }
        Interlocked.Exchange(ref _videoPeer, null)?.Dispose();
        SetVideoMode(null);
        return previous;
    }

    public void SetTouchMode(TouchInputMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        lock (_clientGate)
        {
            _input.ResetBackgroundCursor();
            _touchMode = mode;
        }
    }

    public void SetBackgroundInput(bool enabled)
    {
        lock (_clientGate)
        {
            _input.SetBackgroundMode(enabled);
            _backgroundInput = enabled;
        }
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(_port));
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.UseWebSockets();

        var assets = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        app.MapGet("/", () => Results.File(Path.Combine(assets, "index.html"), "text/html; charset=utf-8"));
        app.MapGet("/app.js", () => Results.File(Path.Combine(assets, "app.js"), "text/javascript; charset=utf-8"));
        app.MapGet("/apple-touch-icon.png", () => Results.File(Path.Combine(assets, "apple-touch-icon.png"), "image/png"));
        app.MapGet("/icon-192.png", () => Results.File(Path.Combine(assets, "icon-192.png"), "image/png"));
        app.MapGet("/icon-512.png", () => Results.File(Path.Combine(assets, "icon-512.png"), "image/png"));
        app.MapGet("/favicon.ico", () => Results.File(Path.Combine(assets, "favicon.ico"), "image/x-icon"));
        app.MapGet("/manifest.json", (HttpContext context) =>
        {
            if (!HasKey(context)) return Results.Unauthorized();
            var manifest = new
            {
                name = "App Remote",
                short_name = "Remote",
                id = "/?key=" + _key,
                start_url = "/?key=" + _key,
                display = "standalone",
                background_color = "#000000",
                theme_color = "#000000",
                icons = new[]
                {
                    new { src = "/icon-192.png", sizes = "192x192", type = "image/png", purpose = "any" },
                    new { src = "/icon-512.png", sizes = "512x512", type = "image/png", purpose = "any" }
                }
            };
            return Results.Json(manifest, contentType: "application/manifest+json");
        });
        app.Map("/ws", HandleSocketAsync);
        app.MapPost("/offer", async (HttpContext context) =>
        {
            var clientId = context.Request.Query["client"].ToString();
            if (!int.TryParse(context.Request.Query["version"], out var version) ||
                !HasKey(context) || !SameOrigin(context) || !IsCurrentClient(clientId, version))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (context.Request.ContentLength is > 65536) return Results.BadRequest();
            using var reader = new StreamReader(context.Request.Body);
            var offer = await reader.ReadToEndAsync(context.RequestAborted);
            if (offer.Length > 65536) return Results.BadRequest();
            var preference = GetVideoPreference();
            if (preference == VideoPreference.Jpeg)
                return Results.StatusCode(StatusCodes.Status409Conflict);
            var androidSerial = SelectedAndroidSerial;
            var desired = preference switch
            {
                VideoPreference.Auto => new[] { VideoPreference.H264, VideoPreference.H265 },
                _ => new[] { preference }
            };
            var candidates = desired.Where(codec => OfferHasCodec(offer, codec)).ToArray();
            if (candidates.Length == 0)
            {
                var suffix = androidSerial is null ? "; using JPEG." : ".";
                var error = preference == VideoPreference.Auto
                    ? "No supported WebRTC video codec" + suffix
                    : $"{FormatCodec(preference)} is unavailable in this browser" + suffix;
                if (IsCurrentClient(clientId, version)) VideoFailed?.Invoke(error);
                return Results.Problem(error, statusCode: StatusCodes.Status415UnsupportedMediaType);
            }
            var requestedAddress = ConnectionOptions.Any(option =>
                option.Address.Equals(context.Request.Host.Host, StringComparison.OrdinalIgnoreCase)) &&
                IPAddress.TryParse(context.Request.Host.Host, out var address) ? address : null;
            Exception? lastError = null;
            foreach (var codec in candidates)
            {
                IRemoteVideoPeer? peer = null;
                try
                {
                    var androidSource = androidSerial is null ? null
                        : await EnsureAndroidCodecAsync(androidSerial, codec);
                    peer = androidSource is null
                        ? new VideoPeer(_session, codec, requestedAddress)
                        : new AndroidVideoPeer(androidSource, requestedAddress);
                    var currentPeer = peer;
                    peer.ActiveChanged += active =>
                    {
                        if (ReferenceEquals(Volatile.Read(ref _videoPeer), currentPeer))
                            SetVideoMode(active ? currentPeer.CodecMode : null);
                    };
                    peer.Failed += message =>
                    {
                        if (ReferenceEquals(Volatile.Read(ref _videoPeer), currentPeer))
                            VideoFailed?.Invoke(message);
                    };
                    IRemoteVideoPeer? previous;
                    lock (_clientGate)
                    {
                        if (_activeClient?.Id != clientId || _videoModeVersion != version ||
                            _androidSerial != androidSerial ||
                            (androidSerial is not null && !ReferenceEquals(_androidSource, androidSource)))
                        {
                            peer.Dispose();
                            return Results.StatusCode(StatusCodes.Status409Conflict);
                        }
                        previous = Interlocked.Exchange(ref _videoPeer, peer);
                    }
                    previous?.Dispose();
                    var answer = await peer.AcceptOfferAsync(offer);
                    if (!IsCurrentClient(clientId, version))
                    {
                        Interlocked.CompareExchange(ref _videoPeer, null, peer);
                        peer.Dispose();
                        return Results.StatusCode(StatusCodes.Status409Conflict);
                    }
                    return Results.Text(answer, "application/sdp");
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    if (peer is not null)
                    {
                        var removed = Interlocked.CompareExchange(ref _videoPeer, null, peer);
                        peer.Dispose();
                        if (ReferenceEquals(removed, peer)) SetVideoMode(null);
                    }
                }
            }
            var failure = $"{FormatCodec(preference)} could not start" +
                (androidSerial is null ? "; using JPEG. " : ". ") + lastError?.Message;
            if (IsCurrentClient(clientId, version)) VideoFailed?.Invoke(failure);
            return Results.Problem(failure, statusCode: 503);
        });

        try { await app.StartAsync(); }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
        _app = app;
        ConnectionOptions = GetConnectionOptions();
        Url = ConnectionOptions[0].Url;
    }

    private async Task<AndroidSource> EnsureAndroidCodecAsync(string serial, VideoPreference codec)
    {
        await _androidSwitch.WaitAsync();
        try
        {
            var active = Volatile.Read(ref _androidSource);
            if (SelectedAndroidSerial != serial)
                throw new InvalidOperationException("Android source changed.");
            if (active?.Codec == codec) return active;
            var factory = _androidFactory ?? throw new InvalidOperationException("Android encoder is unavailable.");
            var replacement = await factory(codec);
            lock (_clientGate)
            {
                if (_androidSerial != serial || !ReferenceEquals(_androidSource, active))
                {
                    _ = replacement.DisposeAsync();
                    throw new InvalidOperationException("Android source changed.");
                }
                _androidSource = replacement;
            }
            AndroidSourceChanged?.Invoke(replacement);
            if (active is not null) await active.DisposeAsync();
            return replacement;
        }
        finally { _androidSwitch.Release(); }
    }

    private async Task StopIdleAndroidAsync()
    {
        await _androidSwitch.WaitAsync();
        try
        {
            AndroidSource? source;
            lock (_clientGate)
            {
                if (_activeClient is not null) return;
                source = _androidSource;
                _androidSource = null;
            }
            if (source is null) return;
            AndroidSourceChanged?.Invoke(null);
            await source.DisposeAsync();
        }
        finally { _androidSwitch.Release(); }
    }

    private async Task HandleSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest || !HasKey(context) || !SameOrigin(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var connection = new ClientConnection(socket);
        var resumeOnly = context.Request.Query["resume"] == "1";
        await _clientTransition.WaitAsync(context.RequestAborted);
        bool busy;
        try
        {
            lock (_clientGate)
            {
                busy = resumeOnly && _activeClient is not null;
                if (!busy)
                {
                    if (_activeClient is { } previous) previous.Replaced = true;
                    _activeClient = connection;
                }
            }
            if (!busy)
            {
                _input.Release();
                if (Volatile.Read(ref _androidSource) is { } oldSource) await oldSource.ReleaseInputAsync();
                _input.ResetBackgroundCursor();
                Interlocked.Exchange(ref _videoPeer, null)?.Dispose();
                SetVideoMode(null);
                _session.SetClientConnected(true);
                ClientChanged?.Invoke(true);
            }
        }
        finally { _clientTransition.Release(); }
        if (busy)
        {
            await socket.CloseOutputAsync((WebSocketCloseStatus)4002, "Another device is connected", context.RequestAborted);
            return;
        }
        try
        {
            var (preference, version, touchMode, backgroundInput) = GetClientSettings();
            var ready = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                { type = "ready", clientId = connection.Id, mode = preference.ToString(), version,
                    touchMode = touchMode.ToString(), backgroundInput,
                    source = SelectedAndroidSerial is null ? "Windows" : "Android" }));
            await socket.SendAsync(ready, WebSocketMessageType.Text, true, context.RequestAborted);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var sender = SendFramesAsync(connection, version, touchMode, backgroundInput, linked.Token);
            var buffer = new byte[4096];
            var closeRequested = false;
            try
            {
                while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
                {
                    var message = await socket.ReceiveAsync(buffer, linked.Token);
                    if (message.MessageType == WebSocketMessageType.Close) { closeRequested = true; break; }
                    if (message.MessageType != WebSocketMessageType.Text || !message.EndOfMessage) continue;
                    if (!IsCurrentClient(connection.Id)) continue;
                    try
                    {
                        var json = Encoding.UTF8.GetString(buffer, 0, message.Count);
                        var android = Volatile.Read(ref _androidSource);
                        if (android is not null) await android.HandleInputAsync(json);
                        else _input.Handle(json);
                    }
                    catch (JsonException) { /* Ignore malformed controls. */ }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                    { VideoFailed?.Invoke("Android input failed: " + ex.Message); }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                linked.Cancel();
                try { await sender; } catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
                if (closeRequested && socket.State == WebSocketState.CloseReceived)
                {
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); }
                    catch (WebSocketException) { }
                }
            }
        }
        finally
        {
            await _clientTransition.WaitAsync();
            try
            {
                bool wasCurrent;
                lock (_clientGate)
                {
                    wasCurrent = ReferenceEquals(_activeClient, connection);
                    if (wasCurrent) _activeClient = null;
                }
                if (wasCurrent)
                {
                    _input.Release();
                    if (Volatile.Read(ref _androidSource) is { } source) await source.ReleaseInputAsync();
                    Interlocked.Exchange(ref _videoPeer, null)?.Dispose();
                    SetVideoMode(null);
                    _session.SetClientConnected(false);
                    await StopIdleAndroidAsync();
                    ClientChanged?.Invoke(false);
                }
            }
            finally { _clientTransition.Release(); }
        }
    }

    private bool IsCurrentClient(string id, int? version = null)
    {
        lock (_clientGate) return _activeClient?.Id == id &&
            (version is null || _videoModeVersion == version);
    }

    private VideoPreference GetVideoPreference()
    {
        lock (_clientGate) return _videoPreference;
    }

    private (VideoPreference Preference, int Version, TouchInputMode TouchMode, bool BackgroundInput) GetClientSettings()
    {
        lock (_clientGate) return (_videoPreference, _videoModeVersion, _touchMode, _backgroundInput);
    }

    private static bool OfferHasCodec(string offer, VideoPreference codec)
    {
        var name = codec == VideoPreference.H265 ? "H265" : "H264";
        return offer.Split('\n').Any(line => line.Trim().StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase) &&
            line.Contains($" {name}/90000", StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatCodec(VideoPreference codec) => codec switch
    {
        VideoPreference.H264 => "H.264",
        VideoPreference.H265 => "H.265",
        VideoPreference.Jpeg => "JPEG",
        _ => "WebRTC video"
    };

    private async Task SendFramesAsync(ClientConnection connection, int announcedVersion,
        TouchInputMode announcedTouchMode, bool announcedBackgroundInput, CancellationToken stop)
    {
        var socket = connection.Socket;
        long lastSequence = -1;
        var waitingSent = false;
        while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
        {
            if (connection.Replaced)
            {
                var notice = Encoding.UTF8.GetBytes("{\"type\":\"replaced\"}");
                await socket.SendAsync(notice, WebSocketMessageType.Text, true, stop);
                await socket.CloseOutputAsync((WebSocketCloseStatus)4001, "Replaced by another device", stop);
                break;
            }
            var (preference, version, touchMode, backgroundInput) = GetClientSettings();
            if (version != announcedVersion)
            {
                var update = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    { type = "videoMode", mode = preference.ToString(), version,
                    source = SelectedAndroidSerial is null ? "Windows" : "Android" }));
                await socket.SendAsync(update, WebSocketMessageType.Text, true, stop);
                announcedVersion = version;
            }
            if (touchMode != announcedTouchMode)
            {
                var update = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    { type = "touchMode", mode = touchMode.ToString() }));
                await socket.SendAsync(update, WebSocketMessageType.Text, true, stop);
                announcedTouchMode = touchMode;
            }
            if (backgroundInput != announcedBackgroundInput)
            {
                var update = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                    { type = "backgroundInput", enabled = backgroundInput }));
                await socket.SendAsync(update, WebSocketMessageType.Text, true, stop);
                announcedBackgroundInput = backgroundInput;
            }
            var frame = _session.LatestFrame;
            if (_session.Selected is null && SelectedAndroidSerial is null)
            {
                if (!waitingSent)
                {
                    var status = Encoding.UTF8.GetBytes("{\"type\":\"waiting\"}");
                    await socket.SendAsync(status, WebSocketMessageType.Text, true, stop);
                    waitingSent = true;
                }
            }
            else
            {
                waitingSent = false;
                if (!_videoActive && frame is not null && frame.Sequence != lastSequence)
                {
                    await socket.SendAsync(frame.Jpeg, WebSocketMessageType.Binary, true, stop);
                    lastSequence = frame.Sequence;
                }
            }
            await Task.Delay(80, stop);
        }
    }

    private bool HasKey(HttpContext context)
    {
        var supplied = context.Request.Query["key"].ToString();
        var expected = supplied.Length == _key.Length ? _key
            : supplied.Length == _legacyKey.Length ? _legacyKey : null;
        return expected is not null && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(supplied), Encoding.ASCII.GetBytes(expected));
    }

    private void SetVideoMode(VideoPreference? codec)
    {
        lock (_videoGate)
        {
            if (_activeCodec == codec) return;
            _activeCodec = codec;
            _videoActive = codec is not null;
            _session.SetVideoActive(_videoActive);
            VideoModeChanged?.Invoke(codec);
        }
    }

    private static bool SameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
               uri.Scheme == "http" && uri.Host.Equals(context.Request.Host.Host, StringComparison.OrdinalIgnoreCase) &&
               uri.Port == context.Request.Host.Port;
    }

    private List<ConnectionOption> GetConnectionOptions()
    {
        var options = new List<ConnectionOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderByDescending(nic => nic.GetIPProperties().GatewayAddresses.Count > 0)
            .ThenByDescending(nic => nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet);
        foreach (var nic in interfaces)
        {
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = address.Address;
                var value = ip.ToString();
                if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip) ||
                    value.StartsWith("169.254.", StringComparison.Ordinal) || !seen.Add(value)) continue;
                options.Add(new ConnectionOption($"{value} - {nic.Name}", value,
                    $"http://{value}:{_port}/?key={_key}"));
            }
        }
        if (options.Count == 0)
            options.Add(new ConnectionOption("This PC - 127.0.0.1", "127.0.0.1",
                $"http://127.0.0.1:{_port}/?key={_key}"));
        return options;
    }

    private static string LoadOrCreateKey()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AppRemote");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "pairing.key");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length == 48 && existing.All(Uri.IsHexDigit)) return existing.ToLowerInvariant();
        }
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        File.WriteAllText(path, key);
        return key;
    }

    private static string CreateShortKey(string secret)
    {
        var hash = SHA256.HashData(Convert.FromHexString(secret));
        return Convert.ToBase64String(hash, 0, 6).Replace('+', '-').Replace('/', '_');
    }

    public async ValueTask DisposeAsync()
    {
        await _clientTransition.WaitAsync();
        try
        {
            _input.Release();
            lock (_clientGate) _activeClient = null;
            Interlocked.Exchange(ref _videoPeer, null)?.Dispose();
            _session.SetClientConnected(false);
            await StopIdleAndroidAsync();
        }
        finally { _clientTransition.Release(); }
        if (_app is not null) await _app.DisposeAsync();
    }
}
