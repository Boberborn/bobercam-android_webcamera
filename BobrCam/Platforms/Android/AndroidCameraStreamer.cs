#if ANDROID
using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Views;
using Java.Nio;

namespace BobrCam;

public static class AndroidCameraStreamer
{
    private const int FullHdWidth = 1920;
    private const int FullHdHeight = 1080;
    private const int HdWidth = 1280;
    private const int HdHeight = 720;
    private const int QuadHdWidth = 2560;
    private const int QuadHdHeight = 1440;
    private const int UltraHdWidth = 3840;
    private const int UltraHdHeight = 2160;
    private const int PreferredFps = 60;
    private const int FallbackFps = 30;
    private const int FullHdBitrate = 16_000_000;
    private const int HdBitrate = 8_000_000;
    private const int QuadHdBitrate = 24_000_000;
    private const int UltraHdBitrate = 35_000_000;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    private static readonly SemaphoreSlim StateGate = new(1, 1);
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    private static CameraDevice? _camera;
    private static CameraCaptureSession? _captureSession;
    private static SessionStateCallback? _captureSessionCallback;
    private static TaskCompletionSource? _captureSessionClosed;
    private static CaptureRequest.Builder? _repeatingRequestBuilder;
    private static HandlerThread? _cameraThread;
    private static Handler? _cameraHandler;
    private static MediaCodec? _encoder;
    private static Surface? _encoderSurface;
    private static Surface? _previewOutput;
    private static SurfaceTexture? _previewTexture;
    private static TcpClient? _client;
    private static System.IO.Stream? _outputStream;
    private static CancellationTokenSource? _streamCancellation;
    private static CancellationTokenSource? _reconnectCancellation;
    private static Task? _reconnectTask;
    private static Task? _drainTask;
    private static Task? _controlTask;
    private static bool _wantPreview;
    private static int _streamFps = FallbackFps;
    private static int _requestedFps = PreferredFps;
    private static int _requestedWidth = FullHdWidth;
    private static int _requestedHeight = FullHdHeight;
    private static bool _prioritizeResolution;
    private static int _streamWidth = FullHdWidth;
    private static int _streamHeight = FullHdHeight;
    private static int _streamBitrate = FullHdBitrate;
    private static Android.Util.Range? _targetFpsRange;
    private static Android.Util.Range? _activeFpsRange;
    private static bool _useHighSpeedSession;
    private static CameraCharacteristics? _cameraCharacteristics;
    private static bool _useFrontCamera;
    private static bool _flashEnabled;
    private static int _exposureCompensation;
    private static int _zoomHundredths = 100;
    private static CameraWhiteBalanceMode _whiteBalanceMode =
        CameraWhiteBalanceMode.Auto;
    private static uint _sequence;

    public static volatile string? LastError;
    public static int FramesSent;
    public static event Action<string>? ConnectionStatusChanged;

    public static void SetPreviewSurface(SurfaceTexture? surface, Action<int, int, int>? onReady)
    {
        _previewTexture = surface;
        if (surface is null)
        {
            _previewOutput?.Dispose();
            _previewOutput = null;
            return;
        }

        surface.SetDefaultBufferSize(FullHdWidth, FullHdHeight);
        _previewOutput?.Dispose();
        _previewOutput = new Surface(surface);
        onReady?.Invoke(0, FullHdWidth, FullHdHeight);
        if (_wantPreview)
            _ = ReconfigureCameraAsync();
    }

    public static async Task StartLocalPreviewAsync()
    {
        _wantPreview = true;
        await ReconfigureCameraAsync();
    }

    public static void StopCamera()
    {
        _reconnectCancellation?.Cancel();
        _streamCancellation?.Cancel();
        _captureSession?.Close();
        _captureSession?.Dispose();
        _captureSession = null;
        _camera?.Close();
        _camera?.Dispose();
        _camera = null;
        StopEncoder();
        _cameraThread?.QuitSafely();
        _cameraThread?.Dispose();
        _cameraThread = null;
        _cameraHandler = null;
        _outputStream?.Dispose();
        _outputStream = null;
        _client?.Dispose();
        _client = null;
    }

    public static async Task StartAsync(string host, int port, string discoveredFingerprint)
    {
        var previousTask = _reconnectTask;
        _reconnectCancellation?.Cancel();
        _outputStream?.Dispose();
        if (previousTask is not null)
        {
            try { await previousTask; }
            catch (System.OperationCanceledException) { }
        }
        _reconnectCancellation?.Dispose();
        _reconnectCancellation = new CancellationTokenSource();
        var token = _reconnectCancellation.Token;
        _reconnectTask = Task.Run(
            () => ReconnectLoopAsync(host, port, discoveredFingerprint, token),
            token);
        await Task.Yield();
    }

    public static async Task StartAccessoryAsync(System.IO.Stream accessoryStream)
    {
        ArgumentNullException.ThrowIfNull(accessoryStream);
        var previousTask = _reconnectTask;
        _reconnectCancellation?.Cancel();
        _outputStream?.Dispose();
        if (previousTask is not null)
        {
            try { await previousTask; }
            catch (System.OperationCanceledException) { }
        }
        _reconnectCancellation?.Dispose();
        _reconnectCancellation = new CancellationTokenSource();
        var token = _reconnectCancellation.Token;
        _reconnectTask = Task.Run(
            () => RunAccessorySessionAsync(accessoryStream, token),
            token);
        await Task.Yield();
    }

    private static async Task RunAccessorySessionAsync(
        System.IO.Stream accessoryStream,
        CancellationToken token)
    {
        try
        {
            ConnectionStatusChanged?.Invoke(
                "Connecting by production USB accessory…");
            await ConnectStreamOnceAsync(accessoryStream, token);
            if (_drainTask is not null)
                await MonitorStreamAsync(_drainTask, usb: true, token);
        }
        catch (System.OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("BobrCam", ex.ToString());
            LastError = ex.GetBaseException().Message;
            ConnectionStatusChanged?.Invoke(
                $"Production USB disconnected: {LastError}");
        }
        finally
        {
            await StopStreamingSessionAsync();
        }
    }

