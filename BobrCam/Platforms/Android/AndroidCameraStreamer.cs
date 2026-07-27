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
using Android.Media;
using Android.OS;
using Android.Views;
using Java.Nio;

namespace BobrCam;

public static class AndroidCameraStreamer
{
    private const int TargetWidth = 1920;
    private const int TargetHeight = 1080;
    private const int PreferredFps = 60;
    private const int FallbackFps = 30;
    private const int TargetBitrate = 16_000_000;

    private static readonly SemaphoreSlim StateGate = new(1, 1);
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    private static CameraDevice? _camera;
    private static CameraCaptureSession? _captureSession;
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
    private static bool _wantPreview;
    private static int _displayOrientation;
    private static int _streamFps = FallbackFps;
    private static Android.Util.Range? _targetFpsRange;
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

        surface.SetDefaultBufferSize(TargetWidth, TargetHeight);
        _previewOutput?.Dispose();
        _previewOutput = new Surface(surface);
        onReady?.Invoke(_displayOrientation, TargetWidth, TargetHeight);
        if (_wantPreview)
            _ = ReconfigureCameraAsync();
    }

    public static void ApplyDisplayOrientation(int degrees) =>
        _displayOrientation = (degrees + 90) % 360;

    public static void GetRotatedAspect(out int width, out int height)
    {
        if (_displayOrientation is 90 or 270)
        {
            width = TargetHeight;
            height = TargetWidth;
        }
        else
        {
            width = TargetWidth;
            height = TargetHeight;
        }
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
            var secure = new SslStream(_client.GetStream(), false, (_, certificate, _, _) =>
            {
                if (certificate is null) return false;
                presentedFingerprint = SecureIdentity.Fingerprint(new X509Certificate2(certificate));
                var paired = Preferences.Default.Get("paired_receiver", string.Empty);
                if (!string.IsNullOrEmpty(discoveredFingerprint) &&
                    !SecureIdentity.FixedTimeEquals(discoveredFingerprint, presentedFingerprint))
                    return false;
                return string.IsNullOrEmpty(paired) ||
                       SecureIdentity.FixedTimeEquals(paired, presentedFingerprint);
            });
            await secure.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "bobrcam.local",
                EnabledSslProtocols = SslProtocols.Tls12,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, _streamCancellation.Token);

            Preferences.Default.Set("paired_receiver", presentedFingerprint);
            _outputStream = secure;
        }
        var authentication = new byte[36];
        "PCA1"u8.CopyTo(authentication);
        SecureIdentity.GetOrCreatePairingToken().CopyTo(authentication, 4);
        await _outputStream.WriteAsync(authentication, _streamCancellation.Token);
        await _outputStream.FlushAsync(_streamCancellation.Token);

        await StateGate.WaitAsync(_streamCancellation.Token);
        try
        {
            StopEncoder();
            await EnsureCameraOpenAsync();
            StartEncoder();
            await CreateCaptureSessionAsync(streaming: true);
            _drainTask = Task.Run(() => DrainEncoderAsync(_streamCancellation.Token));
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
            StopEncoder();
            _outputStream?.Dispose();
            _outputStream = null;
            _client?.Dispose();
            _client = null;
            _drainTask = null;
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
                (Java.Lang.Integer?)manager.GetCameraCharacteristics(id)
                    .Get(CameraCharacteristics.LensFacing) ==
                new Java.Lang.Integer((int)LensFacing.Back))
            ?? manager.GetCameraIdList().First();

        var completion = new TaskCompletionSource<CameraDevice>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        manager.OpenCamera(cameraId, new DeviceStateCallback(completion), _cameraHandler);
        _camera = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _targetFpsRange = DetermineFrameRate(manager.GetCameraCharacteristics(cameraId));
        _streamFps = _targetFpsRange is null
            ? FallbackFps
            : Convert.ToInt32(_targetFpsRange.Upper);
    }

    private static Android.Util.Range? DetermineFrameRate(CameraCharacteristics characteristics)
    {
        var ranges = (Android.Util.Range[]?)characteristics.Get(
            CameraCharacteristics.ControlAeAvailableTargetFpsRanges);
        if (ranges is null || ranges.Length == 0) return null;
        return ranges
            .Where(range => Convert.ToInt32(range.Upper) <= PreferredFps)
            .OrderByDescending(range => Convert.ToInt32(range.Upper))
            .ThenByDescending(range => Convert.ToInt32(range.Lower))
            .FirstOrDefault()
            ?? ranges.OrderByDescending(range => Convert.ToInt32(range.Upper)).First();
    }

    private static void StartEncoder()
    {
        var format = MediaFormat.CreateVideoFormat(MediaFormat.MimetypeVideoAvc, TargetWidth, TargetHeight);
        format.SetInteger(MediaFormat.KeyColorFormat, (int)MediaCodecCapabilities.Formatsurface);
        format.SetInteger(MediaFormat.KeyBitRate, TargetBitrate);
        format.SetInteger(MediaFormat.KeyFrameRate, _streamFps);
        format.SetInteger(MediaFormat.KeyIFrameInterval, 1);
        format.SetInteger(MediaFormat.KeyProfile, (int)MediaCodecProfileType.Avcprofilemain);
        format.SetInteger(MediaFormat.KeyLevel,
            (int)(_streamFps >= PreferredFps ? MediaCodecProfileLevel.Avclevel42 : MediaCodecProfileLevel.Avclevel41));
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
        if (_camera is null || _previewOutput is null) return;
        _captureSession?.Close();
        _captureSession?.Dispose();
        _captureSession = null;

        var outputs = new List<Surface> { _previewOutput };
        if (streaming && _encoderSurface is not null)
            outputs.Add(_encoderSurface);

        var completion = new TaskCompletionSource<CameraCaptureSession>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _camera.CreateCaptureSession(outputs, new SessionStateCallback(completion), _cameraHandler);
        _captureSession = await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using var request = _camera.CreateCaptureRequest(
            streaming ? CameraTemplate.Record : CameraTemplate.Preview);
        foreach (var output in outputs)
            request.AddTarget(output);
        request.Set(CaptureRequest.ControlMode, new Java.Lang.Integer((int)ControlMode.Auto));
        if (_targetFpsRange is not null)
            request.Set(CaptureRequest.ControlAeTargetFpsRange, _targetFpsRange);
        _captureSession.SetRepeatingRequest(request.Build(), null, _cameraHandler);
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
            TargetWidth,
            TargetHeight,
            (ushort)_streamFps,
            1,
            TargetBitrate,
            H264Profile.Main,
            (byte)(_streamFps >= PreferredFps ? 42 : 41),
            8,
            H264ChromaFormat.Yuv420,
            1000,
            (ushort)_displayOrientation,
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

    private sealed class SessionStateCallback(TaskCompletionSource<CameraCaptureSession> completion)
        : CameraCaptureSession.StateCallback
    {
        public override void OnConfigured(CameraCaptureSession session) =>
            completion.TrySetResult(session);

        public override void OnConfigureFailed(CameraCaptureSession session) =>
            completion.TrySetException(new IOException("Camera2 could not configure the preview/encoder surfaces."));
    }
}
#endif
