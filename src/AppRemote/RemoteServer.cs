using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AppRemote.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AppRemote;

internal sealed class RemoteServer : IAsyncDisposable
{
    private const int Port = 8765;
    private readonly RemoteSession _session;
    private readonly InputController _input;
    private readonly string _legacyKey;
    private readonly string _key;
    private WebApplication? _app;
    private VideoPeer? _videoPeer;
    private volatile bool _videoActive;
    private int _busy;

    public event Action<bool>? ClientChanged;
    public event Action<bool>? VideoModeChanged;
    public string? Url { get; private set; }

    public RemoteServer(RemoteSession session)
    {
        _session = session;
        _input = new InputController(session);
        _legacyKey = LoadOrCreateKey();
        _key = CreateShortKey(_legacyKey);
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(Port));
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
            if (!HasKey(context) || !SameOrigin(context) || Volatile.Read(ref _busy) == 0)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (context.Request.ContentLength is > 65536) return Results.BadRequest();
            using var reader = new StreamReader(context.Request.Body);
            var offer = await reader.ReadToEndAsync(context.RequestAborted);
            if (offer.Length > 65536) return Results.BadRequest();
            VideoPeer? peer = null;
            try
            {
                peer = new VideoPeer(_session);
                peer.ActiveChanged += active =>
                {
                    if (ReferenceEquals(Volatile.Read(ref _videoPeer), peer)) SetVideoMode(active);
                };
                var previous = Interlocked.Exchange(ref _videoPeer, peer);
                previous?.Dispose();
                var answer = await peer.AcceptOfferAsync(offer);
                return Results.Text(answer, "application/sdp");
            }
            catch (Exception ex)
            {
                peer?.Dispose();
                return Results.Problem(ex.Message, statusCode: 503);
            }
        });

        await app.StartAsync();
        _app = app;
        var ip = GetLanAddress();
        Url = $"http://{ip}:{Port}/?key={_key}";
    }

    private async Task HandleSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest || !HasKey(context) || !SameOrigin(context))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        try
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            ClientChanged?.Invoke(true);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            var sender = SendFramesAsync(socket, linked.Token);
            var buffer = new byte[4096];
            var closeRequested = false;
            try
            {
                while (socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
                {
                    var message = await socket.ReceiveAsync(buffer, linked.Token);
                    if (message.MessageType == WebSocketMessageType.Close) { closeRequested = true; break; }
                    if (message.MessageType != WebSocketMessageType.Text || !message.EndOfMessage) continue;
                    try { _input.Handle(Encoding.UTF8.GetString(buffer, 0, message.Count)); }
                    catch (JsonException) { /* Ignore malformed controls. */ }
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            finally
            {
                linked.Cancel();
                try { await sender; } catch (OperationCanceledException) { }
                if (closeRequested && socket.State == WebSocketState.CloseReceived)
                {
                    try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); }
                    catch (WebSocketException) { }
                }
                _input.Release();
                Interlocked.Exchange(ref _videoPeer, null)?.Dispose();
                SetVideoMode(false);
                ClientChanged?.Invoke(false);
            }
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private async Task SendFramesAsync(WebSocket socket, CancellationToken stop)
    {
        long lastSequence = -1;
        var waitingSent = false;
        while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
        {
            var frame = _session.LatestFrame;
            if (frame is null)
            {
                if (!waitingSent)
                {
                    var status = Encoding.UTF8.GetBytes("{\"type\":\"waiting\"}");
                    await socket.SendAsync(status, WebSocketMessageType.Text, true, stop);
                    waitingSent = true;
                }
            }
            else if (!_videoActive && frame.Sequence != lastSequence)
            {
                await socket.SendAsync(frame.Jpeg, WebSocketMessageType.Binary, true, stop);
                lastSequence = frame.Sequence;
                waitingSent = false;
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

    private void SetVideoMode(bool active)
    {
        if (_videoActive == active) return;
        _videoActive = active;
        _session.SetVideoActive(active);
        VideoModeChanged?.Invoke(active);
    }

    private static bool SameOrigin(HttpContext context)
    {
        var origin = context.Request.Headers.Origin.ToString();
        return Uri.TryCreate(origin, UriKind.Absolute, out var uri) &&
               uri.Scheme == "http" && uri.Host.Equals(context.Request.Host.Host, StringComparison.OrdinalIgnoreCase) &&
               uri.Port == context.Request.Host.Port;
    }

    private static string GetLanAddress()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .OrderByDescending(nic => nic.GetIPProperties().GatewayAddresses.Count > 0)
            .ThenByDescending(nic => nic.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet);
        foreach (var nic in interfaces)
        {
            foreach (var address in nic.GetIPProperties().UnicastAddresses)
            {
                var ip = address.Address;
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) &&
                    !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal)) return ip.ToString();
            }
        }
        return "127.0.0.1";
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
        _input.Release();
        Interlocked.Exchange(ref _videoPeer, null)?.Dispose();
        if (_app is not null) await _app.DisposeAsync();
    }
}
