using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AppRemote.Android;

internal sealed class AndroidSource : IAsyncDisposable
{
    private readonly string _adb;
    private readonly string _serial;
    private readonly string _remotePath;
    private readonly int _port;
    private readonly Process _server;
    private readonly TcpClient _socket;
    private readonly TcpClient _control;
    private readonly SemaphoreSlim _controlLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _reader;
    private byte[]? _config;
    private readonly VideoPreference _codec;
    private bool _touchDown;
    private int _lastX;
    private int _lastY;

    public event Action<byte[], long, int, int>? Frame;
    public event Action<string>? Failed;
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string Serial => _serial;
    public VideoPreference Codec => _codec;

    private AndroidSource(string adb, string serial, string remotePath, int port,
        Process server, TcpClient socket, TcpClient control, VideoPreference codec)
    {
        _adb = adb;
        _serial = serial;
        _remotePath = remotePath;
        _port = port;
        _server = server;
        _socket = socket;
        _control = control;
        _codec = codec;
        _reader = Task.Run(ReadFramesAsync);
    }

    public static async Task<AndroidSource> StartAsync(string adb, string serial, string serverFile, VideoPreference codec)
    {
        if (codec is not (VideoPreference.H264 or VideoPreference.H265)) throw new ArgumentOutOfRangeException(nameof(codec));
        if (Path.IsPathFullyQualified(adb) && !File.Exists(adb))
            throw new FileNotFoundException("ADB executable was not found.", adb);
        if (!File.Exists(serverFile)) throw new FileNotFoundException("scrcpy server was not found.", serverFile);
        var scid = RandomNumberGenerator.GetInt32(1, int.MaxValue).ToString("x8", CultureInfo.InvariantCulture);
        var remotePath = $"/data/local/tmp/appremote-scrcpy-{scid}.jar";
        var port = 0;
        Process? server = null;
        TcpClient? socket = null;
        TcpClient? control = null;
        try
        {
            await RunAdbAsync(adb, serial, "push", serverFile, remotePath);
            var output = await RunAdbAsync(adb, serial, "forward", "tcp:0", "localabstract:scrcpy_" + scid);
            if (!int.TryParse(output.Trim(), out port)) throw new IOException("ADB did not return a forwarded port: " + output);
            var info = AdbStartInfo(adb, serial, "shell", "CLASSPATH=" + remotePath,
                "app_process", "/", "com.genymobile.scrcpy.Server", "5.0.1",
                "scid=" + scid, "tunnel_forward=true", "audio=false", "control=true",
                "video_codec=" + (codec == VideoPreference.H265 ? "h265" : "h264"),
                "video_bit_rate=4000000", "max_size=1280",
                "max_fps=30", "video_codec_options=i-frame-interval:int=1",
                "send_device_meta=false");
            server = Process.Start(info) ?? throw new IOException("Could not start the scrcpy server.");
            server.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine("scrcpy: " + e.Data); };
            server.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine("scrcpy: " + e.Data); };
            server.BeginErrorReadLine();
            server.BeginOutputReadLine();
            socket = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (true)
            {
                try
                {
                    await socket.ConnectAsync("127.0.0.1", port, timeout.Token);
                    var dummy = new byte[1];
                    await socket.GetStream().ReadExactlyAsync(dummy, timeout.Token);
                    break;
                }
                catch (Exception ex) when ((ex is SocketException or EndOfStreamException) &&
                                           !timeout.IsCancellationRequested && !server.HasExited)
                {
                    socket.Dispose();
                    socket = new TcpClient();
                    await Task.Delay(150, timeout.Token);
                }
            }
            control = new TcpClient();
            await control.ConnectAsync("127.0.0.1", port, timeout.Token);
            return new AndroidSource(adb, serial, remotePath, port, server, socket, control, codec);
        }
        catch
        {
            socket?.Dispose();
            control?.Dispose();
            if (server is not null) { if (!server.HasExited) server.Kill(); server.Dispose(); }
            if (port != 0) await TryAdbAsync(adb, serial, "forward", "--remove", "tcp:" + port);
            await TryAdbAsync(adb, serial, "shell", "rm", "-f", remotePath);
            throw;
        }
    }

    public async Task HandleInputAsync(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var typeValue)) return;
        var type = typeValue.GetString();
        if (type == "move" && !_touchDown) return;
        byte[]? message = null;
        if (type is "down" or "move" or "up" or "right" &&
            root.TryGetProperty("x", out var xValue) && root.TryGetProperty("y", out var yValue) &&
            xValue.TryGetDouble(out var nx) && yValue.TryGetDouble(out var ny) &&
            double.IsFinite(nx) && double.IsFinite(ny) &&
            nx is >= 0 and <= 1 && ny is >= 0 and <= 1 && Width > 0 && Height > 0)
        {
            if (type == "right") { message = KeyMessage(4, 0); }
            else
            {
                var x = Math.Clamp((int)(nx * Width), 0, Width - 1);
                var y = Math.Clamp((int)(ny * Height), 0, Height - 1);
                message = TouchMessage(type switch { "down" => 0, "up" => 1, _ => 2 }, x, y);
            }
        }
        else if (type is "key" && root.TryGetProperty("value", out var keyValue))
        {
            var code = KeyCode(keyValue.GetString());
            if (code != 0) message = KeyMessage(code, 0);
        }
        else if (type is "text" && root.TryGetProperty("value", out var textValue))
        {
            var value = textValue.GetString();
            if (!string.IsNullOrEmpty(value) && Encoding.UTF8.GetByteCount(value) <= 300)
            {
                var bytes = Encoding.UTF8.GetBytes(value);
                message = new byte[5 + bytes.Length];
                message[0] = 1;
                BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(1), bytes.Length);
                bytes.CopyTo(message, 5);
            }
        }
        else if (type == "wheel" && root.TryGetProperty("delta", out var deltaValue) &&
                 deltaValue.TryGetDouble(out var delta) && double.IsFinite(delta) && Width > 0 && Height > 0)
        {
            message = new byte[21];
            message[0] = 3;
            BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(1), Width / 2);
            BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(5), Height / 2);
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(9), (ushort)Width);
            BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(11), (ushort)Height);
            BinaryPrimitives.WriteInt16BigEndian(message.AsSpan(15), (short)Math.Clamp((int)(delta / 500 * 2048), -32767, 32767));
        }
        if (message is null) return;
        await _controlLock.WaitAsync(_stop.Token);
        try
        {
            await _control.GetStream().WriteAsync(message, _stop.Token);
            if (type is "down" or "move" or "up")
            {
                if (type != "move") _touchDown = type == "down";
                _lastX = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(10));
                _lastY = BinaryPrimitives.ReadInt32BigEndian(message.AsSpan(14));
            }
            if (type is "key" or "right")
                await _control.GetStream().WriteAsync(KeyMessage(type == "right" ? 4 :
                    KeyCode(root.GetProperty("value").GetString()), 1), _stop.Token);
        }
        finally { _controlLock.Release(); }
    }

    public async Task ReleaseInputAsync()
    {
        if (!_touchDown || _stop.IsCancellationRequested) return;
        try
        {
            await _controlLock.WaitAsync(_stop.Token);
            try
            {
                if (!_touchDown) return;
                await _control.GetStream().WriteAsync(TouchMessage(1, _lastX, _lastY), _stop.Token);
                _touchDown = false;
            }
            finally { _controlLock.Release(); }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private static int KeyCode(string? value) => value switch
    {
        "Backspace" => 67, "Enter" => 66, "Tab" => 61, "Escape" => 4,
        "ArrowUp" => 19, "ArrowDown" => 20, "ArrowLeft" => 21, "ArrowRight" => 22,
        _ => 0
    };

    private static byte[] KeyMessage(int code, byte action)
    {
        var message = new byte[14];
        message[0] = 0; message[1] = action;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(2), code);
        return message;
    }

    private byte[] TouchMessage(byte action, int x, int y)
    {
        var message = new byte[32];
        message[0] = 2; message[1] = action;
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(10), x);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(14), y);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(18), (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(20), (ushort)Height);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(22), action == 1 ? (ushort)0 : ushort.MaxValue);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(28), action == 1 ? 0 : 1);
        return message;
    }

    private async Task ReadFramesAsync()
    {
        try
        {
            var stream = _socket.GetStream();
            var codec = new byte[4];
            await stream.ReadExactlyAsync(codec, _stop.Token);
            if (!codec.AsSpan().SequenceEqual(_codec == VideoPreference.H265 ? "h265"u8 : "h264"u8))
                throw new IOException("Unexpected Android video codec: " + System.Text.Encoding.ASCII.GetString(codec));
            var header = new byte[12];
            while (!_stop.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, _stop.Token);
                var flagsAndPts = BinaryPrimitives.ReadUInt64BigEndian(header);
                if ((flagsAndPts & (1UL << 63)) != 0)
                {
                    Width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)));
                    Height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)));
                    _config = null;
                    continue;
                }
                var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8)));
                if (length is < 1 or > 8_000_000) throw new IOException("Invalid video packet length: " + length);
                var packet = new byte[length];
                await stream.ReadExactlyAsync(packet, _stop.Token);
                if ((flagsAndPts & (1UL << 62)) != 0) { _config = packet; continue; }
                var keyFrame = (flagsAndPts & (1UL << 61)) != 0;
                if (keyFrame && _config is { Length: > 0 })
                {
                    var combined = new byte[_config.Length + packet.Length];
                    _config.CopyTo(combined, 0);
                    packet.CopyTo(combined, _config.Length);
                    packet = combined;
                }
                Frame?.Invoke(packet, (long)(flagsAndPts & ((1UL << 61) - 1)), Width, Height);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _socket.Dispose();
        _control.Dispose();
        await _reader;
        if (!_server.HasExited) _server.Kill();
        _server.Dispose();
        await TryAdbAsync(_adb, _serial, "forward", "--remove", "tcp:" + _port);
        await TryAdbAsync(_adb, _serial, "shell", "rm", "-f", _remotePath);
        _stop.Dispose();
        _controlLock.Dispose();
    }

    private static ProcessStartInfo AdbStartInfo(string adb, string serial, params string[] args)
    {
        var info = new ProcessStartInfo(adb) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-s");
        info.ArgumentList.Add(serial);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        return info;
    }

    private static async Task<string> RunAdbAsync(string adb, string serial, params string[] args)
    {
        using var process = Process.Start(AdbStartInfo(adb, serial, args)) ?? throw new IOException("Could not start ADB.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var stdout = await output;
        var stderr = await error;
        if (process.ExitCode != 0) throw new IOException($"ADB {string.Join(' ', args)} failed: {stderr.Trim()}");
        return stdout;
    }

    private static async Task TryAdbAsync(string adb, string serial, params string[] args)
    {
        try { await RunAdbAsync(adb, serial, args); } catch (Exception) { }
    }
}
