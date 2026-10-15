using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using AppRemote.Windows;
using AppRemote.Android;
using System.Net;
using QRCoder;
using Forms = System.Windows.Forms;

namespace AppRemote;

public partial class MainWindow : Window
{
    private readonly RemoteSession _session = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private AndroidSource? _androidSource;
    private bool _startingAndroid;
    private RemoteServer _server;
    private bool _initializingSettings = true;
    private bool _clientConnected;
    private VideoPreference? _activeVideo;
    private string? _videoError;
    private string? _displayUrl;
    private bool _closing;
    private bool _exitRequested;
    private bool _trayTipShown;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _trayStopItem;
    private readonly Forms.ToolStripMenuItem _trayConsoleItem;
    private readonly System.Drawing.Icon? _trayImage;

    public MainWindow()
    {
        InitializeComponent();
        System.Windows.Application.Current.SessionEnding += (_, _) => _exitRequested = true;
        RestoreLastWindowCheck.IsChecked = _settings.RestoreLastWindow;
        VideoModePicker.SelectedIndex = (int)_settings.VideoMode;
        AndroidVideoModePicker.SelectedIndex = (int)_settings.AndroidVideoMode;
        AndroidAddressTextBox.Text = _settings.AndroidAddress ?? "";
        AdbPathTextBox.Text = AdbClient.Find(_settings.AdbPath) ?? "";
        TouchModePicker.SelectedIndex = (int)_settings.TouchMode;
        BackgroundInputCheck.IsChecked = _settings.BackgroundInput;
        PortTextBox.Text = _settings.Port.ToString();
        if (typeof(MainWindow).Assembly.GetName().Version is { } version)
        {
            var label = $"v{version.Major}.{version.Minor}.{version.Build}";
            VersionText.Text = label;
            Title = $"App Remote {label}";
        }
        _server = new RemoteServer(_session, _settings.Port);
        _server.SetVideoPreference(_settings.VideoMode);
        _server.SetTouchMode(_settings.TouchMode);
        _server.SetBackgroundInput(_settings.BackgroundInput);
        _initializingSettings = false;
        _trayImage = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        _trayStopItem = new Forms.ToolStripMenuItem("Stop sharing", null, (_, _) => Dispatcher.BeginInvoke(StopSharing));
        _trayStopItem.Enabled = false;
        _trayConsoleItem = new Forms.ToolStripMenuItem("Keep streaming after RDP", null,
            (_, _) => Dispatcher.BeginInvoke(SwitchToConsole));
        var trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("Open App Remote", null, (_, _) => Dispatcher.BeginInvoke(RestoreFromTray));
        trayMenu.Items.Add(_trayStopItem);
        trayMenu.Items.Add(_trayConsoleItem);
        trayMenu.Opening += (_, _) => _trayConsoleItem.Enabled = IsRemoteDesktopSession();
        trayMenu.Items.Add(new Forms.ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(ExitFromTray));
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayImage ?? System.Drawing.SystemIcons.Application,
            Text = "App Remote",
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(RestoreFromTray);
        AttachServer(_server);
        _session.Changed += () => Dispatcher.BeginInvoke(() =>
        {
            if (_androidSource is null)
                SelectedText.Text = _session.Selected is { } selected ? $"Selected window: {selected.Title}" : "No source selected";
            StopButton.IsEnabled = _session.Selected is not null || _androidSource is not null;
            _trayStopItem.Enabled = StopButton.IsEnabled;
            UpdateStatus();
        });
        _session.CaptureFaulted += message => Dispatcher.BeginInvoke(() => StatusText.Text = "Capture error: " + message);
    }

