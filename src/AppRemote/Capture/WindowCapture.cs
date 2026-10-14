using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

namespace AppRemote.Capture;

internal sealed class WindowCapture : IDisposable
{
    private readonly GraphicsCaptureItem _item;
    private readonly IDirect3DDevice _device;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly SemaphoreSlim _processing = new(1, 1);
    private SizeInt32 _size;
    private bool _disposed;
    private long _lastEncodedTick;
    private long _lastErrorTick;

    public event Action<byte[], int, int>? FrameReady;
    public event Action<byte[], int, int>? RawFrameReady;
    public bool JpegEnabled { get; set; } = true;
    public event Action? Closed;
    public event Action<string>? Faulted;

    public WindowCapture(nint hwnd)
    {
        _item = CreateItem(hwnd);
        _size = _item.Size;
        _device = CreateDevice();
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _size);
        _session = _pool.CreateCaptureSession(_item);
        _pool.FrameArrived += OnFrameArrived;
        _item.Closed += OnClosed;
    }

    public void Start() => _session.StartCapture();

    private void OnClosed(GraphicsCaptureItem sender, object args) => Closed?.Invoke();

    private async void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (!_processing.Wait(0))
        {
            try { using var skipped = sender.TryGetNextFrame(); } catch (Exception) { }
            return;
        }

        try
        {
            SizeInt32 size;
            using (var frame = sender.TryGetNextFrame())
            {
                if (frame is null || _disposed) return;
                size = frame.ContentSize;
                if (size.Width <= 0 || size.Height <= 0) return;
                var now = Environment.TickCount64;
                if (now - _lastEncodedTick < 80) return;
                _lastEncodedTick = now;

                using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface);
                if (bitmap.PixelWidth == size.Width && bitmap.PixelHeight == size.Height)
                {
                    var rawHandler = RawFrameReady;
                    if (rawHandler is not null)
                    {
                        var raw = new byte[checked(size.Width * size.Height * 4)];
                        var buffer = new global::Windows.Storage.Streams.Buffer((uint)raw.Length);
                        bitmap.CopyToBuffer(buffer);
                        using var rawReader = DataReader.FromBuffer(buffer);
                        rawReader.ReadBytes(raw);
                        rawHandler(raw, size.Width, size.Height);
                    }
                    if (JpegEnabled)
                    {
                        using var stream = new InMemoryRandomAccessStream();
                        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
                        encoder.SetSoftwareBitmap(bitmap);
                        var scale = Math.Min(1.0, 1280.0 / Math.Max(size.Width, size.Height));
                        encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, Math.Round(size.Width * scale));
                        encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, Math.Round(size.Height * scale));
                        await encoder.FlushAsync();
                        stream.Seek(0);
                        using var reader = new DataReader(stream);
                        await reader.LoadAsync((uint)stream.Size);
                        var bytes = new byte[(int)stream.Size];
                        reader.ReadBytes(bytes);
                        FrameReady?.Invoke(bytes, size.Width, size.Height);
                    }
                }
            }
            if (size.Width != _size.Width || size.Height != _size.Height)
            {
                _size = size;
                _pool.Recreate(_device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
            }
        }
        catch (Exception ex)
        {
            var now = Environment.TickCount64;
            if (!_disposed && now - _lastErrorTick > 3000)
            {
                _lastErrorTick = now;
                Faulted?.Invoke(ex.Message);
            }
        }
        finally
        {
            _processing.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _pool.FrameArrived -= OnFrameArrived;
        _item.Closed -= OnClosed;
        _session.Dispose();
        _pool.Dispose();
        (_device as IDisposable)?.Dispose();
    }

    private static GraphicsCaptureItem CreateItem(nint hwnd)
    {
        const string runtimeClass = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var name = runtimeClass.AsSpan();
        Marshal.ThrowExceptionForHR(WindowsCreateString(name.ToString(), name.Length, out var hstring));
        try
        {
            var iid = typeof(IGraphicsCaptureItemInterop).GUID;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hstring, ref iid, out var factory));
            try
            {
                var interop = (IGraphicsCaptureItemInterop)Marshal.GetTypedObjectForIUnknown(factory, typeof(IGraphicsCaptureItemInterop));
                var itemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                var itemPtr = interop.CreateForWindow(hwnd, ref itemId);
                try { return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr); }
                finally { Marshal.Release(itemPtr); }
            }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(hstring); }
    }

    private static IDirect3DDevice CreateDevice()
    {
        const uint bgraSupport = 0x20;
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(0, 1, 0, bgraSupport, 0, 0, 7,
            out var nativeDevice, out _, out var context));
        if (context != 0) Marshal.Release(context);
        try
        {
            var dxgiGuid = new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(nativeDevice, in dxgiGuid, out var dxgiDevice));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable));
                try { return MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgiDevice); }
        }
        finally { Marshal.Release(nativeDevice); }
    }

    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, ref Guid iid);
        nint CreateForMonitor(nint monitor, ref Guid iid);
    }

    [DllImport("combase.dll")]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, int length, out nint hstring);
    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);
    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(nint runtimeClassId, ref Guid iid, out nint factory);
    [DllImport("d3d11.dll")]
    private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        nint featureLevels, uint featureLevelsCount, uint sdkVersion, out nint device, out int featureLevel, out nint context);
    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);
}
