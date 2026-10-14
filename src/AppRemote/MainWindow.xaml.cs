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
using QRCoder;
using Forms = System.Windows.Forms;

namespace AppRemote;

public partial class MainWindow : Window
{
    private readonly RemoteSession _session = new();
    private readonly AppSettings _settings = AppSettings.Load();
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
            SelectedText.Text = _session.Selected is { } selected ? $"Selected window: {selected.Title}" : "No window selected";
            StopButton.IsEnabled = _session.Selected is not null;
            _trayStopItem.Enabled = _session.Selected is not null;
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
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        try
        {
            await _server.StartAsync();
            RefreshConnectionOptions();
            UpdateStatus();
            TryRestoreLastWindow();
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

    private void StopSharing()
    {
        _session.Stop();
        _settings.Forget();
        SaveSettings();
        SelectedText.Text = "No window selected";
        StopButton.IsEnabled = false;
        _trayStopItem.Enabled = false;
        UpdateStatus();
    }
    private void RestoreLastWindow_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingSettings) return;
        _settings.RestoreLastWindow = RestoreLastWindowCheck.IsChecked == true;
        if (_settings.RestoreLastWindow && _session.Selected is { } selected)
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
        _server.SetVideoPreference(_settings.VideoMode);
        SaveSettings();
        UpdateStatus();
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
        next.SetVideoPreference(_settings.VideoMode);
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
        if (!_settings.RestoreLastWindow) return;
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
        var status = _session.Selected is null
            ? _clientConnected ? "Client connected - choose a window" : "Waiting for client"
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
    }
}
