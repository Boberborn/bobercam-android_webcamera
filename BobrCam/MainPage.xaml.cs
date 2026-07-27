namespace BobrCam;

public partial class MainPage : ContentPage
{
    private readonly VideoReceiver _receiver = new();
    private bool _receiverStarted;
    private bool _fitWindowOnce = true;
#if ANDROID
    private bool _androidAutoConnectStarted;
#endif

    public MainPage()
    {
        InitializeComponent();
#if WINDOWS
        AndroidPanel.IsVisible = false;
        WindowsPanel.IsVisible = true;
        WindowsFpsLabel.IsVisible = true;
        H264PreviewRenderer.StatusChanged += status =>
            MainThread.BeginInvokeOnMainThread(() => WindowsStatusLabel.Text = status);
        H264PreviewRenderer.FpsChanged += fps =>
            MainThread.BeginInvokeOnMainThread(() =>
                WindowsFpsLabel.Text = $"{fps:0.0} FPS");
#else
        AndroidPanel.IsVisible = true;
        WindowsPanel.IsVisible = false;
        WindowsFpsLabel.IsVisible = false;
        AndroidCameraStreamer.ConnectionStatusChanged += status =>
            MainThread.BeginInvokeOnMainThread(() =>
            {
                AndroidStatusLabel.Text = status;
                UsbButton.IsEnabled = WifiButton.IsEnabled = true;
            });
#endif
        ReceiverAddressLabel.Text = $"This PC: {NetworkAddress.GetLocalIPv4Address()}";
        _receiver.StreamConfigured += (configuration, codecData) =>
        {
#if WINDOWS
            H264PreviewRenderer.Configure(configuration, codecData);
            if (_fitWindowOnce)
            {
                _fitWindowOnce = false;
                var w = configuration.Width;
                var h = configuration.Height;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (Window is not null)
                    {
                        Window.Width = Math.Min(w + 96, 1600);
                        Window.Height = Math.Min(h + 200, 1000);
                    }
                });
            }
#endif
        };
        _receiver.AccessUnitReceived += accessUnit =>
        {
#if WINDOWS
            H264PreviewRenderer.Submit(accessUnit);
#endif
        };
        _receiver.StatusChanged += status => MainThread.BeginInvokeOnMainThread(() => WindowsStatusLabel.Text = status);
#if ANDROID
        PreviewImage.IsVisible = false;
        CameraPreview.SurfaceReady += async () =>
        {
            if (await Permissions.RequestAsync<Permissions.Camera>() == PermissionStatus.Granted)
                await AndroidCameraStreamer.StartLocalPreviewAsync();
        };
        SizeChanged += (_, _) =>
        {
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (CameraPreview.Handler?.PlatformView is AspectRatioTextureView tv)
                {
                    var ctx = tv.Context;
                    if (ctx is Android.Content.Context c)
                    {
                        var wm = c.GetSystemService(Android.Content.Context.WindowService)
                            as Android.Views.IWindowManager;
                        if (wm is not null)
                        {
                            var rot = wm.DefaultDisplay?.Rotation ?? Android.Views.SurfaceOrientation.Rotation0;
                            int degrees = rot switch
                            {
                                Android.Views.SurfaceOrientation.Rotation90 => 90,
                                Android.Views.SurfaceOrientation.Rotation180 => 180,
                                Android.Views.SurfaceOrientation.Rotation270 => 270,
                                _ => 0
                            };
                            AndroidCameraStreamer.ApplyDisplayOrientation(degrees);
                            AndroidCameraStreamer.GetRotatedAspect(out var w, out var h);
                            tv.SetTargetAspect(w, h);
                        }
                    }
                }
            });
        };
#endif
        Loaded += (_, _) =>
        {
#if WINDOWS
            if (Window is not null)
            {
                Window.Width = 800;
                Window.Height = 450;
            }
#endif
        };
        WindowsIpEntry.Text = NetworkAddress.GetLocalIPv4Address();
        WindowsPortEntry.Text = VideoProtocol.Port.ToString();
        AndroidPortEntry.Text = VideoProtocol.Port.ToString();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
#if WINDOWS
        if (!_receiverStarted)
        {
            await StartReceiverAsync();
        }
#elif ANDROID
        if (await Permissions.RequestAsync<Permissions.Camera>() == PermissionStatus.Granted)
        {
            await AndroidCameraStreamer.StartLocalPreviewAsync();
            if (!_androidAutoConnectStarted && TryGetPort(AndroidPortEntry, out var port))
            {
                _androidAutoConnectStarted = true;
                await ConnectCameraAsync("127.0.0.1", port, string.Empty, "USB");
            }
        }
