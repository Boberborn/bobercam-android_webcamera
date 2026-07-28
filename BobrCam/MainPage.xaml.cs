namespace BobrCam;

public partial class MainPage : ContentPage
{
    private readonly VideoReceiver _receiver = new();
#if WINDOWS
    private readonly AdbReverseManager _adbReverseManager = new();
#endif
    private bool _receiverStarted;
    private bool _updatingStreamSelectors;
#if WINDOWS
    private bool _previewMirrored;
    private int _previewRotation;
#endif
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
        PreviewRow.Height = new GridLength(270);
        WindowsFpsPicker.ItemsSource = new[] { "30 FPS", "60 FPS" };
        WindowsResolutionPicker.ItemsSource = new[] { "720p", "1080p", "2K", "4K" };
        var requestedFps = Preferences.Default.Get("requested_fps", 60);
        var requestedHeight = Preferences.Default.Get("requested_height", 1080);
        _updatingStreamSelectors = true;
        WindowsFpsPicker.SelectedIndex = requestedFps == 30 ? 0 : 1;
        WindowsResolutionPicker.SelectedIndex = requestedHeight switch
        {
            720 => 0,
            1440 => 2,
            2160 => 3,
            _ => 1
        };
        _updatingStreamSelectors = false;
        _receiver.RequestedFrameRate = (byte)(requestedFps == 30 ? 30 : 60);
        SetRequestedResolution(requestedHeight);
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
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _updatingStreamSelectors = true;
                var actualFps = (int)Math.Round(
                    (double)configuration.FrameRateNumerator /
                    configuration.FrameRateDenominator);
                WindowsFpsPicker.SelectedIndex = actualFps >= 60 ? 1 : 0;
                WindowsResolutionPicker.SelectedIndex = configuration.Height switch
                {
                    <= 720 => 0,
                    >= 2160 => 3,
                    >= 1440 => 2,
                    _ => 1
                };
                _receiver.RequestedFrameRate = (byte)(actualFps >= 60 ? 60 : 30);
                SetRequestedResolution(configuration.Height);
                Preferences.Default.Set("requested_fps", (int)_receiver.RequestedFrameRate);
                Preferences.Default.Set("requested_height", (int)_receiver.RequestedHeight);
                _updatingStreamSelectors = false;
            });
#endif
        };
        _receiver.AccessUnitReceived += accessUnit =>
        {
#if WINDOWS
            H264PreviewRenderer.Submit(accessUnit);
#endif
        };
        _receiver.StatusChanged += status => MainThread.BeginInvokeOnMainThread(() =>
        {
            WindowsStatusLabel.Text = status;
#if WINDOWS
            UpdatePairedPhonesLabel();
#endif
        });
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
                Window.Width = 720;
                Window.Height = 640;
            }
#endif
        };
        WindowsIpEntry.Text = NetworkAddress.GetLocalIPv4Address();
        WindowsPortEntry.Text = VideoProtocol.Port.ToString();
        AndroidPortEntry.Text = VideoProtocol.Port.ToString();
#if WINDOWS
        UpdatePairedPhonesLabel();
#endif
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
            if (!_androidAutoConnectStarted)
            {
                _androidAutoConnectStarted = true;
                if (TryGetPort(AndroidPortEntry, out var port))
                {
                    await ConnectCameraAsync(
                        "127.0.0.1",
                        port,
                        string.Empty,
                        "USB (ADB)");
                }
            }
        }