    private static async Task ReconnectLoopAsync(
        string host,
        int port,
        string discoveredFingerprint,
        CancellationToken token)
    {
        var retryDelay = TimeSpan.FromSeconds(1);
        while (!token.IsCancellationRequested)
        {
            try
            {
                var usb = IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
                ConnectionStatusChanged?.Invoke(
                    $"Connecting by {(usb ? "USB" : "secure Wi-Fi")} to {host}:{port}…");
                await ConnectOnceAsync(host, port, discoveredFingerprint, token);
                retryDelay = TimeSpan.FromSeconds(1);
                if (_drainTask is not null)
                    await MonitorStreamAsync(_drainTask, usb, token);
                if (!token.IsCancellationRequested)
                    throw new IOException("Video connection closed.");
            }
            catch (System.OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Android.Util.Log.Error("BobrCam", ex.ToString());
                LastError = ex.GetBaseException().Message;
                ConnectionStatusChanged?.Invoke(
                    $"Disconnected: {LastError} Retrying in {retryDelay.TotalSeconds:0}s…");
            }
            finally
            {
                await StopStreamingSessionAsync();
            }

            try
            {
                await Task.Delay(retryDelay, token);
            }
            catch (System.OperationCanceledException)
            {
                break;
            }
            retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 5));
        }
    }

    private static async Task MonitorStreamAsync(
        Task drainTask,
        bool usb,
        CancellationToken token)
    {
        var previousFrames = -1;
        var stalledChecks = 0;
        while (!drainTask.IsCompleted)
        {
            var delay = Task.Delay(TimeSpan.FromSeconds(2), token);
            if (await Task.WhenAny(drainTask, delay) == drainTask)
            {
                await drainTask;
                return;
            }

            var frames = Volatile.Read(ref FramesSent);
            ConnectionStatusChanged?.Invoke(
                usb
                    ? $"Connected by USB — H.264 frames: {frames}"
                    : $"Connected by Wi-Fi — encrypted H.264 frames: {frames}");
            stalledChecks = frames == previousFrames ? stalledChecks + 1 : 0;
            previousFrames = frames;
            if (stalledChecks >= 5)
                throw new IOException("Camera stream stalled.");
        }
        await drainTask;
    }

    private static async Task ConnectOnceAsync(
        string host,
        int port,
        string discoveredFingerprint,
        CancellationToken reconnectToken)
    {
        LastError = null;
        FramesSent = 0;
        _sequence = 0;

        _streamCancellation?.Cancel();
        _streamCancellation?.Dispose();
        _streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(reconnectToken);
        _outputStream?.Dispose();
        _client?.Dispose();

        _client = new TcpClient { NoDelay = true };
        await _client.ConnectAsync(host, port, _streamCancellation.Token);
        var isUsb = IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
        if (isUsb)
        {
            _outputStream = _client.GetStream();
        }
        else
        {
            string presentedFingerprint = string.Empty;
            var paired = await SecureIdentity.GetPairedReceiverFingerprintAsync();
            var secure = new SslStream(_client.GetStream(), false, (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                presentedFingerprint = SecureIdentity.Fingerprint(new X509Certificate2(certificate));
                if (!string.IsNullOrEmpty(discoveredFingerprint) &&
                    !SecureIdentity.FixedTimeEquals(discoveredFingerprint, presentedFingerprint))
                    return false;
                return string.IsNullOrEmpty(paired) ||
                       SecureIdentity.FixedTimeEquals(paired, presentedFingerprint);
            });
            using (var tlsTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                       _streamCancellation.Token))
            {
                tlsTimeout.CancelAfter(HandshakeTimeout);
                await secure.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "bobrcam.local",
                    EnabledSslProtocols = SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, tlsTimeout.Token);
            }

            await SecureIdentity.SetPairedReceiverFingerprintAsync(
                presentedFingerprint);
            _outputStream = secure;
        }
        await StartProtocolSessionAsync();
    }

    private static async Task ConnectStreamOnceAsync(
        System.IO.Stream stream,
        CancellationToken reconnectToken)
    {
        LastError = null;
        FramesSent = 0;
        _sequence = 0;

        _streamCancellation?.Cancel();
        _streamCancellation?.Dispose();
        _streamCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(reconnectToken);
        _outputStream?.Dispose();
        _client?.Dispose();
        _client = null;
        _outputStream = stream;
        await StartProtocolSessionAsync();
    }

    private static async Task StartProtocolSessionAsync()
    {
        var cancellation = _streamCancellation ??
            throw new InvalidOperationException("Video session is not initialized.");
        var output = _outputStream ??
            throw new IOException("Receiver connection is closed.");
        using var handshakeTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        handshakeTimeout.CancelAfter(HandshakeTimeout);
        var handshakeToken = handshakeTimeout.Token;
        var challenge = new byte[VideoProtocol.AuthenticationChallengeSize];
        await output.ReadExactlyAsync(
            challenge,
            handshakeToken);
        var nonce = new byte[32];
        if (!VideoProtocol.TryReadAuthenticationChallenge(challenge, nonce))
            throw new AuthenticationException("Invalid Windows authentication challenge.");

        var pairingToken = await SecureIdentity.GetOrCreatePairingTokenAsync();
        var authentication = new byte[VideoProtocol.AuthenticationResponseSize];
        VideoProtocol.WriteAuthenticationResponse(
            authentication,
            pairingToken,
            nonce);
        await output.WriteAsync(authentication, handshakeToken);
        await output.FlushAsync(handshakeToken);
        var streamRequest = new byte[VideoProtocol.StreamRequestSize];
        await output.ReadExactlyAsync(streamRequest, handshakeToken);
        if (!VideoProtocol.TryReadStreamRequest(
                streamRequest,
                out var requestedFps,
                out var requestedWidth,
                out var requestedHeight,
                out var prioritizeResolution,
                out var useFrontCamera))
            throw new InvalidDataException("Invalid Windows stream request.");
        _requestedFps = requestedFps;
        _requestedWidth = requestedWidth;
        _requestedHeight = requestedHeight;
        _prioritizeResolution = prioritizeResolution;
        _useFrontCamera = useFrontCamera;

        await StateGate.WaitAsync(cancellation.Token);
        try
        {
            _captureSession?.Close();
            _captureSession?.Dispose();
            _captureSession = null;
            _camera?.Close();
            _camera?.Dispose();
            _camera = null;
            _cameraCharacteristics = null;
            StopEncoder();
            await EnsureCameraOpenAsync();
            ConfigureStreamMode();
            StartEncoder();
            await CreateCaptureSessionAsync(streaming: true);
            _controlTask = Task.Run(() => ReadControlsAsync(cancellation.Token));
            await SendCameraCapabilitiesAsync(cancellation.Token);
            _drainTask = Task.Run(() => DrainEncoderAsync(cancellation.Token));
        }
        finally
        {
            StateGate.Release();
        }
    }

    private static async Task StopStreamingSessionAsync()
    {
        _streamCancellation?.Cancel();
        var drainTask = _drainTask;
        if (drainTask is not null)
        {
            try { await drainTask.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch { }
        }
        await StateGate.WaitAsync();
        try
        {
            _captureSession?.Close();
            _captureSession?.Dispose();
            _captureSession = null;
            _repeatingRequestBuilder?.Dispose();
            _repeatingRequestBuilder = null;
            StopEncoder();
            _outputStream?.Dispose();
            _outputStream = null;
            _client?.Dispose();
            _client = null;
            _drainTask = null;
            _controlTask = null;
            if (_wantPreview && _previewOutput is not null)
                await CreateCaptureSessionAsync(streaming: false);
        }
        catch (Exception ex)
        {
            LastError = ex.GetBaseException().Message;
        }
        finally
        {
            StateGate.Release();
        }
    }

    private static async Task ReconfigureCameraAsync()
    {
        if (!_wantPreview || _previewOutput is null) return;
        await StateGate.WaitAsync();
        try
        {
            await EnsureCameraOpenAsync();
            await CreateCaptureSessionAsync(streaming: _encoderSurface is not null);
        }
        catch (Exception ex)
        {
            LastError = ex.GetBaseException().Message;
            throw;
        }
        finally
        {
            StateGate.Release();
        }
    }

    private static async Task EnsureCameraOpenAsync()
    {
        if (_camera is not null) return;
        if (_cameraThread is null)
        {
            _cameraThread = new HandlerThread("BobrCam.Camera2");
            _cameraThread.Start();
            _cameraHandler = new Handler(_cameraThread.Looper!);
        }

        var context = Platform.CurrentActivity ?? Android.App.Application.Context;
        var manager = (CameraManager)context.GetSystemService(Context.CameraService)!;
        var cameraId = manager.GetCameraIdList()
            .FirstOrDefault(id =>
                ((Java.Lang.Integer?)manager.GetCameraCharacteristics(id)
                    .Get(CameraCharacteristics.LensFacing))?.IntValue() ==
                (int)(_useFrontCamera ? LensFacing.Front : LensFacing.Back))
            ?? manager.GetCameraIdList().First();
        Android.Util.Log.Info(
            "BobrCam",
            $"Opening camera {cameraId} ({(_useFrontCamera ? "front" : "rear")}).");

        var completion = new TaskCompletionSource<CameraDevice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OpenCamera(cameraId, new DeviceStateCallback(completion), _cameraHandler);
        _camera = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _cameraCharacteristics = manager.GetCameraCharacteristics(cameraId);
        ConfigureStreamMode();
    }

    private static void ConfigureStreamMode()
    {
        var characteristics = _cameraCharacteristics
            ?? throw new InvalidOperationException("Camera characteristics are unavailable.");
        (_streamWidth, _streamHeight) = SelectSupportedResolution(
            characteristics,
            _requestedWidth,
            _requestedHeight);
        Android.Util.Range? highSpeedRange = null;
        var cameraSupportsHighSpeed =
            _requestedFps >= PreferredFps &&
            OperatingSystem.IsAndroidVersionAtLeast(29) &&
            TrySelectHighSpeedMode(
                characteristics,
                _streamWidth,
                _streamHeight,
                _requestedFps,
                out highSpeedRange);
        _useHighSpeedSession =
            cameraSupportsHighSpeed &&
            (_requestedFps <= PreferredFps ||
             SupportsHardwareAvcEncoding(
                 _streamWidth,
                 _streamHeight,
                 _requestedFps));
        if (cameraSupportsHighSpeed && !_useHighSpeedSession)
        {
            Android.Util.Log.Info(
                "BobrCam",
                $"Hardware AVC cannot sustain {_streamWidth}x{_streamHeight} at {_requestedFps} FPS; falling back to {PreferredFps} FPS.");
            ConnectionStatusChanged?.Invoke(
                $"{_streamWidth}x{_streamHeight} at {_requestedFps} FPS is not supported by the hardware encoder; using {PreferredFps} FPS.");
        }
        var effectiveRequestedFps =
            _requestedFps > PreferredFps && !_useHighSpeedSession
                ? PreferredFps
                : _requestedFps;
        if (_useHighSpeedSession)
        {
            _targetFpsRange = highSpeedRange;
            _streamFps = _requestedFps;
        }
        else
        {
            if (!_prioritizeResolution &&
                effectiveRequestedFps >= PreferredFps &&
                !SupportsFrameRate(
                    characteristics,
                    _streamWidth,
                    _streamHeight,
                    effectiveRequestedFps))
            {
                (_streamWidth, _streamHeight) = SelectSupportedResolution(
                    characteristics,
                    HdWidth,
                    HdHeight);
            }
            _targetFpsRange = DetermineFrameRate(
                characteristics,
                effectiveRequestedFps,
                _streamWidth,
                _streamHeight);
            _streamFps = _targetFpsRange is null
                ? FallbackFps
                : Convert.ToInt32(_targetFpsRange.Upper);
        }
        _streamBitrate = _streamHeight switch
        {
            >= UltraHdHeight => UltraHdBitrate,
            >= QuadHdHeight => QuadHdBitrate,
            <= HdHeight => HdBitrate,
            _ => FullHdBitrate
        };
    }

    private static bool TrySelectHighSpeedMode(
        CameraCharacteristics characteristics,
        int width,
        int height,
        int requestedFps,
        out Android.Util.Range? selectedRange)
    {
        selectedRange = null;
        var map = (StreamConfigurationMap?)characteristics.Get(
            CameraCharacteristics.ScalerStreamConfigurationMap);
        var size = map?.GetHighSpeedVideoSizes()?.FirstOrDefault(candidate =>
            candidate.Width == width && candidate.Height == height);
        if (size is null)
            return false;
        var ranges = map!.GetHighSpeedVideoFpsRangesFor(size);
        if (ranges is null)
            return false;
        selectedRange = ranges
            .Where(range =>
                Convert.ToInt32(range.Lower) == Convert.ToInt32(range.Upper) &&
                Convert.ToInt32(range.Upper) >= requestedFps)
            .OrderBy(range => Convert.ToInt32(range.Upper))
            .FirstOrDefault();
        return selectedRange is not null;
    }

    private static bool SupportsHardwareAvcEncoding(
        int width,
        int height,
        int frameRate)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(29))
            return false;
        try
        {
            using var format = MediaFormat.CreateVideoFormat(
                MediaFormat.MimetypeVideoAvc,
                width,
                height);
            format.SetInteger(
                MediaFormat.KeyColorFormat,
                (int)MediaCodecCapabilities.Formatsurface);
            format.SetInteger(MediaFormat.KeyFrameRate, frameRate);
            format.SetInteger(
                MediaFormat.KeyProfile,
                (int)MediaCodecProfileType.Avcprofilemain);
            format.SetInteger(
                MediaFormat.KeyLevel,
                (int)GetEncoderLevel(height, frameRate));

            using var codecList = new MediaCodecList(MediaCodecListKind.AllCodecs);
            foreach (var codec in codecList.GetCodecInfos() ?? [])
            {
                if (codec is null || !codec.IsEncoder ||
                    !codec.IsHardwareAccelerated ||
                    !(codec.GetSupportedTypes() ?? []).Any(type =>
                        string.Equals(
                            type,
                            MediaFormat.MimetypeVideoAvc,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var capabilities = codec.GetCapabilitiesForType(
                    MediaFormat.MimetypeVideoAvc);
                var video = capabilities?.VideoCapabilities;
                if (video is null ||
                    !video.AreSizeAndRateSupported(width, height, frameRate) ||
                    capabilities is null ||
                    !capabilities.IsFormatSupported(format))
                {
                    continue;
                }

                try
                {
                    var achievable = video.GetAchievableFrameRatesFor(width, height);
                    if (achievable is not null &&
                        Convert.ToDouble(achievable.Upper) < frameRate)
                    {
                        continue;
                    }
                }
                catch (ArgumentException)
                {
                    // Some vendors omit measured rates; declared support remains usable.
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn(
                "BobrCam",
                $"Could not query AVC encoder performance: {ex.GetBaseException().Message}");
        }
        return false;
    }

    private static Android.Util.Range? DetermineFrameRate(
        CameraCharacteristics characteristics,
        int requestedFps,
        int width,
        int height)
    {
        var durationLimit = GetMaximumFrameRate(characteristics, width, height);
        Android.Util.Range[]? ranges;
        try
        {
            ranges = (Android.Util.Range[]?)characteristics.Get(
                CameraCharacteristics.ControlAeAvailableTargetFpsRanges);
        }
        catch (InvalidCastException)
        {
            ranges = null;
        }
        if (ranges is null || ranges.Length == 0)
        {
            var compatibleMaximum = durationLimit > 0
                ? Math.Min(requestedFps, durationLimit)
                : Math.Min(requestedFps, FallbackFps);
            if (!OperatingSystem.IsAndroidVersionAtLeast(28))
                compatibleMaximum = Math.Min(compatibleMaximum, FallbackFps);
            var value = new Java.Lang.Integer(Math.Max(1, compatibleMaximum));
            return new Android.Util.Range(value, value);
        }
        var maximum = durationLimit > 0
            ? Math.Min(requestedFps, durationLimit)
            : requestedFps;
        return ranges
            .Where(range => Convert.ToInt32(range.Upper) <= maximum)
            .OrderByDescending(range => Convert.ToInt32(range.Upper))
            .ThenByDescending(range => Convert.ToInt32(range.Lower))
            .FirstOrDefault()
            ?? ranges.OrderBy(range => Convert.ToInt32(range.Upper)).First();
    }

    private static (int Width, int Height) SelectSupportedResolution(
        CameraCharacteristics characteristics,
        int requestedWidth,
        int requestedHeight)
    {
        var map = (StreamConfigurationMap?)characteristics.Get(
            CameraCharacteristics.ScalerStreamConfigurationMap);
        var sizes = map?.GetOutputSizes(
            Java.Lang.Class.FromType(typeof(SurfaceTexture)));
        if (sizes is null || sizes.Length == 0)
            return (FullHdWidth, FullHdHeight);

        var exact = sizes.FirstOrDefault(size =>
            size.Width == requestedWidth && size.Height == requestedHeight);
        if (exact is not null)
            return (exact.Width, exact.Height);

        var requestedPixels = (long)requestedWidth * requestedHeight;
        var fallback = sizes
            .Where(size =>
                size.Width * 9L == size.Height * 16L &&
                (long)size.Width * size.Height <= requestedPixels)
            .OrderByDescending(size => (long)size.Width * size.Height)
            .FirstOrDefault()
            ?? sizes.OrderBy(size =>
                Math.Abs((long)size.Width * size.Height - requestedPixels))
                .First();
        return (fallback.Width, fallback.Height);
    }

    private static bool SupportsFrameRate(
        CameraCharacteristics characteristics,
        int width,
        int height,
        int frameRate)
    {
        var maximum = GetMaximumFrameRate(characteristics, width, height);
        return maximum >= frameRate;
    }

    private static int GetMaximumFrameRate(
        CameraCharacteristics characteristics,
        int width,
        int height)
    {
        var map = (StreamConfigurationMap?)characteristics.Get(
            CameraCharacteristics.ScalerStreamConfigurationMap);
        if (map is null) return 0;
        try
        {
            var duration = map.GetOutputMinFrameDuration(
                Java.Lang.Class.FromType(typeof(SurfaceTexture)),
                new Android.Util.Size(width, height));
            return duration > 0
                ? (int)(1_000_000_000L / duration)
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static void StartEncoder()
    {
        var format = MediaFormat.CreateVideoFormat(
            MediaFormat.MimetypeVideoAvc,
            _streamWidth,
            _streamHeight);
        format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
        format.SetInteger(MediaFormat.KeyBitRate, _streamBitrate);
        format.SetInteger(MediaFormat.KeyFrameRate, _streamFps);
        if (_useHighSpeedSession)
        {
            format.SetFloat(
                MediaFormat.KeyCaptureRate,
                Convert.ToSingle(_targetFpsRange!.Upper));
            format.SetFloat("max-fps-to-encoder", _streamFps);
        }
        format.SetInteger(MediaFormat.KeyIFrameInterval, 1);
        format.SetInteger(MediaFormat.KeyProfile, (int)MediaCodecProfileType.Avcprofilemain);
        format.SetInteger(MediaFormat.KeyLevel, (int)GetEncoderLevel());
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
            format.SetInteger(MediaFormat.KeyMaxBFrames, 0);

        _encoder = MediaCodec.CreateEncoderByType(MediaFormat.MimetypeVideoAvc);
        _encoder.Configure(format, null, null, MediaCodecConfigFlags.Encode);
        _encoderSurface = _encoder.CreateInputSurface();
        _encoder.Start();
    }

    private static void StopEncoder()
    {
        try { _encoder?.SignalEndOfInputStream(); } catch { }
        try { _encoder?.Stop(); } catch { }
        _encoderSurface?.Dispose();
        _encoderSurface = null;
        _encoder?.Dispose();
        _encoder = null;
    }

    private static async Task CreateCaptureSessionAsync(bool streaming)
    {
        if (_camera is null) return;
        _repeatingRequestBuilder?.Dispose();
        _repeatingRequestBuilder = null;
        _captureSession?.Close();
        _captureSession?.Dispose();
        _captureSession = null;

        var outputs = new List<Surface>();
        if (_wantPreview && _previewOutput is not null)
            outputs.Add(_previewOutput);
        if (streaming && _encoderSurface is not null)
            outputs.Add(_encoderSurface);
        if (outputs.Count == 0)
            return;

        var useHighSpeed =
            _useHighSpeedSession &&
            streaming &&
            outputs.Count == 1;
        _activeFpsRange = useHighSpeed
            ? _targetFpsRange
            : DetermineFrameRate(
                _cameraCharacteristics!,
                _requestedFps,
                _streamWidth,
                _streamHeight);

        var completion = new TaskCompletionSource<CameraCaptureSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _captureSessionClosed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _captureSessionCallback = new SessionStateCallback(
            completion,
            _captureSessionClosed);
        if (useHighSpeed)
            _camera.CreateConstrainedHighSpeedCaptureSession(
                outputs,
                _captureSessionCallback,
                _cameraHandler);
        else
            _camera.CreateCaptureSession(
                outputs,
                _captureSessionCallback,
                _cameraHandler);
        _captureSession = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var request = _camera.CreateCaptureRequest(
            streaming ? CameraTemplate.Record : CameraTemplate.Preview);
        _repeatingRequestBuilder = request;
        foreach (var output in outputs)
            request.AddTarget(output);
        ApplyCameraSettings(request);
        SubmitRepeatingRequest();
        if (useHighSpeed)
            ConnectionStatusChanged?.Invoke(
                $"High-speed camera active: {_streamWidth}x{_streamHeight} at {_streamFps} FPS.");
    }

    private static void SubmitRepeatingRequest()
    {
        if (_captureSession is null || _repeatingRequestBuilder is null)
            return;
        var request = _repeatingRequestBuilder.Build();
        if (_captureSession is CameraConstrainedHighSpeedCaptureSession highSpeed)
        {
            var burst = highSpeed.CreateHighSpeedRequestList(request);
            highSpeed.SetRepeatingBurst(burst, null, _cameraHandler);
            return;
        }
        _captureSession.SetRepeatingRequest(request, null, _cameraHandler);
    }

    private static void ApplyCameraSettings(CaptureRequest.Builder request)
    {
        request.Set(
            CaptureRequest.ControlMode!,
            new Java.Lang.Integer((int)ControlMode.Auto));
        request.Set(
            CaptureRequest.FlashMode!,
            new Java.Lang.Integer((int)(_flashEnabled
                ? Android.Hardware.Camera2.FlashMode.Torch
                : Android.Hardware.Camera2.FlashMode.Off)));
        if (_activeFpsRange is not null)
            request.Set(CaptureRequest.ControlAeTargetFpsRange!, _activeFpsRange);

        var exposureRange = GetExposureCompensationRange();
        if (exposureRange is not null)
        {
            _exposureCompensation = Math.Clamp(
                _exposureCompensation,
                Convert.ToInt32(exposureRange.Lower),
                Convert.ToInt32(exposureRange.Upper));
            request.Set(
                CaptureRequest.ControlAeExposureCompensation!,
                new Java.Lang.Integer(_exposureCompensation));
        }

        var supportedWhiteBalance = GetSupportedWhiteBalanceModes();
        if (!IsWhiteBalanceSupported(supportedWhiteBalance, _whiteBalanceMode))
            _whiteBalanceMode = CameraWhiteBalanceMode.Auto;
        request.Set(
            CaptureRequest.ControlAwbMode!,
            new Java.Lang.Integer((int)_whiteBalanceMode));
        var maximumZoom = GetMaximumZoom();
        _zoomHundredths = Math.Clamp(
            _zoomHundredths,
            100,
            (int)Math.Round(maximumZoom * 100));
        var cropRegion = GetCurrentCropRegion();
        if (cropRegion is not null)
            request.Set(CaptureRequest.ScalerCropRegion!, cropRegion);
    }

    private static async Task ApplyCameraControlAsync(
        CameraControlMessage message,
        CancellationToken token)
    {
        await StateGate.WaitAsync(token);
        try
        {
            var cameraRequestChanged = false;
            switch (message.Command)
            {
                case CameraControlCommand.SetExposureCompensation:
                    _exposureCompensation = message.Value;
                    cameraRequestChanged = true;
                    break;
                case CameraControlCommand.SetZoom:
                    _zoomHundredths = message.Value;
                    cameraRequestChanged = true;
                    break;
                case CameraControlCommand.SetWhiteBalance:
                    _whiteBalanceMode = (CameraWhiteBalanceMode)message.Value;
                    cameraRequestChanged = true;
                    break;
                default:
                    return;
            }

            if (!cameraRequestChanged)
                return;

            if (_captureSession is null || _repeatingRequestBuilder is null)
                return;
            ApplyCameraSettings(_repeatingRequestBuilder);
            SubmitRepeatingRequest();
        }
        finally
        {
            StateGate.Release();
        }
    }

    private static async Task ReadControlsAsync(CancellationToken token)
    {
        var input = _outputStream ??
            throw new IOException("Receiver connection is closed.");
        var message = new byte[VideoProtocol.CameraControlSize];
        while (!token.IsCancellationRequested)
        {
            await input.ReadExactlyAsync(message, token);
            if (!VideoProtocol.TryReadCameraControl(message, out var control))
                throw new InvalidDataException("Invalid camera control message.");
            if (control.Command == CameraControlCommand.SwitchCamera)
                await SwitchCameraAsync(token);
            else if (control.Command == CameraControlCommand.ToggleFlash)
                await ToggleFlashAsync(token);
            else
                await ApplyCameraControlAsync(control, token);
        }
    }

    private static async Task SwitchCameraAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _useFrontCamera = !_useFrontCamera;
        _flashEnabled = false;
        ConnectionStatusChanged?.Invoke(
            _useFrontCamera
                ? "Switching to front camera…"
                : "Switching to rear camera…");

        // Front and rear cameras often expose different supported output sizes.
        // Closing the transport makes the normal reconnect path rebuild Camera2
        // and MediaCodec together for the newly selected camera.
        _outputStream?.Dispose();
    }

    private static async Task ToggleFlashAsync(CancellationToken token)
    {
        await StateGate.WaitAsync(token);
        try
        {
            var available = (Java.Lang.Boolean?)_cameraCharacteristics?.Get(
                CameraCharacteristics.FlashInfoAvailable);
            if (available?.BooleanValue() != true)
                return;
            _flashEnabled = !_flashEnabled;
            if (_captureSession is null || _repeatingRequestBuilder is null)
                return;
            ApplyCameraSettings(_repeatingRequestBuilder);
            SubmitRepeatingRequest();
        }
        finally
        {
            StateGate.Release();
        }
    }

    private static async Task SendCameraCapabilitiesAsync(CancellationToken token)
    {
        var exposureRange = GetExposureCompensationRange();
        var minimumExposure = exposureRange is null
            ? 0
            : Math.Clamp(Convert.ToInt32(exposureRange.Lower), sbyte.MinValue, sbyte.MaxValue);
        var maximumExposure = exposureRange is null
            ? 0
            : Math.Clamp(Convert.ToInt32(exposureRange.Upper), sbyte.MinValue, sbyte.MaxValue);
        var maximumZoom = Math.Clamp(
            (int)Math.Round(GetMaximumZoom() * 100),
            100,
            ushort.MaxValue);
        var whiteBalanceModes = GetSupportedWhiteBalanceModes();
        var flashAvailable = ((Java.Lang.Boolean?)_cameraCharacteristics?.Get(
            CameraCharacteristics.FlashInfoAvailable))?.BooleanValue() == true;

        var flags = CameraCapabilityFlags.None;
        if (flashAvailable)
            flags |= CameraCapabilityFlags.Flash;
        if (minimumExposure < maximumExposure)
            flags |= CameraCapabilityFlags.ExposureCompensation;
        if (maximumZoom > 100)
            flags |= CameraCapabilityFlags.Zoom;
        if (whiteBalanceModes != CameraWhiteBalanceModes.None)
            flags |= CameraCapabilityFlags.WhiteBalance;
        var payload = new byte[VideoProtocol.CameraCapabilitiesSize];
        var capabilities = new CameraCapabilities(
            flags,
            (sbyte)minimumExposure,
            (sbyte)maximumExposure,
            (ushort)maximumZoom,
            whiteBalanceModes,
            (sbyte)_exposureCompensation,
            (ushort)_zoomHundredths,
            _whiteBalanceMode);
        if (!VideoProtocol.TryWriteCameraCapabilities(payload, capabilities))
            throw new InvalidOperationException("Could not serialize camera capabilities.");
        await SendPacketAsync(
            VideoPacketType.CameraCapabilities,
            VideoPacketFlags.None,
            payload,
            0,
            0,
            token);
    }

    private static Android.Util.Range? GetExposureCompensationRange() =>
        (Android.Util.Range?)_cameraCharacteristics?.Get(
            CameraCharacteristics.ControlAeCompensationRange);

    private static float GetMaximumZoom()
    {
        var value = (Java.Lang.Float?)_cameraCharacteristics?.Get(
            CameraCharacteristics.ScalerAvailableMaxDigitalZoom);
        return Math.Max(1f, value?.FloatValue() ?? 1f);
    }

    private static CameraWhiteBalanceModes GetSupportedWhiteBalanceModes()
    {
        var available = (int[]?)_cameraCharacteristics?.Get(
            CameraCharacteristics.ControlAwbAvailableModes);
        if (available is null)
            return CameraWhiteBalanceModes.None;

        var result = CameraWhiteBalanceModes.None;
        foreach (var mode in available)
        {
            result |= (CameraWhiteBalanceMode)mode switch
            {
                CameraWhiteBalanceMode.Auto => CameraWhiteBalanceModes.Auto,
                CameraWhiteBalanceMode.Incandescent =>
                    CameraWhiteBalanceModes.Incandescent,
                CameraWhiteBalanceMode.Fluorescent =>
                    CameraWhiteBalanceModes.Fluorescent,
                CameraWhiteBalanceMode.Daylight => CameraWhiteBalanceModes.Daylight,
                CameraWhiteBalanceMode.CloudyDaylight =>
                    CameraWhiteBalanceModes.CloudyDaylight,
                _ => CameraWhiteBalanceModes.None
            };
        }
        return result;
    }

    private static bool IsWhiteBalanceSupported(
        CameraWhiteBalanceModes supported,
        CameraWhiteBalanceMode mode) =>
        mode switch
        {
            CameraWhiteBalanceMode.Auto =>
                supported.HasFlag(CameraWhiteBalanceModes.Auto),
            CameraWhiteBalanceMode.Incandescent =>
                supported.HasFlag(CameraWhiteBalanceModes.Incandescent),
            CameraWhiteBalanceMode.Fluorescent =>
                supported.HasFlag(CameraWhiteBalanceModes.Fluorescent),
            CameraWhiteBalanceMode.Daylight =>
                supported.HasFlag(CameraWhiteBalanceModes.Daylight),
            CameraWhiteBalanceMode.CloudyDaylight =>
                supported.HasFlag(CameraWhiteBalanceModes.CloudyDaylight),
            _ => false
        };

    private static Android.Graphics.Rect? GetCurrentCropRegion()
    {
        var activeArray = _cameraCharacteristics?.Get(
            CameraCharacteristics.SensorInfoActiveArraySize)
            as Android.Graphics.Rect;
        if (activeArray is null)
            return null;
        var zoom = Math.Max(1f, _zoomHundredths / 100f);
        var cropWidth = Math.Max(2, (int)(activeArray.Width() / zoom));
        var cropHeight = Math.Max(2, (int)(activeArray.Height() / zoom));
        var left = activeArray.Left + (activeArray.Width() - cropWidth) / 2;
        var top = activeArray.Top + (activeArray.Height() - cropHeight) / 2;
        return new Android.Graphics.Rect(
            left,
            top,
            left + cropWidth,
            top + cropHeight);
    }

    private static async Task DrainEncoderAsync(CancellationToken token)
    {
        var encoder = _encoder ?? throw new InvalidOperationException("H.264 encoder is not running.");
        var info = new MediaCodec.BufferInfo();
        byte[] codecData = [];
        var configurationSent = false;

        try
        {
            while (!token.IsCancellationRequested)
            {
                var index = encoder.DequeueOutputBuffer(info, 10_000);
                if (index == (int)MediaCodecInfoState.OutputFormatChanged)
                {
                    codecData = ReadCodecData(encoder.OutputFormat);
                    await SendConfigurationAsync(codecData, token);
                    configurationSent = true;
                    continue;
                }
                if (index < 0) continue;

                try
                {
                    var codecConfig = (info.Flags & MediaCodecBufferFlags.CodecConfig) != 0;
                    if (codecConfig || info.Size <= 0) continue;
                    if (!configurationSent)
                    {
                        codecData = ReadCodecData(encoder.OutputFormat);
                        await SendConfigurationAsync(codecData, token);
                        configurationSent = true;
                    }

                    var buffer = encoder.GetOutputBuffer(index)
                        ?? throw new InvalidOperationException("Encoder returned an empty output buffer.");
                    buffer.Position(info.Offset);
                    buffer.Limit(info.Offset + info.Size);
                    var encoded = GC.AllocateUninitializedArray<byte>(info.Size);
                    buffer.Get(encoded);

                    var keyFrame = (info.Flags & MediaCodecBufferFlags.KeyFrame) != 0;
                    var payload = keyFrame && codecData.Length > 0
                        ? JoinCodecDataAndAccessUnit(codecData, encoded)
                        : EnsureAnnexB(encoded);
                    await SendPacketAsync(
                        VideoPacketType.AccessUnit,
                        keyFrame ? VideoPacketFlags.KeyFrame : VideoPacketFlags.None,
                        payload,
                        info.PresentationTimeUs,
                        (uint)(1_000_000 / _streamFps),
                        token);
                    Interlocked.Increment(ref FramesSent);
                }
                finally
                {
                    encoder.ReleaseOutputBuffer(index, false);
                }
            }
        }
        catch (System.OperationCanceledException) { }
        catch (Exception ex)
        {
            LastError = ex.GetBaseException().Message;
        }
    }

    private static byte[] ReadCodecData(MediaFormat format)
    {
        var parts = new List<byte[]>(2);
        for (var index = 0; index < 2; index++)
        {
            var buffer = format.GetByteBuffer($"csd-{index}");
            if (buffer is null) continue;
            var bytes = new byte[buffer.Remaining()];
            buffer.Get(bytes);
            parts.Add(EnsureAnnexB(bytes));
        }
        var length = parts.Sum(part => part.Length);
        var result = new byte[length];
        var offset = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }
        return result;
    }

    private static byte[] JoinCodecDataAndAccessUnit(byte[] codecData, byte[] accessUnit)
    {
        var annexB = EnsureAnnexB(accessUnit);
        var result = GC.AllocateUninitializedArray<byte>(codecData.Length + annexB.Length);
        codecData.CopyTo(result, 0);
        annexB.CopyTo(result, codecData.Length);
        return result;
    }

    private static byte[] EnsureAnnexB(byte[] data)
    {
        if (data.Length >= 4 && data[0] == 0 && data[1] == 0 &&
            (data[2] == 1 || (data[2] == 0 && data[3] == 1)))
            return data;

        var converted = TryConvertAvccToAnnexB(data);
        if (converted is not null)
            return converted;

        var output = GC.AllocateUninitializedArray<byte>(data.Length + 4);
        output[0] = 0;
        output[1] = 0;
        output[2] = 0;
        output[3] = 1;
        data.CopyTo(output, 4);
        return output;
    }

    private static byte[]? TryConvertAvccToAnnexB(byte[] data)
    {
        if (data.Length < 5) return null;
        var output = (byte[])data.Clone();
        var offset = 0;
        var nalCount = 0;
        while (offset + 4 <= output.Length)
        {
            var nalLength = BinaryPrimitives.ReadInt32BigEndian(
                output.AsSpan(offset, 4));
            if (nalLength <= 0 || offset + 4 + nalLength > output.Length)
                return null;
            output[offset] = 0;
            output[offset + 1] = 0;
            output[offset + 2] = 0;
            output[offset + 3] = 1;
            offset += 4 + nalLength;
            nalCount++;
        }
        return offset == output.Length && nalCount > 0 ? output : null;
    }

    private static async Task SendConfigurationAsync(byte[] codecData, CancellationToken token)
    {
        var configuration = new H264StreamConfiguration(
            (ushort)_streamWidth,
            (ushort)_streamHeight,
            (ushort)_streamFps,
            1,
            (uint)_streamBitrate,
            H264Profile.Main,
            GetProtocolLevel(),
            8,
            H264ChromaFormat.Yuv420,
            1000,
            0,
            0);
        var payload = new byte[VideoProtocol.StreamConfigurationSize + codecData.Length];
        if (!VideoProtocol.TryWriteStreamConfiguration(payload, configuration))
            throw new InvalidOperationException("Could not serialize H.264 stream configuration.");
        codecData.CopyTo(payload, VideoProtocol.StreamConfigurationSize);
        await SendPacketAsync(
            VideoPacketType.StreamConfiguration,
            VideoPacketFlags.CodecConfiguration,
            payload,
            0,
            0,
            token);
    }

    private static MediaCodecProfileLevel GetEncoderLevel() =>
        GetEncoderLevel(_streamHeight, _streamFps);

    private static MediaCodecProfileLevel GetEncoderLevel(
        int height,
        int frameRate)
    {
        if (frameRate >= 120)
        {
            if (height >= UltraHdHeight)
                return OperatingSystem.IsAndroidVersionAtLeast(29)
                    ? MediaCodecProfileLevel.Avclevel61
                    : MediaCodecProfileLevel.Avclevel52;
            if (height >= QuadHdHeight)
                return MediaCodecProfileLevel.Avclevel52;
            return height <= HdHeight
                ? MediaCodecProfileLevel.Avclevel42
                : MediaCodecProfileLevel.Avclevel51;
        }
        if (height >= UltraHdHeight)
            return frameRate >= PreferredFps
                ? MediaCodecProfileLevel.Avclevel52
                : MediaCodecProfileLevel.Avclevel51;
        if (height >= QuadHdHeight)
            return frameRate >= PreferredFps
                ? MediaCodecProfileLevel.Avclevel51
                : MediaCodecProfileLevel.Avclevel5;
        if (height <= HdHeight && frameRate >= PreferredFps)
            return MediaCodecProfileLevel.Avclevel4;
        if (frameRate >= PreferredFps)
            return MediaCodecProfileLevel.Avclevel42;
        return OperatingSystem.IsAndroidVersionAtLeast(28)
            ? MediaCodecProfileLevel.Avclevel41
            : MediaCodecProfileLevel.Avclevel4;
    }

    private static byte GetProtocolLevel()
    {
        if (_streamFps >= 120)
        {
            if (_streamHeight >= UltraHdHeight)
                return 61;
            if (_streamHeight >= QuadHdHeight)
                return 52;
            return _streamHeight <= HdHeight ? (byte)42 : (byte)51;
        }
        if (_streamHeight >= UltraHdHeight)
            return _streamFps >= PreferredFps ? (byte)52 : (byte)51;
        if (_streamHeight >= QuadHdHeight)
            return _streamFps >= PreferredFps ? (byte)51 : (byte)50;
        if (_streamHeight <= HdHeight && _streamFps >= PreferredFps)
            return 40;
        if (_streamFps >= PreferredFps)
            return 42;
        return OperatingSystem.IsAndroidVersionAtLeast(28) ? (byte)41 : (byte)40;
    }

    private static async Task SendPacketAsync(
        VideoPacketType type,
        VideoPacketFlags flags,
        byte[] payload,
        long presentationTimeUs,
        uint durationUs,
        CancellationToken token)
    {
        var output = _outputStream ?? throw new IOException("Receiver connection is closed.");
        var header = new byte[VideoProtocol.PacketHeaderSize];
        var packet = new VideoPacketHeader(
            type,
            flags,
            payload.Length,
            _sequence++,
            presentationTimeUs,
            durationUs);
        if (!VideoProtocol.TryWritePacketHeader(header, packet))
            throw new InvalidOperationException("Could not serialize H.264 packet header.");

        await WriteGate.WaitAsync(token);
        try
        {
            await output.WriteAsync(header, token);
            await output.WriteAsync(payload, token);
        }
        finally
        {
            WriteGate.Release();
        }
    }

    private sealed class DeviceStateCallback(TaskCompletionSource<CameraDevice> completion)
        : CameraDevice.StateCallback
    {
        public override void OnOpened(CameraDevice camera) => completion.TrySetResult(camera);

        public override void OnDisconnected(CameraDevice camera)
        {
            InvalidateCamera(camera, "Camera disconnected.");
            completion.TrySetException(new IOException("Camera disconnected."));
        }

        public override void OnError(CameraDevice camera, CameraError error)
        {
            InvalidateCamera(camera, $"Camera2 error: {error}.");
            completion.TrySetException(new IOException($"Camera2 open failed: {error}."));
        }
    }

    private static void InvalidateCamera(CameraDevice camera, string error)
    {
        try { camera.Close(); } catch { }
        if (!ReferenceEquals(
                Interlocked.CompareExchange(ref _camera, null, camera),
                camera))
            return;

        try { _captureSession?.Close(); } catch { }
        try { _captureSession?.Dispose(); } catch { }
        _captureSession = null;
        LastError = error;
        _streamCancellation?.Cancel();
    }

    private sealed class SessionStateCallback(
        TaskCompletionSource<CameraCaptureSession> completion,
        TaskCompletionSource closed)
        : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) =>
            completion.TrySetResult(session);

        public override void OnConfigureFailed(CameraCaptureSession session) =>
            completion.TrySetException(new IOException("Camera2 could not configure the preview/encoder surfaces."));

        public override void OnClosed(CameraCaptureSession session) =>
            closed.TrySetResult();
    }
}
#endif
