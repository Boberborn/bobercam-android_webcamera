#if ANDROID
using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Android.Graphics;
using Android.Hardware;
using LegacyCamera = Android.Hardware.Camera;

namespace BobrCam;

public static class AndroidCameraStreamer
{
    private static LegacyCamera? _camera;
    private static TcpClient? _client;
    private static SslStream? _secureStream;
    private static SurfaceTexture? _surfaceTexture;

    public static async Task StartAsync(string host, int port, string discoveredFingerprint)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(host, port);
        string presentedFingerprint = string.Empty;
        _secureStream = new SslStream(_client.GetStream(), false, (_, certificate, _, _) =>
        {
            if (certificate is null) return false;
            presentedFingerprint = SecureIdentity.Fingerprint(new X509Certificate2(certificate));
            var paired = Preferences.Default.Get("paired_receiver", string.Empty);
            if (!string.IsNullOrEmpty(discoveredFingerprint) && !SecureIdentity.FixedTimeEquals(discoveredFingerprint, presentedFingerprint)) return false;
            return string.IsNullOrEmpty(paired) || SecureIdentity.FixedTimeEquals(paired, presentedFingerprint);
        });
        await _secureStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "bobrcam.local",
            EnabledSslProtocols = SslProtocols.Tls12,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck
        });
        Preferences.Default.Set("paired_receiver", presentedFingerprint);
        var authentication = new byte[36];
        "PCA1"u8.CopyTo(authentication);
        SecureIdentity.GetOrCreatePairingToken().CopyTo(authentication, 4);
        await _secureStream.WriteAsync(authentication);
        await _secureStream.FlushAsync();
        _camera = LegacyCamera.Open(0);
        var parameters = _camera.GetParameters()!;
        parameters.SetPreviewSize(640, 480);
        _camera.SetParameters(parameters);
        _surfaceTexture = new SurfaceTexture(10);
        _camera.SetPreviewTexture(_surfaceTexture);
        _camera.SetPreviewCallback(new FrameCallback(_secureStream));
        _camera.StartPreview();
    }

    private sealed class FrameCallback : Java.Lang.Object, LegacyCamera.IPreviewCallback
    {
        private readonly Stream _output;
        public FrameCallback(Stream output) => _output = output;
        public void OnPreviewFrame(byte[]? data, LegacyCamera? camera)
        {
            if (data is null || camera is null) return;
            try
            {
                var size = camera.GetParameters()!.PreviewSize!;
                using var image = new YuvImage(data, ImageFormatType.Nv21, size.Width, size.Height, null);
                using var jpeg = new MemoryStream();
                image.CompressToJpeg(new Android.Graphics.Rect(0, 0, size.Width, size.Height), 65, jpeg);
                var bytes = jpeg.ToArray();
                Span<byte> header = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
                _output.Write(header.ToArray()); _output.Write(bytes); _output.Flush();
            }
            catch { }
        }
    }
}
#endif
