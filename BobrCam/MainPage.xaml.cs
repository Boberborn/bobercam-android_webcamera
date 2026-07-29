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
    private int _previewAspectWidth = 16;
    private int _previewAspectHeight = 9;
    private bool _updatingCameraControls;
    private bool _updatingEffectControls;
    private bool _effectControlsSupported;
    private bool _faceTrackingSupported;
    private ushort? _resolutionBeforeLegacyEffect;
    private byte? _frameRateBeforeLegacyEffect;
    private VideoEffectMode _selectedEffectMode;
    private CameraWhiteBalanceMode[] _availableWhiteBalanceModes =
        [CameraWhiteBalanceMode.Auto];
    private readonly Dictionary<CameraControlCommand, CancellationTokenSource>
        _cameraControlDebounce = [];
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
        PreviewRow.Height = GridLength.Auto;
        PreviewBorder.WidthRequest = 640;
        PreviewBorder.MaximumWidthRequest = 720;
        PreviewBorder.HorizontalOptions = LayoutOptions.Center;
        PreviewBorder.VerticalOptions = LayoutOptions.Start;
        PreviewBorder.SizeChanged += (_, _) => UpdateWindowsPreviewSize();
        WindowsFpsPicker.ItemsSource = new[] { "30 FPS", "60 FPS" };
        WindowsResolutionPicker.ItemsSource = new[] { "720p", "1080p", "2K", "4K" };
        WhiteBalancePicker.ItemsSource = new[] { "Auto" };
        WhiteBalancePicker.SelectedIndex = 0;
        _updatingEffectControls = true;
        _selectedEffectMode = Enum.IsDefined(
            (VideoEffectMode)Preferences.Default.Get("effect_mode", 0))
            ? (VideoEffectMode)Preferences.Default.Get("effect_mode", 0)
            : VideoEffectMode.Off;
        BeautySmoothSlider.Value = Math.Clamp(
            Preferences.Default.Get("beauty_smoothness", 35),
            0,
            100);
        BeautyBrightnessSlider.Value = Math.Clamp(
            Preferences.Default.Get("beauty_brightness", 0),
            -50,
            50);
        BeautyWarmthSlider.Value = Math.Clamp(
            Preferences.Default.Get("beauty_warmth", 0),
            -50,
            50);
        BeautyVignetteSlider.Value = Math.Clamp(
            Preferences.Default.Get("beauty_vignette", 0),
            0,
            100);
        MaskStrengthSlider.Value = Math.Clamp(
            Preferences.Default.Get("mask_strength", 90),
            0,
            100);
        _updatingEffectControls = false;
        ApplyEffectModeUi();
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
        _receiver.CameraCapabilitiesReceived += capabilities =>
            MainThread.BeginInvokeOnMainThread(() =>
                ApplyCameraCapabilities(capabilities));
#else
        AndroidPanel.IsVisible = true;
        WindowsPanel.IsVisible = false;
        WindowsFpsLabel.IsVisible = false;
        PreviewBorder.IsVisible = false;
        PreviewRow.Height = new GridLength(0);
        CameraPreview.IsVisible = false;
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
            SharedH264StreamWriter.Configure(configuration, codecData);
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
                _previewAspectWidth = configuration.Width;
                _previewAspectHeight = configuration.Height;
                UpdateWindowsPreviewSize();
                H264PreviewRenderer.SetPreviewTransform(
                    _previewMirrored,
                    _previewRotation);
                SharedH264StreamWriter.SetRotation(_previewRotation);
                _receiver.RequestedFrameRate = (byte)(actualFps >= 60 ? 60 : 30);
                SetRequestedResolution(configuration.Height);
                if (_resolutionBeforeLegacyEffect is null)
                {
                    Preferences.Default.Set(
                        "requested_fps",
                        (int)_receiver.RequestedFrameRate);
                    Preferences.Default.Set(
                        "requested_height",
                        (int)_receiver.RequestedHeight);
                }
                _updatingStreamSelectors = false;
            });