#endif
    }

    private async void OnUsbConnectClicked(object sender, EventArgs e)
    {
#if ANDROID
        if (!TryGetPort(AndroidPortEntry, out var port)) return;
        await ConnectCameraAsync("127.0.0.1", port, string.Empty, "USB");
#endif
    }

    private async void OnWifiConnectClicked(object sender, EventArgs e)
    {
#if ANDROID
        if (!TryGetPort(AndroidPortEntry, out var port)) return;
        var host = AndroidIpEntry.Text?.Trim() ?? string.Empty;
        string fingerprint = string.Empty;
        if (string.IsNullOrEmpty(host))
        {
            AndroidStatusLabel.Text = "Finding Windows receiver on Wi-Fi…";
            try
            {
                var receiver = await ReceiverDiscovery.FindAsync(TimeSpan.FromSeconds(8));
                host = receiver.Host;
                port = receiver.Port;
                fingerprint = receiver.CertificateFingerprint;
                AndroidIpEntry.Text = host;
                AndroidPortEntry.Text = port.ToString();
            }
            catch (Exception ex)
            {
                AndroidStatusLabel.Text = ex.GetBaseException().Message;
                return;
            }
        }
        await ConnectCameraAsync(host, port, fingerprint, "Wi-Fi");
#endif
    }

#if ANDROID
    private async Task ConnectCameraAsync(string host, int port, string fingerprint, string mode)
    {
        UsbButton.IsEnabled = WifiButton.IsEnabled = false;
        var granted = await Permissions.RequestAsync<Permissions.Camera>();
        if (granted != PermissionStatus.Granted)
        {
            var current = Permissions.CheckStatusAsync<Permissions.Camera>().GetAwaiter().GetResult();
            if (current == PermissionStatus.Denied)
            {
                AndroidStatusLabel.Text = "Camera permission was denied. Open Settings to allow it.";
                if (await DisplayAlert("Camera permission required",
                        "BobrCam needs the camera to send video to Windows. Open Settings to grant it?",
                        "Open Settings", "Cancel"))
                {
                    AppInfo.Current.ShowSettingsUI();
                }
            }
            else
            {
                AndroidStatusLabel.Text = "Camera permission is required.";
            }
            UsbButton.IsEnabled = WifiButton.IsEnabled = true;
            return;
        }
        try
        {
            AndroidStatusLabel.Text = $"Connecting securely by {mode}…";
            await AndroidCameraStreamer.StartAsync(host, port, fingerprint);
            AndroidStatusLabel.Text = $"{mode} auto-connect active.";
            UsbButton.IsEnabled = WifiButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AndroidStatusLabel.Text = ex.GetBaseException().Message;
            UsbButton.IsEnabled = WifiButton.IsEnabled = true;
            try { await AndroidCameraStreamer.StartLocalPreviewAsync(); }
            catch { }
        }
    }
#endif

    private bool TryGetPort(Entry entry, out int port)
    {
        if (int.TryParse(entry.Text, out port) && port is > 0 and <= 65535) return true;
        port = 0;
        if (DeviceInfo.Current.Platform == DevicePlatform.Android)
            AndroidStatusLabel.Text = "Enter a valid port (1–65535).";
        else
            WindowsStatusLabel.Text = "Enter a valid port (1–65535).";
        return false;
    }

    private async void OnApplyReceiverClicked(object sender, EventArgs e)
    {
#if WINDOWS
        await _receiver.StopAsync();
        _receiverStarted = false;
        await StartReceiverAsync();
#endif
    }

#if WINDOWS
    private async Task StartReceiverAsync()
    {
        if (!TryGetPort(WindowsPortEntry, out var port)) return;
        var bindAddress = WindowsIpEntry.Text?.Trim() ?? string.Empty;
        try
        {
            await _receiver.StartAsync(bindAddress, port);
            _receiverStarted = true;
            ReceiverAddressLabel.Text = $"Listening on {bindAddress}:{port}";
        }
        catch (Exception ex)
        {
            WindowsStatusLabel.Text = $"Receiver error: {ex.Message}";
        }
    }
#endif

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
#if WINDOWS
        await _receiver.StopAsync();
        _receiverStarted = false;
#endif
    }
}
