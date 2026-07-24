using System.Net;
using System.Net.Sockets;

namespace BobrCam;

public static class VideoProtocol
{
    public const int Port = 28444;
    public const int DiscoveryPort = 28445;
    public const int MaxFrameBytes = 8 * 1024 * 1024;
}

public static class NetworkAddress
{
    public static string GetLocalIPv4Address() => Dns.GetHostEntry(Dns.GetHostName()).AddressList
        .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))?.ToString() ?? "No Wi-Fi address found";
}
