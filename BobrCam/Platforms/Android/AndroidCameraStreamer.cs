#if ANDROID
using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using Android.Graphics;
using Android.Hardware;
using LegacyCamera = Android.Hardware.Camera;

namespace BobrCam;

public static class AndroidCameraStreamer
{
    private const int TargetWidth = 1920;
    private const int TargetHeight = 1080;
    private const int TargetFps = 60;

    private static LegacyCamera? _camera;
    private static TcpClient? _client;
    private static SslStream? _secureStream;
    private static SurfaceTexture? _previewSurface;
    private static bool _wantPreview;
    private static int _displayOrientation;
    private static int _previewWidth = TargetWidth;
    private static int _previewHeight = TargetHeight;

    public static volatile string? LastError;
    public static int FramesSent;

    public static void SetPreviewSurface(SurfaceTexture? surface, Action<int, int, int>? onReady)
    {
        _previewSurface = surface;
        if (surface is not null)
        {
            onReady?.Invoke(_displayOrientation, _previewWidth, _previewHeight);
            if (_wantPreview)
                _ = StartCameraAsync();
        }
    }

    public static void ApplyDisplayOrientation(int degrees)
    {
        _displayOrientation = (degrees + 90) % 360;
        if (_camera is not null)
        {
            try { _camera.SetDisplayOrientation(_displayOrientation); } catch { }
        }
    }

    public static void GetRotatedAspect(out int w, out int h)
    {
        if (_displayOrientation is 90 or 270) { w = _previewHeight; h = _previewWidth; }
        else { w = _previewWidth; h = _previewHeight; }
    }

    public static async Task StartLocalPreviewAsync()
    {
        _wantPreview = true;
        if (_previewSurface is not null)
            await StartCameraAsync();
    }

    public static void StopCamera()
    {
        if (_camera is not null)
        {
            try { _camera.SetPreviewCallback(null); } catch { }
            try { _camera.StopPreview(); } catch { }
            try { _camera.Release(); } catch { }
            _camera = null;
        }
    }

    private static async Task StartCameraAsync()
    {
        if (_camera is not null || _previewSurface is null) return;
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            if (_camera is not null || _previewSurface is null) return;
            _camera = LegacyCamera.Open(0);
            var parameters = _camera.GetParameters()!;
            SelectPreviewSize(parameters);
            SelectFpsRange(parameters);
            _camera.SetParameters(parameters);
            _previewWidth = parameters.PreviewSize!.Width;
            _previewHeight = parameters.PreviewSize!.Height;
            try { _camera.SetDisplayOrientation(_displayOrientation); } catch { }
            _camera.SetPreviewTexture(_previewSurface);
            _camera.SetPreviewCallback(_secureStream is not null ? new FrameCallback(_secureStream) : null);
            _camera.StartPreview();
        });
    }

    private static void SelectPreviewSize(LegacyCamera.Parameters parameters)
    {
        var sizes = parameters.SupportedPreviewSizes;
        if (sizes is null || sizes.Count == 0)
        {
            parameters.SetPreviewSize(TargetWidth, TargetHeight);
            return;
        }
        var target = sizes.FirstOrDefault(s => s?.Width == TargetWidth && s?.Height == TargetHeight)
            ?? sizes.OrderByDescending(s => (double)s!.Width * s!.Height)
                    .FirstOrDefault(s => Math.Abs((double)s!.Width / s!.Height - 16.0 / 9.0) < 0.01)
            ?? sizes[0];
        parameters.SetPreviewSize(target!.Width, target!.Height);
    }

    private static void SelectFpsRange(LegacyCamera.Parameters parameters)
    {
        var ranges = parameters.SupportedPreviewFpsRange;
        if (ranges is null || ranges.Count == 0) return;
        var target = ranges
            .Select(r => new[] { r[0], r[1] })
            .Where(r => r[1] >= TargetFps * 1000)
            .OrderBy(r => r[1])
            .FirstOrDefault()
            ?? ranges.Select(r => new[] { r[0], r[1] }).OrderByDescending(r => r[1]).First();
        parameters.SetPreviewFpsRange(target[0], target[1]);
    }

    public static async Task StartAsync(string host, int port, string discoveredFingerprint)
    {
        LastError = null;
        FramesSent = 0;
        StopCamera();

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

        if (_previewSurface is not null)
            await StartCameraAsync();
    }

    private static byte[] RotateJpeg(byte[] jpeg)
    {
        var pcRotation = (_displayOrientation + 270) % 360; // 90° left from phone
        if (pcRotation is 0 or 180) return jpeg;
        var bmp = BitmapFactory.DecodeByteArray(jpeg, 0, jpeg.Length);
        if (bmp is null) return jpeg;
        var matrix = new Matrix();
        matrix.PostRotate(pcRotation);
        var rotated = Bitmap.CreateBitmap(bmp, 0, 0, bmp.Width, bmp.Height, matrix, true);
        bmp.Recycle();
        using var ms = new MemoryStream();
        rotated.Compress(Bitmap.CompressFormat.Jpeg, 70, ms);
        rotated.Recycle();
        return ms.ToArray();
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
                var bytes = RotateJpeg(jpeg.ToArray());
                Span<byte> header = stackalloc byte[4];
                BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
                _output.Write(header.ToArray()); _output.Write(bytes); _output.Flush();
                Interlocked.Increment(ref FramesSent);
            }
            catch (Exception ex)
            {
                LastError = ex.GetBaseException().Message;
            }
        }
    }
}
#endif