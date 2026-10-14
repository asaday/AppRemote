using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using AppRemote.Windows;
using QRCoder;

namespace AppRemote;

public partial class MainWindow : Window
{
    private readonly RemoteSession _session = new();
    private readonly RemoteServer _server;
    private bool _clientConnected;
    private bool _h264Active;

    public MainWindow()
    {
        InitializeComponent();
        if (typeof(MainWindow).Assembly.GetName().Version is { } version)
        {
            var label = $"v{version.Major}.{version.Minor}.{version.Build}";
            VersionText.Text = label;
            Title = $"App Remote {label}";
        }
        _server = new RemoteServer(_session);
        _server.ClientChanged += connected => Dispatcher.BeginInvoke(() =>
        {
            _clientConnected = connected;
            if (!connected) _h264Active = false;
            UpdateStatus();
        });
        _server.VideoModeChanged += active => Dispatcher.BeginInvoke(() =>
        {
            _h264Active = active;
            UpdateStatus();
        });
        _session.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            SelectedText.Text = _session.Selected is { } selected ? $"Controlling: {selected.Title}" : "Window closed. Choose another window.";
            UpdateStatus();
        });
        _session.CaptureFaulted += message => Dispatcher.BeginInvoke(() => StatusText.Text = "Capture error: " + message);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        try
        {
            await _server.StartAsync();
            UrlText.Text = _server.Url ?? "";
            CopyUrlButton.IsEnabled = _server.Url is not null;
            var qr = new QRCodeGenerator();
            using var data = qr.CreateQrCode(_server.Url!, QRCodeGenerator.ECCLevel.M);
            var png = new PngByteQRCode(data).GetGraphic(5);
            using var stream = new MemoryStream(png);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            QrImage.Source = bitmap;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not start server: " + ex.Message;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private async void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_server.Url is null) return;
        try
        {
            Clipboard.SetText(_server.Url);
            CopyUrlButton.Content = "Copied!";
            await Task.Delay(1500);
            if (IsLoaded) CopyUrlButton.Content = "Copy URL";
        }
        catch (Exception)
        {
            CopyUrlButton.Content = "Copy failed";
        }
    }

    private void RefreshWindows()
    {
        var selected = (WindowList.SelectedItem as WindowEntry)?.Handle;
        WindowList.ItemsSource = NativeWindows.List(new WindowInteropHelper(this).Handle);
        if (selected is not null)
            WindowList.SelectedItem = WindowList.Items.Cast<WindowEntry>().FirstOrDefault(w => w.Handle == selected);
    }

    private void Select_Click(object sender, RoutedEventArgs e) => SelectWindow();
    private void WindowList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => SelectWindow();

    private void SelectWindow()
    {
        if (WindowList.SelectedItem is not WindowEntry entry) return;
        try
        {
            _session.Select(entry);
            SelectedText.Text = $"Controlling: {entry.Title}";
            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not select window: " + ex.Message;
            RefreshWindows();
        }
    }

    private void UpdateStatus()
    {
        StatusText.Text = _session.Selected is null
            ? _clientConnected ? "Phone connected - choose a window" : "Waiting for phone"
            : !_clientConnected ? "Window selected - waiting for phone"
            : _h264Active ? "Streaming window (H.264 / WebRTC)" : "Streaming window (JPEG / WebSocket)";
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _session.Dispose();
        await _server.DisposeAsync();
    }
}
