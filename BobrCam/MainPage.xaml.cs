namespace BobrCam;

public partial class MainPage : ContentPage
{
    private readonly VideoReceiver _receiver = new();
    private bool _receiverStarted;

    public MainPage()
    {
        InitializeComponent();
#if WINDOWS
        AndroidPanel.IsVisible = false;
        WindowsPanel.IsVisible = true;
#else
        AndroidPanel.IsVisible = true;
        WindowsPanel.IsVisible = false;
#endif
        ReceiverAddressLabel.Text = $"This PC: {NetworkAddress.GetLocalIPv4Address()}";
        _receiver.FrameReceived += jpeg =>
        {
            FrameBridge.Publish(jpeg);
            MainThread.BeginInvokeOnMainThread(() =>
                PreviewImage.Source = ImageSource.FromStream(() => new MemoryStream(jpeg)));
        };
        _receiver.StatusChanged += status => MainThread.BeginInvokeOnMainThread(() => WindowsStatusLabel.Text = status);
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
        if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
        {
            AndroidStatusLabel.Text = "Camera permission is required.";
            UsbButton.IsEnabled = WifiButton.IsEnabled = true;
            return;
        }
        try
        {
            AndroidStatusLabel.Text = $"Connecting securely by {mode}…";
            await AndroidCameraStreamer.StartAsync(host, port, fingerprint);
            AndroidStatusLabel.Text = $"Connected by {mode} — encrypted stream active.";
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