    private void AttachServer(RemoteServer server)
    {
        server.ClientChanged += connected => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_server, server)) return;
            _clientConnected = connected;
            ClientStatusText.Text = connected ? "Browser connected" : "No browser connected";
            if (!connected) { _activeVideo = null; _videoError = null; }
            UpdateStatus();
        });
        server.VideoModeChanged += codec => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_server, server)) return;
            _activeVideo = codec;
            if (codec is not null) _videoError = null;
            UpdateStatus();
        });
        server.VideoFailed += message => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_server, server)) return;
            _videoError = message;
            UpdateStatus();
        });
        server.AndroidSourceChanged += source => Dispatcher.BeginInvoke(() =>
        {
            if (!ReferenceEquals(_server, server) || !ReferenceEquals(server.CurrentAndroidSource, source)) return;
            _androidSource = source;
            AndroidStatusText.Text = $"Ready: {source.Serial} ({(source.Codec == VideoPreference.H265 ? "H.265" : "H.264")})";
            UpdateStatus();
        });
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        await RefreshDevicesAsync();
        try
        {
            await _server.StartAsync();
            RefreshConnectionOptions();
            UpdateStatus();
            if (_settings.LastSource == SourceKind.Android && _settings.RestoreLastWindow)
            {
                var remembered = DeviceList.Items.Cast<AndroidDevice>()
                    .FirstOrDefault(device => device.Serial == _settings.LastAndroidSerial && device.State == "device");
                if (remembered is null && _settings.LastAndroidSerial is { } serial &&
                    serial == _settings.AndroidAddress && ValidAddress(serial) &&
                    AdbClient.Find(_settings.AdbPath) is { } adb)
                {
                    try
                    {
                        await AdbClient.RunAsync(adb, null, "connect", serial);
                        await RefreshDevicesAsync();
                        remembered = DeviceList.Items.Cast<AndroidDevice>()
                            .FirstOrDefault(device => device.Serial == serial && device.State == "device");
                    }
                    catch (Exception ex) { AndroidStatusText.Text = "Could not reconnect Android: " + ex.Message; }
                }
                if (remembered is not null) { DeviceList.SelectedItem = remembered; await SelectAndroidAsync(remembered.Serial, true); }
            }
            else TryRestoreLastWindow();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not start server: " + ex.Message;
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshWindows();

    private void ConnectionMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_server is not null) UpdateConnectionUrl();
    }

    private void RefreshConnectionOptions(string? preferredAddress = null)
    {
        ConnectionMode.Items.Clear();
        foreach (var option in _server.ConnectionOptions)
            ConnectionMode.Items.Add(new ComboBoxItem { Content = option.Label, Tag = option, ToolTip = option.Label });
        var preferred = ConnectionMode.Items.Cast<ComboBoxItem>().FirstOrDefault(item =>
            ((ConnectionOption)item.Tag).Address == preferredAddress);
        ConnectionMode.SelectedItem = preferred ?? ConnectionMode.Items.Cast<ComboBoxItem>().FirstOrDefault();
        ConnectionMode.Visibility = _server.ConnectionOptions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdateConnectionUrl();
    }

    private void UpdateConnectionUrl()
    {
        var option = (ConnectionMode.SelectedItem as ComboBoxItem)?.Tag as ConnectionOption;
        _displayUrl = option?.Url ?? _server.Url;
        UrlText.Text = _displayUrl ?? "";
        CopyUrlButton.IsEnabled = _displayUrl is not null;
        if (_displayUrl is null) return;
        using var data = new QRCodeGenerator().CreateQrCode(_displayUrl, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(5);
        using var stream = new MemoryStream(png);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        QrImage.Source = bitmap;
    }

    private async void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_displayUrl is null) return;
        try
        {
            System.Windows.Clipboard.SetText(_displayUrl);
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
    private void Stop_Click(object sender, RoutedEventArgs e)
        => StopSharing();

    private async void StopSharing()
    {
        var android = _server.SetAndroidSource(null);
        _androidSource = null;
        _session.Stop();
        if (android is not null) await android.DisposeAsync();
        _settings.Forget();
        SaveSettings();
        SelectedText.Text = "No source selected";
        StopButton.IsEnabled = false;
        _trayStopItem.Enabled = false;
        UpdateStatus();
    }
    private void RestoreLastWindow_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingSettings) return;
        _settings.RestoreLastWindow = RestoreLastWindowCheck.IsChecked == true;
        if (_settings.RestoreLastWindow && _androidSource is { } android)
            _settings.RememberAndroid(android.Serial);
        else if (_settings.RestoreLastWindow && _session.Selected is { } selected)
        {
            if (!_settings.Remember(selected)) _settings.Forget();
        }
        if (!_settings.RestoreLastWindow) _settings.Forget();
        SaveSettings();
    }
    private void VideoMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingSettings || VideoModePicker.SelectedIndex < 0) return;
        _settings.VideoMode = (VideoPreference)VideoModePicker.SelectedIndex;
        _videoError = null;
        _activeVideo = null;
        if (_androidSource is null) _server.SetVideoPreference(_settings.VideoMode);
        SaveSettings();
        UpdateStatus();
    }
    private async void AndroidVideoMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingSettings || AndroidVideoModePicker.SelectedIndex < 0) return;
        _settings.AndroidVideoMode = (VideoPreference)AndroidVideoModePicker.SelectedIndex;
        SaveSettings();
        if (_androidSource is { } source) await SelectAndroidAsync(source.Serial, false);
    }
    private void TouchMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingSettings || TouchModePicker.SelectedIndex < 0) return;
        _settings.TouchMode = (TouchInputMode)TouchModePicker.SelectedIndex;
        _server.SetTouchMode(_settings.TouchMode);
        SaveSettings();
    }
    private void BackgroundInput_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingSettings) return;
        _settings.BackgroundInput = BackgroundInputCheck.IsChecked == true;
        _server.SetBackgroundInput(_settings.BackgroundInput);
        SaveSettings();
    }
    private void PortTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; ApplyPort_Click(sender, e); }
    }
    private async void ApplyPort_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortTextBox.Text, out var port) || port is < 1 or > 65535)
        {
            StatusText.Text = "Enter a port from 1 to 65535.";
            return;
        }
        if (port == _settings.Port) { UpdateStatus(); return; }
        ApplyPortButton.IsEnabled = false;
        var next = new RemoteServer(_session, port);
        next.SetAndroidSource(_androidSource, AndroidFactory());
        next.SetVideoPreference(_androidSource is null ? _settings.VideoMode : _settings.AndroidVideoMode);
        next.SetTouchMode(_settings.TouchMode);
        next.SetBackgroundInput(_settings.BackgroundInput);
        try
        {
            await next.StartAsync();
            if (_closing) { await next.DisposeAsync(); return; }
            var previous = _server;
            var address = (ConnectionMode.SelectedItem as ComboBoxItem)?.Tag is ConnectionOption option
                ? option.Address : null;
            _server = next;
            AttachServer(next);
            _clientConnected = false;
            ClientStatusText.Text = "No browser connected";
            _activeVideo = null;
            _videoError = null;
            RefreshConnectionOptions(address);
            _settings.Port = port;
            SaveSettings();
            UpdateStatus();
            await previous.DisposeAsync();
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_server, next)) await next.DisposeAsync();
            StatusText.Text = "Could not use port " + port + ": " + ex.Message;
        }
        finally { ApplyPortButton.IsEnabled = true; }
    }
    private void WindowList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => SelectWindow();

    private void SelectWindow()
    {
        if (WindowList.SelectedItem is not WindowEntry entry) return;
        try
        {
            _session.Select(entry);
            var android = _server.SetAndroidSource(null);
            _androidSource = null;
            _server.SetVideoPreference(_settings.VideoMode);
            _server.SetTouchMode(_settings.TouchMode);
            if (android is not null) _ = android.DisposeAsync();
            if (_settings.RestoreLastWindow)
            {
                if (!_settings.Remember(entry)) _settings.Forget();
                SaveSettings();
            }
            SelectedText.Text = $"Selected window: {entry.Title}";
            StopButton.IsEnabled = true;
            _trayStopItem.Enabled = true;
            UpdateStatus();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Could not select window: " + ex.Message;
            RefreshWindows();
        }
    }

    private void TryRestoreLastWindow()
    {
        if (!_settings.RestoreLastWindow || _settings.LastSource != SourceKind.Windows) return;
        var match = _settings.FindLastWindow(WindowList.Items.Cast<WindowEntry>());
        if (match is null) return;
        try
        {
            WindowList.SelectedItem = match;
            _session.Select(match, activate: false);
            SelectedText.Text = $"Selected window: {match.Title}";
            StopButton.IsEnabled = true;
            _trayStopItem.Enabled = true;
            UpdateStatus();
        }
        catch (Exception)
        {
            RefreshWindows();
        }
    }

    private async void RefreshDevices_Click(object sender, RoutedEventArgs e) => await RefreshDevicesAsync();

    private async Task RefreshDevicesAsync()
    {
        var adb = AdbClient.Find(_settings.AdbPath);
        AdbPathTextBox.Text = adb ?? "";
        if (adb is null)
        {
            DeviceList.ItemsSource = null;
            AndroidStatusText.Text = "ADB was not found. Select adb.exe from Android SDK Platform Tools.";
            return;
        }
        try
        {
            var selected = (DeviceList.SelectedItem as AndroidDevice)?.Serial;
            DeviceList.ItemsSource = await AdbClient.DevicesAsync(adb);
            if (selected is not null)
                DeviceList.SelectedItem = DeviceList.Items.Cast<AndroidDevice>()
                    .FirstOrDefault(device => device.Serial == selected);
            AndroidStatusText.Text = DeviceList.Items.Count == 0
                ? "No devices found. Enable debugging and authorize this PC."
                : $"{DeviceList.Items.Count} device(s) found.";
        }
        catch (Exception ex) { AndroidStatusText.Text = "ADB failed: " + ex.Message; }
    }

    private async void SelectDevice_Click(object sender, RoutedEventArgs e)
    {
        if (DeviceList.SelectedItem is not AndroidDevice device) return;
        if (device.State != "device") { AndroidStatusText.Text = $"Device is {device.State}. Authorize this PC first."; return; }
        await SelectAndroidAsync(device.Serial, false);
    }

    private async Task SelectAndroidAsync(string serial, bool restoring)
    {
        if (_startingAndroid) return;
        var adb = AdbClient.Find(_settings.AdbPath);
        if (adb is null) { AndroidStatusText.Text = "Select adb.exe first."; return; }
        _startingAndroid = true;
        SelectDeviceButton.IsEnabled = false;
        AndroidStatusText.Text = "Starting Android video...";
        try
        {
            var serverFile = await ScrcpyServerFile.GetAsync();
            var mode = _settings.AndroidVideoMode == VideoPreference.H265
                ? VideoPreference.H265 : VideoPreference.H264;
            AndroidSource source;
            try { source = await AndroidSource.StartAsync(adb, serial, serverFile, mode); }
            catch when (_settings.AndroidVideoMode == VideoPreference.Auto && mode == VideoPreference.H265)
            { source = await AndroidSource.StartAsync(adb, serial, serverFile, VideoPreference.H264); }
            if (_closing) { await source.DisposeAsync(); return; }
            _androidSource = source;
            _session.Stop();
            var previous = _server.SetAndroidSource(source, AndroidFactory());
            _server.SetVideoPreference(_settings.AndroidVideoMode);
            _server.SetTouchMode(TouchInputMode.Direct);
            if (previous is not null) await previous.DisposeAsync();
            if (_settings.RestoreLastWindow && !restoring)
            { _settings.RememberAndroid(serial); SaveSettings(); }
            SourceTabs.SelectedIndex = 1;
            SelectedText.Text = "Selected Android: " + serial;
            StopButton.IsEnabled = true;
            _trayStopItem.Enabled = true;
            AndroidStatusText.Text = $"Ready: {serial} ({(source.Codec == VideoPreference.H265 ? "H.265" : "H.264")})";
            UpdateStatus();
        }
        catch (Exception ex) { AndroidStatusText.Text = "Could not start Android: " + ex.Message; }
        finally { _startingAndroid = false; SelectDeviceButton.IsEnabled = true; }
    }

    private Func<VideoPreference, Task<AndroidSource>>? AndroidFactory()
    {
        if (_androidSource is null) return null;
        var serial = _androidSource.Serial;
        var adb = AdbClient.Find(_settings.AdbPath);
        if (adb is null) return null;
        return async codec => await AndroidSource.StartAsync(adb, serial,
            await ScrcpyServerFile.GetAsync(), codec);
    }

    private static bool ValidAddress(string value)
        => IPEndPoint.TryParse(value, out var endpoint) && endpoint.Port is > 0 and <= 65535 &&
           !IPAddress.IsLoopback(endpoint.Address);

    private async void ConnectDevice_Click(object sender, RoutedEventArgs e)
    {
        var address = AndroidAddressTextBox.Text.Trim();
        if (!ValidAddress(address)) { AndroidStatusText.Text = "Enter a valid IP:port."; return; }
        var adb = AdbClient.Find(_settings.AdbPath);
        if (adb is null) { AndroidStatusText.Text = "Select adb.exe first."; return; }
        try
        {
            AndroidStatusText.Text = "Connecting...";
            AndroidStatusText.Text = await AdbClient.RunAsync(adb, null, "connect", address);
            _settings.AndroidAddress = address;
            SaveSettings();
            await RefreshDevicesAsync();
        }
        catch (Exception ex) { AndroidStatusText.Text = "Connection failed: " + ex.Message; }
    }

    private async void PairDevice_Click(object sender, RoutedEventArgs e)
    {
        var address = PairAddressTextBox.Text.Trim();
        var code = PairCodeTextBox.Text.Trim();
        if (!ValidAddress(address) || code.Length != 6 || !code.All(char.IsAsciiDigit))
        { AndroidStatusText.Text = "Enter the pairing IP:port and 6-digit code shown on Android."; return; }
        var adb = AdbClient.Find(_settings.AdbPath);
        if (adb is null) { AndroidStatusText.Text = "Select adb.exe first."; return; }
        try
        {
            AndroidStatusText.Text = "Pairing...";
            AndroidStatusText.Text = await AdbClient.RunAsync(adb, null, "pair", address, code);
            PairCodeTextBox.Clear();
            await RefreshDevicesAsync();
        }
        catch (Exception ex) { AndroidStatusText.Text = "Pairing failed: " + ex.Message; }
    }

    private async void BrowseAdb_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog { Filter = "ADB executable (adb.exe)|adb.exe",
            Title = "Select adb.exe from Android SDK Platform Tools" };
        if (picker.ShowDialog(this) != true) return;
        _settings.AdbPath = picker.FileName;
        SaveSettings();
        await RefreshDevicesAsync();
    }

    private void SaveSettings()
    {
        try { _settings.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = "Could not save settings: " + ex.Message;
        }
    }

    private void UpdateStatus()
    {
        if (_androidSource is { } android)
        {
            var text = !_clientConnected ? "Android selected - waiting for client" :
                _activeVideo is VideoPreference.H264 or VideoPreference.H265
                    ? $"Streaming Android ({(_activeVideo == VideoPreference.H265 ? "H.265" : "H.264")} / WebRTC)"
                    : "Android selected - connecting video";
            StatusText.Text = _videoError is null ? text : text + " — " + _videoError;
            return;
        }
        var status = _session.Selected is null
            ? _clientConnected ? "Client connected - choose a source" : "Waiting for client"
            : !_clientConnected ? "Window selected - waiting for client"
            : _activeVideo is VideoPreference.H264 or VideoPreference.H265
                ? $"Streaming window ({(_activeVideo == VideoPreference.H265 ? "H.265" : "H.264")} / WebRTC)"
                : "Streaming window (JPEG / WebSocket)";
        StatusText.Text = _videoError is null ? status : status + " — " + _videoError;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized) HideToTray();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested) return;
        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        if (_trayTipShown) return;
        _trayTipShown = true;
        _trayIcon.ShowBalloonTip(3000, "App Remote is still running",
            "Open or exit from the tray icon.", Forms.ToolTipIcon.Info);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    private static bool IsRemoteDesktopSession()
        => (uint)Process.GetCurrentProcess().SessionId != WTSGetActiveConsoleSessionId();

    private void SwitchToConsole()
    {
        if (!IsRemoteDesktopSession()) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "tscon.exe"),
                Arguments = $"{Process.GetCurrentProcess().SessionId} /dest:console",
                UseShellExecute = true,
                Verb = "runas"
            });
        }
        catch (Win32Exception ex)
        {
            if (ex.NativeErrorCode == 1223) return; // The user canceled UAC.
            RestoreFromTray();
            StatusText.Text = "Could not switch to console: " + ex.Message;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _closing = true;
        _trayIcon.Visible = false;
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        _trayImage?.Dispose();
        _session.Dispose();
        await _server.DisposeAsync();
        if (_server.CurrentAndroidSource is { } android) await android.DisposeAsync();
    }
}
