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

    public void Select(WindowEntry entry)
    {
        if (!NativeWindows.IsSameWindow(entry)) throw new InvalidOperationException("The window has closed.");
        var next = new WindowCapture(entry.Handle);
        next.FrameReady += (bytes, width, height) => OnFrame(next, bytes, width, height);
        next.RawFrameReady += (bytes, width, height) => OnRawFrame(next, bytes, width, height);
        next.Closed += () => OnClosed(next);
        next.Faulted += message => CaptureFaulted?.Invoke(message);
        try { next.Start(); }
        catch { next.Dispose(); throw; }

        WindowCapture? previous;
        lock (_gate)
        {
            previous = _capture;
            _capture = next;
            next.JpegEnabled = !_videoActive;
            _window = entry;
            _frame = null;
        }
        previous?.Dispose();
        Changed?.Invoke();
        NativeWindows.Activate(entry.Handle);
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