#endif
        };
        _receiver.AccessUnitReceived += accessUnit =>
        {
#if WINDOWS
            SharedH264StreamWriter.Publish(accessUnit);
            H264PreviewRenderer.Submit(accessUnit);
#endif
        };
        _receiver.StatusChanged += status => MainThread.BeginInvokeOnMainThread(() =>
        {
            WindowsStatusLabel.Text = status;
        });
#if ANDROID
        PreviewImage.IsVisible = false;
#endif
        Loaded += (_, _) =>
        {
#if WINDOWS
            if (Window is not null)
            {
                Window.Width = 720;
                Window.Height = 850;
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
        SharedH264StreamWriter.SetRotation(_previewRotation);
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
    private void ApplyCameraCapabilities(CameraCapabilities capabilities)
    {
        _updatingCameraControls = true;
        try
        {
            var exposureAvailable = capabilities.Flags.HasFlag(
                CameraCapabilityFlags.ExposureCompensation);
            ExposureSlider.IsEnabled = exposureAvailable;
            ExposureSlider.Minimum = capabilities.MinimumExposureCompensation;
            ExposureSlider.Maximum = capabilities.MaximumExposureCompensation;
            ExposureSlider.Value = capabilities.CurrentExposureCompensation;
            ExposureValueLabel.Text =
                $"Exposure {capabilities.CurrentExposureCompensation:+0;-0;0}";

            var zoomAvailable = capabilities.Flags.HasFlag(
                CameraCapabilityFlags.Zoom);
            ZoomSlider.IsEnabled = zoomAvailable;
            ZoomSlider.Minimum = 1;
            ZoomSlider.Maximum = Math.Max(
                1,
                capabilities.MaximumZoomHundredths / 100d);
            ZoomSlider.Value = Math.Clamp(
                capabilities.CurrentZoomHundredths / 100d,
                ZoomSlider.Minimum,
                ZoomSlider.Maximum);
            ZoomValueLabel.Text = $"Zoom {ZoomSlider.Value:0.0}×";

            var modes = new List<(string Label, CameraWhiteBalanceMode Mode)>();
            AddWhiteBalanceMode(
                CameraWhiteBalanceModes.Auto,
                "Auto",
                CameraWhiteBalanceMode.Auto);
            AddWhiteBalanceMode(
                CameraWhiteBalanceModes.Daylight,
                "Daylight",
                CameraWhiteBalanceMode.Daylight);
            AddWhiteBalanceMode(
                CameraWhiteBalanceModes.CloudyDaylight,
                "Cloudy",
                CameraWhiteBalanceMode.CloudyDaylight);
            AddWhiteBalanceMode(
                CameraWhiteBalanceModes.Fluorescent,
                "Fluorescent",
                CameraWhiteBalanceMode.Fluorescent);
            AddWhiteBalanceMode(
                CameraWhiteBalanceModes.Incandescent,
                "Incandescent",
                CameraWhiteBalanceMode.Incandescent);
            if (modes.Count == 0)
                modes.Add(("Auto", CameraWhiteBalanceMode.Auto));

            _availableWhiteBalanceModes =
                modes.Select(item => item.Mode).ToArray();
            WhiteBalancePicker.ItemsSource =
                modes.Select(item => item.Label).ToArray();
            WhiteBalancePicker.SelectedIndex = Math.Max(
                0,
                Array.IndexOf(
                    _availableWhiteBalanceModes,
                    capabilities.CurrentWhiteBalanceMode));
            WhiteBalancePicker.IsEnabled =
                capabilities.Flags.HasFlag(CameraCapabilityFlags.WhiteBalance);
            FlashButton.IsEnabled =
                capabilities.Flags.HasFlag(CameraCapabilityFlags.Flash);
            CameraSettingsGrid.IsVisible =
                exposureAvailable ||
                zoomAvailable ||
                WhiteBalancePicker.IsEnabled;
            _effectControlsSupported = capabilities.Flags.HasFlag(
                CameraCapabilityFlags.PhoneGpuEffects);
            _faceTrackingSupported = capabilities.Flags.HasFlag(
                CameraCapabilityFlags.FaceTracking);
            EffectsCard.IsEnabled = _effectControlsSupported;
            MaskModeButton.IsEnabled =
                _effectControlsSupported && _faceTrackingSupported;
            MaskTrackingLabel.Text = _faceTrackingSupported
                ? "Face-tracked Bobr"
                : "Requires phone face tracking";
            if (!_faceTrackingSupported &&
                _selectedEffectMode == VideoEffectMode.Mask)
            {
                _selectedEffectMode = VideoEffectMode.Off;
                Preferences.Default.Set(
                    "effect_mode",
                    (int)VideoEffectMode.Off);
            }
            ApplyEffectModeUi();
            _ = !_faceTrackingSupported &&
                _selectedEffectMode == VideoEffectMode.Beauty &&
                _receiver.RequestedHeight > 720
                ? SelectEffectModeAsync(VideoEffectMode.Beauty)
                : RestorePhoneEffectSettingsAsync();

            void AddWhiteBalanceMode(
                CameraWhiteBalanceModes flag,
                string label,
                CameraWhiteBalanceMode mode)
            {
                if (capabilities.WhiteBalanceModes.HasFlag(flag))
                    modes.Add((label, mode));
            }
        }
        finally
        {
            _updatingCameraControls = false;
        }
    }

    private void OnExposureChanged(object sender, ValueChangedEventArgs e)
    {
        var value = (int)Math.Round(e.NewValue);
        ExposureValueLabel.Text = $"Exposure {value:+0;-0;0}";
        if (!_updatingCameraControls)
            QueueCameraControl(
                CameraControlCommand.SetExposureCompensation,
                value);
    }

    private void OnZoomChanged(object sender, ValueChangedEventArgs e)
    {
        ZoomValueLabel.Text = $"Zoom {e.NewValue:0.0}×";
        if (!_updatingCameraControls)
            QueueCameraControl(
                CameraControlCommand.SetZoom,
                (int)Math.Round(e.NewValue * 100));
    }

    private async void OnWhiteBalanceChanged(object sender, EventArgs e)
    {
        if (_updatingCameraControls ||
            WhiteBalancePicker.SelectedIndex < 0 ||
            WhiteBalancePicker.SelectedIndex >= _availableWhiteBalanceModes.Length)
        {
            return;
        }

        await SendCameraControlAsync(
            CameraControlCommand.SetWhiteBalance,
            "Applying phone white balance…",
            (int)_availableWhiteBalanceModes[WhiteBalancePicker.SelectedIndex]);
    }

    private async void OnEffectOffClicked(object sender, EventArgs e) =>
        await SelectEffectModeAsync(VideoEffectMode.Off);

    private async void OnBeautyModeClicked(object sender, EventArgs e) =>
        await SelectEffectModeAsync(VideoEffectMode.Beauty);

    private async void OnMaskModeClicked(object sender, EventArgs e)
    {
        await SelectEffectModeAsync(VideoEffectMode.Mask);
    }

    private async Task SelectEffectModeAsync(VideoEffectMode mode)
    {
        if (!_effectControlsSupported)
        {
            WindowsStatusLabel.Text =
                "Connect a phone that supports GPU effects first.";
            return;
        }

        if (mode == VideoEffectMode.Mask && !_faceTrackingSupported)
        {
            WindowsStatusLabel.Text =
                "Bobr Mask requires a phone with face tracking.";
            return;
        }

        _selectedEffectMode = mode;
        Preferences.Default.Set("effect_mode", (int)mode);
        ApplyEffectModeUi();

        if (!_faceTrackingSupported &&
            mode == VideoEffectMode.Beauty &&
            _receiver.RequestedHeight > 720)
        {
            _resolutionBeforeLegacyEffect ??= _receiver.RequestedHeight;
            _frameRateBeforeLegacyEffect ??= _receiver.RequestedFrameRate;
            SetRequestedResolution(720);
            _receiver.RequestedFrameRate = 30;
            _receiver.PrioritizeResolution = true;
            _updatingStreamSelectors = true;
            WindowsResolutionPicker.SelectedIndex = 0;
            WindowsFpsPicker.SelectedIndex = 0;
            _updatingStreamSelectors = false;
            await RestartReceiverForModeChangeAsync(
                "720p30 Beauty for this phone");
            return;
        }

        if (mode == VideoEffectMode.Off &&
            _resolutionBeforeLegacyEffect is ushort previousHeight &&
            _frameRateBeforeLegacyEffect is byte previousFrameRate)
        {
            SetRequestedResolution(previousHeight);
            _receiver.RequestedFrameRate = previousFrameRate;
            _receiver.PrioritizeResolution = true;
            _updatingStreamSelectors = true;
            WindowsResolutionPicker.SelectedIndex = previousHeight switch
            {
                <= 720 => 0,
                >= 2160 => 3,
                >= 1440 => 2,
                _ => 1
            };
            WindowsFpsPicker.SelectedIndex = previousFrameRate >= 60 ? 1 : 0;
            _updatingStreamSelectors = false;
            _resolutionBeforeLegacyEffect = null;
            _frameRateBeforeLegacyEffect = null;
            await RestartReceiverForModeChangeAsync(
                $"{previousHeight}p direct H.264");
            return;
        }

        await SendCameraControlAsync(
            CameraControlCommand.SetEffectMode,
            mode switch
            {
                VideoEffectMode.Beauty => "Starting phone Beauty mode…",
                VideoEffectMode.Mask when _faceTrackingSupported =>
                    "Starting face-tracked Bobr mask…",
                _ => "Returning to direct H.264 mode…"
            },
            (int)mode);
    }

    private void ApplyEffectModeUi()
    {
        BeautySettingsGrid.IsVisible =
            _selectedEffectMode == VideoEffectMode.Beauty;
        MaskSettingsGrid.IsVisible =
            _selectedEffectMode == VideoEffectMode.Mask;
        SetModeButtonState(
            EffectOffButton,
            _selectedEffectMode == VideoEffectMode.Off);
        SetModeButtonState(
            BeautyModeButton,
            _selectedEffectMode == VideoEffectMode.Beauty);
        SetModeButtonState(
            MaskModeButton,
            _selectedEffectMode == VideoEffectMode.Mask);

        static void SetModeButtonState(Button button, bool selected)
        {
            button.BackgroundColor = Color.FromArgb(
                selected ? "#F45145" : "#59413B");
            button.TextColor = Colors.White;
        }
    }

    private async Task RestorePhoneEffectSettingsAsync()
    {
        if (!_effectControlsSupported)
            return;
        try
        {
            await _receiver.SendCameraControlAsync(
                CameraControlCommand.SetBeautySmoothness,
                (int)Math.Round(BeautySmoothSlider.Value));
            await _receiver.SendCameraControlAsync(
                CameraControlCommand.SetBeautyBrightness,
                (int)Math.Round(BeautyBrightnessSlider.Value));
            await _receiver.SendCameraControlAsync(
                CameraControlCommand.SetBeautyWarmth,
                (int)Math.Round(BeautyWarmthSlider.Value));
            await _receiver.SendCameraControlAsync(
                CameraControlCommand.SetBeautyVignette,
                (int)Math.Round(BeautyVignetteSlider.Value));
            await _receiver.SendCameraControlAsync(
                CameraControlCommand.SetMaskStrength,
                (int)Math.Round(MaskStrengthSlider.Value));
            await _receiver.SendCameraControlAsync(
                CameraControlCommand.SetEffectMode,
                (int)_selectedEffectMode);
        }
        catch (Exception ex)
        {
            WindowsStatusLabel.Text = ex.GetBaseException().Message;
        }
    }

    private void OnBeautySmoothChanged(object sender, ValueChangedEventArgs e)
    {
        var value = (int)Math.Round(e.NewValue);
        BeautySmoothLabel.Text = $"Smooth {value}";
        if (_updatingEffectControls)
            return;
        Preferences.Default.Set("beauty_smoothness", value);
        QueueCameraControl(CameraControlCommand.SetBeautySmoothness, value);
    }

    private void OnBeautyBrightnessChanged(
        object sender,
        ValueChangedEventArgs e)
    {
        var value = (int)Math.Round(e.NewValue);
        BeautyBrightnessLabel.Text = $"Light {value:+0;-0;0}";
        if (_updatingEffectControls)
            return;
        Preferences.Default.Set("beauty_brightness", value);
        QueueCameraControl(CameraControlCommand.SetBeautyBrightness, value);
    }

    private void OnBeautyWarmthChanged(object sender, ValueChangedEventArgs e)
    {
        var value = (int)Math.Round(e.NewValue);
        BeautyWarmthLabel.Text = $"Warmth {value:+0;-0;0}";
        if (_updatingEffectControls)
            return;
        Preferences.Default.Set("beauty_warmth", value);
        QueueCameraControl(CameraControlCommand.SetBeautyWarmth, value);
    }

    private void OnBeautyVignetteChanged(
        object sender,
        ValueChangedEventArgs e)
    {
        var value = (int)Math.Round(e.NewValue);
        BeautyVignetteLabel.Text = $"Vignette {value}";
        if (_updatingEffectControls)
            return;
        Preferences.Default.Set("beauty_vignette", value);
        QueueCameraControl(CameraControlCommand.SetBeautyVignette, value);
    }

    private void OnMaskStrengthChanged(object sender, ValueChangedEventArgs e)
    {
        var value = (int)Math.Round(e.NewValue);
        MaskStrengthLabel.Text = $"{value}%";
        if (_updatingEffectControls)
            return;
        Preferences.Default.Set("mask_strength", value);
        QueueCameraControl(CameraControlCommand.SetMaskStrength, value);
    }

    private void QueueCameraControl(CameraControlCommand command, int value)
    {
        if (_cameraControlDebounce.TryGetValue(command, out var previous))
            previous.Cancel();
        var cancellation = new CancellationTokenSource();
        _cameraControlDebounce[command] = cancellation;
        _ = SendDebouncedCameraControlAsync(command, value, cancellation);
    }

    private async Task SendDebouncedCameraControlAsync(
        CameraControlCommand command,
        int value,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(120, cancellation.Token);
            await _receiver.SendCameraControlAsync(
                command,
                value,
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            WindowsStatusLabel.Text = ex.GetBaseException().Message;
        }
        finally
        {
            if (_cameraControlDebounce.TryGetValue(command, out var current) &&
                ReferenceEquals(current, cancellation))
            {
                _cameraControlDebounce.Remove(command);
            }
            cancellation.Dispose();
        }
    }

    private async Task SendCameraControlAsync(
        CameraControlCommand command,
        string status,
        int value = 0)
    {
        try
        {
            WindowsStatusLabel.Text = status;
            await _receiver.SendCameraControlAsync(command, value);
        }
        catch (Exception ex)
        {
            WindowsStatusLabel.Text = ex.GetBaseException().Message;
        }
    }
#else
    private void OnExposureChanged(object sender, ValueChangedEventArgs e)
    {
    }

    private void OnZoomChanged(object sender, ValueChangedEventArgs e)
    {
    }

    private void OnWhiteBalanceChanged(object sender, EventArgs e)
    {
    }

    private void OnEffectOffClicked(object sender, EventArgs e)
    {
    }

    private void OnBeautyModeClicked(object sender, EventArgs e)
    {
    }

    private void OnMaskModeClicked(object sender, EventArgs e)
    {
    }

    private void OnBeautySmoothChanged(object sender, ValueChangedEventArgs e)
    {
    }

    private void OnBeautyBrightnessChanged(
        object sender,
        ValueChangedEventArgs e)
    {
    }

    private void OnBeautyWarmthChanged(object sender, ValueChangedEventArgs e)
    {
    }

    private void OnBeautyVignetteChanged(
        object sender,
        ValueChangedEventArgs e)
    {
    }

    private void OnMaskStrengthChanged(object sender, ValueChangedEventArgs e)
    {
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

    private void UpdateWindowsPreviewSize()
    {
        if (PreviewBorder.Width <= 0 ||
            _previewAspectWidth <= 0 ||
            _previewAspectHeight <= 0)
        {
            return;
        }

        var desiredHeight =
            PreviewBorder.Width * _previewAspectHeight / _previewAspectWidth;
        if (Math.Abs(PreviewBorder.HeightRequest - desiredHeight) > 0.5)
            PreviewBorder.HeightRequest = desiredHeight;
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
        await _adbReverseManager.DisposeAsync();
        _receiverStarted = false;
#endif
    }
}
