using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BobrCam;

public sealed class VideoReceiver
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PacketTimeout = TimeSpan.FromSeconds(10);
    private readonly ConnectionRateLimiter _rateLimiter = new();
    private readonly SemaphoreSlim _connectionSlots = new(4, 4);
    private TcpListener? _wifiListener;
    private TcpListener? _usbListener;
    private CancellationTokenSource? _cancellation;
    private X509Certificate2? _identity;
    private ReceiverAdvertiser? _advertiser;
    private Task? _advertiserTask;
    private readonly object _activeConnectionGate = new();
    private CancellationTokenSource? _activeConnectionCancellation;
    private TcpClient? _activeClient;
    private Stream? _activeTransport;
    private readonly SemaphoreSlim _controlWriteGate = new(1, 1);
    private byte[]? _activeSessionToken;
    private bool _activeConnectionIsUsb;
    private long _activeConnectionGeneration;
    public event Action<H264StreamConfiguration, byte[]>? StreamConfigured;
    public event Action<EncodedVideoAccessUnit>? AccessUnitReceived;
    public event Action<CameraCapabilities>? CameraCapabilitiesReceived;
    public event Action<string>? StatusChanged;
    public byte RequestedFrameRate { get; set; } = 60;
    public ushort RequestedWidth { get; set; } = 1920;
    public ushort RequestedHeight { get; set; } = 1080;
    public bool PrioritizeResolution { get; set; }
    public bool UseFrontCamera { get; private set; }
    public Task StartAsync(
        string bindAddress,
        int port,
        int usbHostPort = VideoProtocol.UsbHostPort)
    {
        if (_cancellation is not null) return Task.CompletedTask;
        var wifiAddress = string.IsNullOrWhiteSpace(bindAddress)
            ? IPAddress.Any
            : IPAddress.Parse(bindAddress);
        if (wifiAddress.Equals(IPAddress.Any))
        {
            throw new InvalidOperationException(
                "Choose this PC's private IPv4 address. Listening on every network interface is disabled.");
        }
        if (!IPAddress.IsLoopback(wifiAddress) &&
            !NetworkAddress.IsPrivateIPv4Address(wifiAddress))
        {
            throw new InvalidOperationException(
                "BobrCam Wi-Fi can listen only on a private local IPv4 address.");
        }
        if (usbHostPort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(usbHostPort));
        if (port == usbHostPort &&
            (wifiAddress.Equals(IPAddress.Any) || IPAddress.IsLoopback(wifiAddress)))
        {
            throw new InvalidOperationException(
                "Wi-Fi and local USB listeners must use separate endpoints.");
        }

        _wifiListener = new TcpListener(wifiAddress, port);
        _usbListener = new TcpListener(IPAddress.Loopback, usbHostPort);
        _identity = SecureIdentity.GetOrCreate("bobrcam-receiver.pfx", "BobrCam Windows");
        _cancellation = new CancellationTokenSource();
        _wifiListener.Start(8);
        try
        {
            _usbListener.Start(2);
        }
        catch
        {
            _wifiListener.Stop();
            _wifiListener = null;
            _usbListener = null;
            _cancellation.Dispose();
            _cancellation = null;
            throw;
        }
        _advertiser = new ReceiverAdvertiser(SecureIdentity.Fingerprint(_identity), port);
        _advertiserTask = _advertiser.Start(_cancellation.Token);
        _ = AcceptLoopAsync(_wifiListener, isUsb: false, _cancellation.Token);
        _ = AcceptLoopAsync(_usbListener, isUsb: true, _cancellation.Token);
        StatusChanged?.Invoke("Waiting for a phone — automatic connection enabled.");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        _cancellation?.Cancel();
        _wifiListener?.Stop();
        _usbListener?.Stop();
        if (_advertiserTask is not null)
        {
            try { await _advertiserTask; }
            catch (OperationCanceledException) { }
        }
        _advertiserTask = null;
        _advertiser = null;
        lock (_activeConnectionGate)
        {
            _activeConnectionCancellation?.Cancel();
            _activeClient?.Dispose();
            _activeConnectionCancellation?.Dispose();
            _activeConnectionCancellation = null;
            _activeClient = null;
            _activeTransport = null;
            _activeSessionToken = null;
            _activeConnectionIsUsb = false;
        }
        _cancellation?.Dispose();
        _cancellation = null;
        _wifiListener = null;
        _usbListener = null;
    }

    public async Task SendCameraControlAsync(
        CameraControlCommand command,
        int value = 0,
        CancellationToken token = default)
    {
        Stream transport;
        long generation;
        lock (_activeConnectionGate)
        {
            transport = _activeTransport ??
                throw new InvalidOperationException("Connect a phone first.");
            generation = _activeConnectionGeneration;
        }

        var message = new byte[VideoProtocol.CameraControlSize];
        if (!VideoProtocol.TryWriteCameraControl(
                message,
                new CameraControlMessage(command, value)))
            throw new ArgumentOutOfRangeException(nameof(command));
        await _controlWriteGate.WaitAsync(token);
        try
        {
            await transport.WriteAsync(message, token);
            await transport.FlushAsync(token);
        }
        catch (Exception ex) when (
            ex is ObjectDisposedException or IOException &&
            !IsActiveConnection(generation))
        {
        }
        finally
        {
            _controlWriteGate.Release();
        }
    }

    public void TogglePhoneCamera()
    {
        lock (_activeConnectionGate)
        {
            UseFrontCamera = !UseFrontCamera;
            _activeConnectionCancellation?.Cancel();
            _activeClient?.Dispose();
        }
        StatusChanged?.Invoke(
            UseFrontCamera
                ? "Switching to front camera…"
                : "Switching to rear camera…");
    }

    private async Task AcceptLoopAsync(
        TcpListener listener,
        bool isUsb,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token);
                _ = HandleConnectionAsync(client, isUsb, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                StatusChanged?.Invoke($"Receiver error ({ex.Message}) — still listening…");
            }
        }
    }

    private async Task HandleConnectionAsync(
        TcpClient client,
        bool isUsb,
        CancellationToken receiverToken)
    {
        using (client)
        {
            var remoteAddress =
                (client.Client.RemoteEndPoint as IPEndPoint)?.Address ??
                IPAddress.None;
            var rateLimitKey = isUsb ? "local-usb" : remoteAddress.ToString();
            if (!_rateLimiter.CanAttempt(rateLimitKey) ||
                !await _connectionSlots.WaitAsync(0, receiverToken))
            {
                return;
            }

            using var handshakeTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(receiverToken);
            client.NoDelay = true;
            client.ReceiveBufferSize = 2 * 1024 * 1024;
            client.SendBufferSize = 64 * 1024;
            var network = client.GetStream();
            using var secure = isUsb ? null : new SslStream(network, false);
            Stream transport = (Stream?)secure ?? network;
            long generation = 0;
            CancellationTokenSource? connectionCancellation = null;
            try
            {
                handshakeTimeout.CancelAfter(HandshakeTimeout);
                if (secure is not null)
                {
                    await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _identity,
                        ClientCertificateRequired = false,
                        EnabledSslProtocols = SslProtocols.Tls12,
                        CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                    }, handshakeTimeout.Token);
                }
                var sessionToken =
                    await ValidatePhoneHandshakeAsync(
                        transport,
                        handshakeTimeout.Token);
                await SendStreamRequestAsync(
                    transport,
                    RequestedFrameRate,
                    RequestedWidth,
                    RequestedHeight,
                    PrioritizeResolution,
                    UseFrontCamera,
                    handshakeTimeout.Token);
                _rateLimiter.RecordSuccess(rateLimitKey);

                connectionCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(receiverToken);
                lock (_activeConnectionGate)
                {
                    if (_activeClient is not null &&
                        _activeSessionToken is not null &&
                        !CryptographicOperations.FixedTimeEquals(
                            sessionToken,
                            _activeSessionToken))
                    {
                        return;
                    }

                    if (_activeClient is not null &&
                        _activeConnectionIsUsb &&
                        !isUsb)
                    {
                        return;
                    }

                    _activeConnectionCancellation?.Cancel();
                    _activeClient?.Dispose();
                    _activeConnectionCancellation?.Dispose();
                    _activeConnectionCancellation = connectionCancellation;
                    _activeClient = client;
                    _activeTransport = transport;
                    _activeSessionToken = sessionToken;
                    _activeConnectionIsUsb = isUsb;
                    generation = ++_activeConnectionGeneration;
                }

                StatusChanged?.Invoke(
                    isUsb
                        ? "Phone connected by USB — H.264 stream active."
                        : "Phone connected by Wi-Fi — encrypted H.264 stream active.");
                await ReadPacketsAsync(transport, connectionCancellation.Token);
            }
            catch (OperationCanceledException) when (
                receiverToken.IsCancellationRequested ||
                connectionCancellation?.IsCancellationRequested == true)
            {
            }
            catch (Exception ex) when (!receiverToken.IsCancellationRequested)
            {
                if (generation == 0)
                    _rateLimiter.RecordFailure(rateLimitKey);
                if (generation == 0 || IsActiveConnection(generation))
                    StatusChanged?.Invoke(
                        $"Phone disconnected ({ex.Message}) — waiting to reconnect…");
            }
            finally
            {
                if (generation != 0)
                {
                    lock (_activeConnectionGate)
                    {
                        if (_activeConnectionGeneration == generation)
                        {
                            _activeConnectionCancellation = null;
                            _activeClient = null;
                            _activeTransport = null;
                            _activeSessionToken = null;
                            _activeConnectionIsUsb = false;
                            StatusChanged?.Invoke("Phone disconnected — waiting to reconnect…");
                        }
                    }
                }
                connectionCancellation?.Dispose();
                _connectionSlots.Release();
            }
        }
    }

    private bool IsActiveConnection(long generation)
    {
        lock (_activeConnectionGate)
            return _activeConnectionGeneration == generation;
    }

    private async Task<byte[]> ValidatePhoneHandshakeAsync(
        Stream stream,
        CancellationToken token)
    {
        var nonce = RandomNumberGenerator.GetBytes(32);
        var challenge = new byte[VideoProtocol.AuthenticationChallengeSize];
        VideoProtocol.WriteAuthenticationChallenge(challenge, nonce);
        await stream.WriteAsync(challenge, token);
        await stream.FlushAsync(token);

        var response = new byte[VideoProtocol.AuthenticationResponseSize];
        await ReadExactlyWithTimeoutAsync(
            stream,
            response,
            HandshakeTimeout,
            token);
        var sessionToken = new byte[32];
        if (!VideoProtocol.TryReadAuthenticationResponse(
                response,
                nonce,
                sessionToken) ||
            sessionToken.AsSpan().IndexOfAnyExcept((byte)0) < 0)
        {
            throw new AuthenticationException("Invalid phone authentication.");
        }

        return sessionToken;
    }

    private static async Task SendStreamRequestAsync(
        Stream stream,
        byte frameRate,
        ushort width,
        ushort height,
        bool prioritizeResolution,
        bool useFrontCamera,
        CancellationToken token)
    {
        var message = new byte[VideoProtocol.StreamRequestSize];
        if (!VideoProtocol.TryWriteStreamRequest(
                message,
                frameRate,
                width,
                height,
                prioritizeResolution,
                useFrontCamera))
            throw new InvalidOperationException("Invalid requested stream mode.");
        await stream.WriteAsync(message, token);
        await stream.FlushAsync(token);
    }

    private async Task ReadPacketsAsync(Stream stream, CancellationToken token)
    {
        var headerBytes = new byte[VideoProtocol.PacketHeaderSize];
        while (!token.IsCancellationRequested)
        {
            await ReadExactlyWithTimeoutAsync(
                stream,
                headerBytes,
                PacketTimeout,
                token);
            if (!VideoProtocol.TryReadPacketHeader(headerBytes, out var header))
                throw new InvalidDataException("Invalid BobrCam H.264 packet header.");

            var payload = GC.AllocateUninitializedArray<byte>(header.PayloadLength);
            if (payload.Length > 0)
            {
                await ReadExactlyWithTimeoutAsync(
                    stream,
                    payload,
                    PacketTimeout,
                    token);
            }

            switch (header.Type)
            {
                case VideoPacketType.StreamConfiguration:
                    if (payload.Length < VideoProtocol.StreamConfigurationSize ||
                        !VideoProtocol.TryReadStreamConfiguration(payload, out var configuration))
                    {
                        throw new InvalidDataException("Invalid H.264 stream configuration.");
                    }

                    StreamConfigured?.Invoke(
                        configuration,
                        payload.AsSpan(VideoProtocol.StreamConfigurationSize).ToArray());
                    break;

                case VideoPacketType.AccessUnit:
                    AccessUnitReceived?.Invoke(new EncodedVideoAccessUnit(
                        payload,
                        header.Sequence,
                        header.PresentationTimeMicroseconds,
                        header.DurationMicroseconds,
                        header.Flags.HasFlag(VideoPacketFlags.KeyFrame),
                        header.Flags.HasFlag(VideoPacketFlags.Discontinuity)));
                    break;

                case VideoPacketType.EndOfStream:
                    return;

                case VideoPacketType.CameraCapabilities:
                    if (!VideoProtocol.TryReadCameraCapabilities(
                            payload,
                            out var capabilities))
                    {
                        throw new InvalidDataException(
                            "Invalid camera capabilities.");
                    }
                    CameraCapabilitiesReceived?.Invoke(capabilities);
                    break;
            }
        }
    }

    private static async Task ReadExactlyWithTimeoutAsync(
        Stream stream,
        Memory<byte> destination,
        TimeSpan timeout,
        CancellationToken token)
    {
        using var timeoutCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCancellation.CancelAfter(timeout);
        try
        {
            await stream.ReadExactlyAsync(destination, timeoutCancellation.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("The phone stopped sending data.");
        }
    }

    private sealed class ConnectionRateLimiter
    {
        private const int MaximumFailures = 5;
        private static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(1);
        private static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(2);
        private readonly object _gate = new();
        private readonly Dictionary<string, FailureState> _failures =
            new(StringComparer.Ordinal);

        public bool CanAttempt(string key)
        {
            lock (_gate)
            {
                if (!_failures.TryGetValue(key, out var state))
                    return true;
                if (state.BlockedUntilUtc > DateTime.UtcNow)
                    return false;
                if (DateTime.UtcNow - state.WindowStartedUtc > FailureWindow)
                    _failures.Remove(key);
                return true;
            }
        }

        public void RecordSuccess(string key)
        {
            lock (_gate)
                _failures.Remove(key);
        }

        public void RecordFailure(string key)
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                if (!_failures.TryGetValue(key, out var state) ||
                    now - state.WindowStartedUtc > FailureWindow)
                {
                    if (_failures.Count >= 256)
                    {
                        var oldest = _failures.MinBy(item =>
                            item.Value.WindowStartedUtc).Key;
                        _failures.Remove(oldest);
                    }
                    _failures[key] = new FailureState(now, 1, DateTime.MinValue);
                    return;
                }

                var failures = state.Failures + 1;
                _failures[key] = new FailureState(
                    state.WindowStartedUtc,
                    failures,
                    failures >= MaximumFailures
                        ? now.Add(BlockDuration)
                        : DateTime.MinValue);
            }
        }

        private readonly record struct FailureState(
            DateTime WindowStartedUtc,
            int Failures,
            DateTime BlockedUntilUtc);
    }
}
