using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace BobrCam;

public sealed class VideoReceiver
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private X509Certificate2? _identity;
    private ReceiverAdvertiser? _advertiser;
    public event Action<byte[]>? FrameReceived;
    public event Action<string>? StatusChanged;

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
        StatusChanged?.Invoke(Preferences.Default.ContainsKey("paired_phone") ? "Waiting for paired phone…" : "Waiting to pair the first phone…");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _cancellation?.Cancel(); _listener?.Stop(); _cancellation?.Dispose(); _cancellation = null; _listener = null;
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener!.AcceptTcpClientAsync(token);
                using var secure = new SslStream(client.GetStream(), false);
                using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _identity,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12,
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck
                }, handshakeTimeout.Token);
                await AuthenticatePhoneAsync(secure, handshakeTimeout.Token);
                StatusChanged?.Invoke("Phone connected — encrypted stream active.");
                await ReadFramesAsync(secure, token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (!token.IsCancellationRequested) { StatusChanged?.Invoke($"Connection rejected: {ex.Message}"); }
        }
    }

    private static async Task AuthenticatePhoneAsync(Stream stream, CancellationToken token)
    {
        var message = new byte[36];
        await stream.ReadExactlyAsync(message, token);
        if (!message.AsSpan(0, 4).SequenceEqual("PCA1"u8)) throw new AuthenticationException("Invalid phone authentication.");
        var supplied = message.AsSpan(4, 32).ToArray();
        var savedText = Preferences.Default.Get("paired_phone_token", string.Empty);
        if (string.IsNullOrEmpty(savedText)) { Preferences.Default.Set("paired_phone_token", Convert.ToBase64String(supplied)); return; }
        if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(savedText), supplied))
            throw new AuthenticationException("This Windows receiver is paired with another phone.");
    }

    private async Task ReadFramesAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        while (!token.IsCancellationRequested)
        {
            await stream.ReadExactlyAsync(header, token);
            var length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length <= 0 || length > VideoProtocol.MaxFrameBytes) throw new InvalidDataException("Invalid camera frame.");
            var jpeg = new byte[length];
            await stream.ReadExactlyAsync(jpeg, token);
            FrameReceived?.Invoke(jpeg);
        }
    }
}