#endif
    }

    private async void OnUsbConnectClicked(object sender, EventArgs e)
    {
#if ANDROID
        if (!TryGetPort(AndroidPortEntry, out var port)) return;
        AndroidStatusLabel.Text = "Connecting by USB debugging…";
        await ConnectCameraAsync("127.0.0.1", port, string.Empty, "USB (ADB)");
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

    private void OnAllowNewPhoneClicked(object sender, EventArgs e)
    {
#if WINDOWS
        _receiver.AllowNewPhone(TimeSpan.FromSeconds(60));
#endif
    }

    private async void OnForgetPhonesClicked(object sender, EventArgs e)
    {
#if WINDOWS
        var confirmed = await DisplayAlert(
            "Forget paired phones?",
            "Every phone must be paired again. A phone connected by USB can pair automatically.",
            "Forget",
            "Cancel");
        if (!confirmed)
            return;
        _receiver.ForgetPairedPhones();
        UpdatePairedPhonesLabel();
#endif
    }

    private void OnFlipClicked(object sender, EventArgs e)
    {
#if WINDOWS
        _previewMirrored = !_previewMirrored;
        H264PreviewRenderer.SetPreviewTransform(
            _previewMirrored,
            _previewRotation);
#endif
    }

    private void OnRotateClicked(object sender, EventArgs e)
    {
#if WINDOWS
        _previewRotation = (_previewRotation + 90) % 360;
        H264PreviewRenderer.SetPreviewTransform(
            _previewMirrored,
            _previewRotation);
#endif
    }

    private async void OnSwitchCameraClicked(object sender, EventArgs e)
    {
#if WINDOWS
        _receiver.TogglePhoneCamera();
        await Task.CompletedTask;
#endif
    }

    private async void OnFlashClicked(object sender, EventArgs e)
    {
#if WINDOWS
        await SendCameraControlAsync(
            CameraControlCommand.ToggleFlash,
            "Toggling phone flash…");
#endif
    }

#if WINDOWS
    private async Task SendCameraControlAsync(
        CameraControlCommand command,
        string status)
    {
        try
        {
            WindowsStatusLabel.Text = status;
            await _receiver.SendCameraControlAsync(command);
        }
        catch (Exception ex)
        {
            WindowsStatusLabel.Text = ex.GetBaseException().Message;
        }
    }
#endif

    private async void OnWindowsFpsChanged(object sender, EventArgs e)
    {
#if WINDOWS
        if (_updatingStreamSelectors) return;
        var requestedFps = WindowsFpsPicker.SelectedIndex == 0 ? 30 : 60;
        _receiver.RequestedFrameRate = (byte)requestedFps;
        _receiver.PrioritizeResolution = false;
        Preferences.Default.Set("requested_fps", requestedFps);
        await RestartReceiverForModeChangeAsync($"{requestedFps} FPS");
#endif
    }

    private async void OnWindowsResolutionChanged(object sender, EventArgs e)
    {
#if WINDOWS
        if (_updatingStreamSelectors) return;
        var requestedHeight = WindowsResolutionPicker.SelectedIndex switch
        {
            0 => 720,
            2 => 1440,
            3 => 2160,
            _ => 1080
        };
        SetRequestedResolution(requestedHeight);
        _receiver.PrioritizeResolution = true;
        Preferences.Default.Set("requested_height", requestedHeight);
        await RestartReceiverForModeChangeAsync(
            requestedHeight switch
            {
                1440 => "2K",
                2160 => "4K",
                _ => $"{requestedHeight}p"
            });
#endif
    }

#if WINDOWS
    private void SetRequestedResolution(int height)
    {
        (_receiver.RequestedWidth, _receiver.RequestedHeight) = height switch
        {
            720 => ((ushort)1280, (ushort)720),
            1440 => ((ushort)2560, (ushort)1440),
            2160 => ((ushort)3840, (ushort)2160),
            _ => ((ushort)1920, (ushort)1080)
        };
    }

    private async Task RestartReceiverForModeChangeAsync(string mode)
    {
        if (!_receiverStarted) return;
        WindowsStatusLabel.Text = $"Switching to {mode}…";
        await _receiver.StopAsync();
        _receiverStarted = false;
        await StartReceiverAsync();
    }
#endif

#if WINDOWS
    private async Task StartReceiverAsync()
    {
        if (!TryGetPort(WindowsPortEntry, out var port)) return;
        var bindAddress = WindowsIpEntry.Text?.Trim() ?? string.Empty;
        try
        {
            await _receiver.StartAsync(bindAddress, port);
            _adbReverseManager.Start(port, VideoProtocol.UsbHostPort);
            _receiverStarted = true;
            ReceiverAddressLabel.Text =
                $"Wi-Fi TLS: {bindAddress}:{port} · USB ADB: 127.0.0.1:{VideoProtocol.UsbHostPort}";
            UpdatePairedPhonesLabel();
        }
        catch (Exception ex)
        {
            WindowsStatusLabel.Text = $"Receiver error: {ex.Message}";
        }
    }

    private void UpdatePairedPhonesLabel() =>
        PairedPhonesLabel.Text =
            $"{_receiver.PairedPhoneCount} paired phone(s)";
#endif

    protected override async void OnDisappearing()
    {
        base.OnDisappearing();
#if WINDOWS
        await _receiver.StopAsync();
        await _adbReverseManager.DisposeAsync();
        _receiverStarted = false;
#endif
    }
}
