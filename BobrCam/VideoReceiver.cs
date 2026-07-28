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
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private X509Certificate2? _identity;
    private ReceiverAdvertiser? _advertiser;
    private readonly object _activeConnectionGate = new();
    private CancellationTokenSource? _activeConnectionCancellation;
    private TcpClient? _activeClient;
    private byte[]? _activePairingToken;
    private byte[]? _supersededPairingToken;
    private long _activeConnectionGeneration;
    public event Action<H264StreamConfiguration, byte[]>? StreamConfigured;
    public event Action<EncodedVideoAccessUnit>? AccessUnitReceived;
    public event Action<string>? StatusChanged;
    public byte RequestedFrameRate { get; set; } = 60;
    public ushort RequestedWidth { get; set; } = 1920;
    public ushort RequestedHeight { get; set; } = 1080;
    public bool PrioritizeResolution { get; set; }

    public Task StartAsync(string bindAddress, int port)
    {
        if (_cancellation is not null) return Task.CompletedTask;
        if (!string.IsNullOrWhiteSpace(bindAddress) && bindAddress != "0.0.0.0")
            _ = IPAddress.Parse(bindAddress);
        // Listen on every local interface so the same port accepts both
        // Wi-Fi traffic and ADB reverse traffic arriving through localhost.
        _listener = new TcpListener(IPAddress.Any, port);
        _identity = SecureIdentity.GetOrCreate("bobrcam-receiver.pfx", "BobrCam Windows");
        _cancellation = new CancellationTokenSource();
        _listener.Start();
        _advertiser = new ReceiverAdvertiser(SecureIdentity.Fingerprint(_identity), port);
        _advertiser.Start(_cancellation.Token);
        _ = AcceptLoopAsync(_cancellation.Token);
        StatusChanged?.Invoke("Waiting for a phone…");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cancellation?.Cancel();
        _listener?.Stop();
        lock (_activeConnectionGate)
        {
            _activeConnectionCancellation?.Cancel();
            _activeClient?.Dispose();
            _activeConnectionCancellation?.Dispose();
            _activeConnectionCancellation = null;
            _activeClient = null;
            _activePairingToken = null;
            _supersededPairingToken = null;
        }
        _cancellation?.Dispose();
        _cancellation = null;
        _listener = null;
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(token);
                _ = HandleConnectionAsync(client, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                StatusChanged?.Invoke($"Receiver error ({ex.Message}) — still listening…");
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken receiverToken)
    {
        using (client)
        using (var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(receiverToken))
        {
            var network = client.GetStream();
            var isUsb = client.Client.RemoteEndPoint is IPEndPoint endpoint &&
                IPAddress.IsLoopback(endpoint.Address);
            using var secure = isUsb ? null : new SslStream(network, false);
            Stream transport = (Stream?)secure ?? network;
            long generation = 0;
            CancellationTokenSource? connectionCancellation = null;
            try
            {
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
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
                var pairingToken =
                    await ValidatePhoneHandshakeAsync(transport, handshakeTimeout.Token);
                await SendStreamRequestAsync(
                    transport,
                    RequestedFrameRate,
                    RequestedWidth,
                    RequestedHeight,
                    PrioritizeResolution,
                    handshakeTimeout.Token);

                connectionCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(receiverToken);
                lock (_activeConnectionGate)
                {
                    if (_activeClient is not null &&
                        _supersededPairingToken is not null &&
                        CryptographicOperations.FixedTimeEquals(
                            pairingToken,
                            _supersededPairingToken))
                    {
                        return;
                    }

                    if (_activeClient is not null &&
                        _activePairingToken is not null &&
                        !CryptographicOperations.FixedTimeEquals(
                            pairingToken,
                            _activePairingToken))
                    {
                        _supersededPairingToken = _activePairingToken;
                    }

                    _activeConnectionCancellation?.Cancel();
                    _activeClient?.Dispose();
                    _activeConnectionCancellation?.Dispose();
                    _activeConnectionCancellation = connectionCancellation;
                    _activeClient = client;
                    _activePairingToken = pairingToken;
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
                            _activePairingToken = null;
                            _supersededPairingToken = null;
                            StatusChanged?.Invoke("Phone disconnected — waiting to reconnect…");
                        }
                    }
                }
                connectionCancellation?.Dispose();
            }
        }
    }

    private bool IsActiveConnection(long generation)
    {
        lock (_activeConnectionGate)
            return _activeConnectionGeneration == generation;
    }

    private static async Task<byte[]> ValidatePhoneHandshakeAsync(
        Stream stream,
        CancellationToken token)
    {
        var message = new byte[36];
        await stream.ReadExactlyAsync(message, token);
        if (!message.AsSpan(0, 4).SequenceEqual("PCA1"u8)) throw new AuthenticationException("Invalid phone authentication.");
        if (message.AsSpan(4, 32).IndexOfAnyExcept((byte)0) < 0)
            throw new AuthenticationException("Invalid phone authentication token.");
        return message[4..];
    }

    private static async Task SendStreamRequestAsync(
        Stream stream,
        byte frameRate,
        ushort width,
        ushort height,
        bool prioritizeResolution,
        CancellationToken token)
    {
        var message = new byte[VideoProtocol.StreamRequestSize];
        if (!VideoProtocol.TryWriteStreamRequest(
                message,
                frameRate,
                width,
                height,
                prioritizeResolution))
            throw new InvalidOperationException("Invalid requested stream mode.");
        await stream.WriteAsync(message, token);
        await stream.FlushAsync(token);
    }

    private async Task ReadPacketsAsync(Stream stream, CancellationToken token)
    {
        var headerBytes = new byte[VideoProtocol.PacketHeaderSize];
        while (!token.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(headerBytes, token);
            if (!VideoProtocol.TryReadPacketHeader(headerBytes, out var header))
                throw new InvalidDataException("Invalid BobrCam H.264 packet header.");

            var payload = GC.AllocateUninitializedArray<byte>(header.PayloadLength);
            if (payload.Length > 0)
                await stream.ReadExactlyAsync(payload, token);

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
            }
        }
    }
}
