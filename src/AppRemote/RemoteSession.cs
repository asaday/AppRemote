using AppRemote.Capture;
using AppRemote.Windows;

namespace AppRemote;

internal sealed record CapturedFrame(byte[] Jpeg, int Width, int Height, long Sequence);

internal sealed class RemoteSession : IDisposable
{
    private readonly object _gate = new();
    private WindowCapture? _capture;
    private WindowEntry? _window;
    private CapturedFrame? _frame;
    private long _sequence;
    private bool _videoActive;
    private bool _clientConnected;

    public event Action? Changed;
    public event Action<string>? CaptureFaulted;
    public event Action<byte[], int, int>? RawFrameReady;

    public WindowEntry? Selected
    {
        get { lock (_gate) return _window; }
    }

    public CapturedFrame? LatestFrame
    {
        get { lock (_gate) return _frame; }
    }

    public void Select(WindowEntry entry, bool activate = true)
    {
        if (!NativeWindows.IsSameWindow(entry)) throw new InvalidOperationException("The window has closed.");
        WindowCapture? next = null;
        WindowCapture? previous;
        lock (_gate)
        {
            if (_clientConnected) next = CreateCapture(entry);
            previous = _capture;
            _capture = next;
            _window = entry;
            _frame = null;
        }
        previous?.Dispose();
        Changed?.Invoke();
        if (activate) NativeWindows.Activate(entry.Handle);
    }

    public void Stop()
    {
        WindowCapture? capture;
        lock (_gate)
        {
            capture = _capture;
            if (capture is null && _window is null) return;
            _capture = null;
            _window = null;
            _frame = null;
        }
        capture?.Dispose();
        Changed?.Invoke();
    }

    public void SetClientConnected(bool connected)
    {
        WindowCapture? previous;
        string? fault = null;
        var selectionLost = false;
        lock (_gate)
        {
            if (_clientConnected == connected) return;
            _clientConnected = connected;
            previous = _capture;
            _capture = null;
            _frame = null;
            if (connected && _window is { } entry)
            {
                try
                {
                    if (!NativeWindows.IsSameWindow(entry)) throw new InvalidOperationException("The window has closed.");
                    _capture = CreateCapture(entry);
                }
                catch (Exception ex)
                {
                    fault = ex.Message;
                    _window = null;
                    selectionLost = true;
                }
            }
        }
        previous?.Dispose();
        if (fault is not null) CaptureFaulted?.Invoke(fault);
        if (selectionLost) Changed?.Invoke();
    }

    private WindowCapture CreateCapture(WindowEntry entry)
    {
        var capture = new WindowCapture(entry.Handle);
        capture.FrameReady += (bytes, width, height) => OnFrame(capture, bytes, width, height);
        capture.RawFrameReady += (bytes, width, height) => OnRawFrame(capture, bytes, width, height);
        capture.Closed += () => OnClosed(capture);
        capture.Faulted += message => CaptureFaulted?.Invoke(message);
        capture.JpegEnabled = !_videoActive;
        try { capture.Start(); return capture; }
        catch { capture.Dispose(); throw; }
    }

    private void OnFrame(WindowCapture source, byte[] bytes, int width, int height)
    {
        lock (_gate)
        {
            if (_capture != source) return;
            _frame = new CapturedFrame(bytes, width, height, ++_sequence);
        }
    }

    private void OnRawFrame(WindowCapture source, byte[] bytes, int width, int height)
    {
        lock (_gate)
        {
            if (_capture != source) return;
        }
        RawFrameReady?.Invoke(bytes, width, height);
    }

    public void SetVideoActive(bool active)
    {
        lock (_gate)
        {
            _videoActive = active;
            if (_capture is not null) _capture.JpegEnabled = !active;
        }
    }

    private void OnClosed(WindowCapture source)
    {
        lock (_gate)
        {
            if (_capture != source) return;
            _capture = null;
            _window = null;
            _frame = null;
        }
        source.Dispose();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        WindowCapture? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
            _window = null;
            _frame = null;
        }
        capture?.Dispose();
    }
}
