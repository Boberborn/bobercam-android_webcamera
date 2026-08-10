using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BobrCam;

public sealed record ReceiverEndpoint(string Host, int Port, string CertificateFingerprint);

public static class ReceiverDiscovery
{
    public static async Task<ReceiverEndpoint> FindAsync(TimeSpan timeout)
    {
        if (DeviceInfo.Current.DeviceType == DeviceType.Virtual)
            return new ReceiverEndpoint("127.0.0.1", VideoProtocol.Port, string.Empty);

        using var cancellation = new CancellationTokenSource(timeout);
        using var udp = new UdpClient(VideoProtocol.DiscoveryPort) { EnableBroadcast = true };
        try
        {
            while (true)
            {
                var packet = await udp.ReceiveAsync(cancellation.Token);
                var parts = Encoding.UTF8.GetString(packet.Buffer).Split('|');
                if (parts.Length == 3 && parts[0] == "PHONECAM/1" && int.TryParse(parts[1], out var port))
                    return new ReceiverEndpoint(packet.RemoteEndPoint.Address.ToString(), port, parts[2]);
            }
        }
        catch (OperationCanceledException) { throw new TimeoutException("Windows receiver not found. Check Wi-Fi and firewall."); }
    }

}

public sealed class ReceiverAdvertiser
{
    private readonly byte[] _message;
    public ReceiverAdvertiser(string fingerprint, int port) => _message = Encoding.UTF8.GetBytes($"PHONECAM/1|{port}|{fingerprint}");
    public Task Start(CancellationToken token) => RunAsync(token);
    private async Task RunAsync(CancellationToken token)
    {
        using var udp = new UdpClient { EnableBroadcast = true };
        while (!token.IsCancellationRequested)
        {
            try
            {
                await udp.SendAsync(_message, new IPEndPoint(IPAddress.Broadcast, VideoProtocol.DiscoveryPort), token);
                await Task.Delay(1000, token);
            }
            catch (OperationCanceledException) { }
            catch { await Task.Delay(2000, token); }
        }
    }
}
