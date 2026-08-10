#if WINDOWS
using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace BobrCam;

public sealed class CameraPreviewHandler
    : Microsoft.Maui.Handlers.ViewHandler<CameraPreview, Microsoft.UI.Xaml.Controls.Grid>
{
    public static readonly PropertyMapper<CameraPreview> CameraPreviewPropertyMapper = new();

    public CameraPreviewHandler() : base(CameraPreviewPropertyMapper) { }

    protected override Microsoft.UI.Xaml.Controls.Grid CreatePlatformView()
    {
        var image = new Microsoft.UI.Xaml.Controls.Image
        {
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Center,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Center
        };
        var grid = new Microsoft.UI.Xaml.Controls.Grid();
        grid.Children.Add(image);
        H264PreviewRenderer.Attach(image);
        return grid;
    }
}

public static class H264PreviewRenderer
{
    public static event Action<string>? StatusChanged;
    public static event Action<double>? FpsChanged;
    private static readonly object Sync = new();
    private static Microsoft.UI.Xaml.Controls.Image? _image;
    private static DispatcherQueue? _dispatcher;
    private static Channel<EncodedVideoAccessUnit>? _queue;
    private static CancellationTokenSource? _cancellation;
    private static WriteableBitmap? _bitmap;
    private static byte[]? _latestPixels;
    private static int _latestLength;
    private static int _latestWidth;
    private static int _latestHeight;
    private static int _presentScheduled;
    private static bool _mirrored;
    private static int _rotationDegrees;
    private static long _fpsWindowStart = System.Diagnostics.Stopwatch.GetTimestamp();
    private static int _fpsWindowFrames;

    public static void Attach(Microsoft.UI.Xaml.Controls.Image image)
    {
        lock (Sync)
        {
            _image = image;
            _dispatcher = image.DispatcherQueue;
            image.SizeChanged += (_, _) => ApplyPreviewTransform();
        }
    }

    public static void Configure(H264StreamConfiguration configuration, byte[] codecData)
    {
        lock (Sync)
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            Interlocked.Exchange(
                ref _fpsWindowStart,
                System.Diagnostics.Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _fpsWindowFrames, 0);
            FpsChanged?.Invoke(0);
            _queue = Channel.CreateBounded<EncodedVideoAccessUnit>(new BoundedChannelOptions(3)
            {
                SingleReader = true,
                SingleWriter = true,
                FullMode = BoundedChannelFullMode.DropOldest
            });
            _ = Task.Run(
                () => DecodeLoopAsync(configuration, codecData, _queue.Reader, _cancellation.Token),
                _cancellation.Token);
        }
    }

    public static void Submit(EncodedVideoAccessUnit accessUnit)
    {
        Channel<EncodedVideoAccessUnit>? queue;
        lock (Sync) queue = _queue;
        queue?.Writer.TryWrite(accessUnit);
    }

    public static void SetPreviewTransform(bool mirrored, int rotationDegrees)
    {
        _mirrored = mirrored;
        _rotationDegrees =
            ((rotationDegrees % 360) + 360) % 360;
        var dispatcher = _dispatcher;
        if (dispatcher is null) return;
        dispatcher.TryEnqueue(ApplyPreviewTransform);
    }

    private static void ApplyPreviewTransform()
    {
        if (_image is null) return;
        var quarterTurn = _rotationDegrees is 90 or 270;
        var fitScale = 1d;
        if (quarterTurn &&
            _image.ActualWidth > 0 &&
            _image.ActualHeight > 0)
        {
            fitScale = Math.Min(
                _image.ActualWidth / _image.ActualHeight,
                _image.ActualHeight / _image.ActualWidth);
        }
        _image.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        _image.RenderTransform = new Microsoft.UI.Xaml.Media.CompositeTransform
        {
            ScaleX = (_mirrored ? -1 : 1) * fitScale,
            ScaleY = fitScale,
            Rotation = _rotationDegrees
        };
    }

    private static async Task DecodeLoopAsync(
        H264StreamConfiguration configuration,
        byte[] codecData,
        ChannelReader<EncodedVideoAccessUnit> reader,
        CancellationToken token)
    {
        try
        {
            using var decoder = new FfmpegH264Decoder(configuration, codecData, PresentFrame);
            StatusChanged?.Invoke("H.264 decoder ready.");
            var waitingForKeyFrame = true;
            await foreach (var accessUnit in reader.ReadAllAsync(token))
            {
                if (accessUnit.IsDiscontinuity)
                {
                    decoder.Flush();
                    waitingForKeyFrame = true;
                }
                if (waitingForKeyFrame && !accessUnit.IsKeyFrame)
                    continue;

                if (decoder.TryDecode(accessUnit))
                {
                    waitingForKeyFrame = false;
                }
                else
                {
                    decoder.Flush();
                    waitingForKeyFrame = true;
                    StatusChanged?.Invoke("Waiting for the next H.264 keyframe…");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            System.Diagnostics.Debug.WriteLine($"BobrCam H.264 decoder stopped: {exception}");
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "bobrcam-decoder-error.txt"),
                exception.ToString());
            StatusChanged?.Invoke($"H.264 decoder error: {exception.GetBaseException().Message}");
        }
    }

    private static void PresentFrame(byte[] pixels, int length, int width, int height)
    {
        UpdateFps();
        var replaced = Interlocked.Exchange(ref _latestPixels, pixels);
        if (replaced is not null)
            ArrayPool<byte>.Shared.Return(replaced);
        _latestLength = length;
        _latestWidth = width;
        _latestHeight = height;

        var dispatcher = _dispatcher;
        if (dispatcher is null || Interlocked.Exchange(ref _presentScheduled, 1) != 0)
            return;
        if (!dispatcher.TryEnqueue(PresentLatestOnUiThread))
            Interlocked.Exchange(ref _presentScheduled, 0);
    }

    private static void PresentLatestOnUiThread()
    {
        try
        {
            var pixels = Interlocked.Exchange(ref _latestPixels, null);
            if (pixels is null) return;
            try
            {
                if (_bitmap is null ||
                    _bitmap.PixelWidth != _latestWidth ||
                    _bitmap.PixelHeight != _latestHeight)
                {
                    _bitmap = new WriteableBitmap(_latestWidth, _latestHeight);
                    if (_image is not null)
                        _image.Source = _bitmap;
                }

                using var stream = _bitmap.PixelBuffer.AsStream();
                stream.Position = 0;
                stream.Write(pixels, 0, _latestLength);
                _bitmap.Invalidate();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixels);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _presentScheduled, 0);
            if (_latestPixels is not null &&
                Interlocked.Exchange(ref _presentScheduled, 1) == 0)
            {
                if (!(_dispatcher?.TryEnqueue(PresentLatestOnUiThread) ?? false))
                    Interlocked.Exchange(ref _presentScheduled, 0);
            }
        }
    }

    private static void UpdateFps()
    {
        Interlocked.Increment(ref _fpsWindowFrames);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        var started = Interlocked.Read(ref _fpsWindowStart);
        var elapsed = now - started;
        if (elapsed < System.Diagnostics.Stopwatch.Frequency)
            return;
        if (Interlocked.CompareExchange(ref _fpsWindowStart, now, started) != started)
            return;

        var frames = Interlocked.Exchange(ref _fpsWindowFrames, 0);
        FpsChanged?.Invoke(
            frames * (double)System.Diagnostics.Stopwatch.Frequency / elapsed);
    }
}

internal sealed unsafe class FfmpegH264Decoder : IDisposable
{
    private static readonly AVCodecContext_get_format GetFormatCallback = SelectHardwareFormat;

    private readonly Action<byte[], int, int, int> _present;
    private AVCodecContext* _codecContext;
    private AVFrame* _decodedFrame;
    private AVFrame* _softwareFrame;
    private AVPacket* _packet;
    private AVBufferRef* _hardwareDevice;
    private SwsContext* _scaleContext;
    private AVPixelFormat _hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool _disposed;

    public FfmpegH264Decoder(
        H264StreamConfiguration configuration,
        byte[] codecData,
        Action<byte[], int, int, int> present)
    {
        _present = present;
        ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "libs");

        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec is null)
            throw new InvalidOperationException("FFmpeg H.264 decoder is unavailable.");

        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        _decodedFrame = ffmpeg.av_frame_alloc();
        _softwareFrame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        if (_codecContext is null || _decodedFrame is null || _softwareFrame is null || _packet is null)
            throw new OutOfMemoryException("FFmpeg decoder allocation failed.");

        _codecContext->width = configuration.Width;
        _codecContext->height = configuration.Height;
        _codecContext->coded_width = configuration.Width;
        _codecContext->coded_height = configuration.Height;
        _codecContext->pkt_timebase = new AVRational { num = 1, den = 1_000_000 };
        _codecContext->time_base = new AVRational { num = 1, den = 1_000_000 };
        _codecContext->framerate = new AVRational { num = 30, den = 1 };
        _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _codecContext->thread_count = 1;

        if (codecData is { Length: > 0 })
        {
            var copy = new byte[codecData.Length + 1];
            Buffer.BlockCopy(codecData, 0, copy, 0, codecData.Length);
            fixed (byte* p = copy)
            {
                _codecContext->extradata = (byte*)ffmpeg.av_malloc((nuint)copy.Length);
                _codecContext->extradata_size = copy.Length;
                Buffer.MemoryCopy(p, _codecContext->extradata, codecData.Length, codecData.Length);
                _codecContext->extradata[codecData.Length] = 0;
            }
        }

        EnableD3D11va(codec);
        ThrowIfError(ffmpeg.avcodec_open2(_codecContext, codec, null), "open H.264 decoder");
    }

    public bool TryDecode(EncodedVideoAccessUnit accessUnit)
    {
        if (_disposed || accessUnit.Data.Length == 0) return false;

        ffmpeg.av_packet_unref(_packet);
        ThrowIfError(ffmpeg.av_new_packet(_packet, accessUnit.Data.Length), "allocate H.264 packet");
        fixed (byte* source = accessUnit.Data)
            Buffer.MemoryCopy(source, _packet->data, accessUnit.Data.Length, accessUnit.Data.Length);
        _packet->pts = accessUnit.PresentationTimeMicroseconds;
        _packet->dts = accessUnit.PresentationTimeMicroseconds;
        _packet->duration = accessUnit.DurationMicroseconds;
        if (accessUnit.IsKeyFrame)
            _packet->flags |= ffmpeg.AV_PKT_FLAG_KEY;

        var sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet);
        if (sendResult == ffmpeg.AVERROR_INVALIDDATA)
            return false;
        if (sendResult < 0 && sendResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            ThrowIfError(sendResult, "send H.264 packet");

        while (true)
        {
            var receiveResult = ffmpeg.avcodec_receive_frame(_codecContext, _decodedFrame);
            if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) ||
                receiveResult == ffmpeg.AVERROR_EOF)
                break;
            if (receiveResult == ffmpeg.AVERROR_INVALIDDATA)
                return false;
            ThrowIfError(receiveResult, "receive H.264 frame");
            ConvertAndPresent(_decodedFrame);
            ffmpeg.av_frame_unref(_decodedFrame);
        }
        return true;
    }

    public void Flush()
    {
        if (!_disposed && _codecContext is not null)
            ffmpeg.avcodec_flush_buffers(_codecContext);
    }

    private void EnableD3D11va(AVCodec* codec)
    {
        for (var index = 0; ; index++)
        {
            var config = ffmpeg.avcodec_get_hw_config(codec, index);
            if (config is null) break;
            if ((config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) == 0 ||
                config->device_type != AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)
                continue;

            AVBufferRef* hardwareDevice = null;
            if (ffmpeg.av_hwdevice_ctx_create(
                    &hardwareDevice,
                    AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
                    null,
                    null,
                    0) < 0)
                throw new InvalidOperationException(
                    "D3D11VA hardware device creation failed.");

            _hardwareDevice = hardwareDevice;
            _hardwarePixelFormat = config->pix_fmt;
            _codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(_hardwareDevice);
            _codecContext->get_format.Pointer =
                Marshal.GetFunctionPointerForDelegate(GetFormatCallback);
            return;
        }
        throw new PlatformNotSupportedException(
            "This GPU or driver does not provide FFmpeg D3D11VA H.264 decoding.");
    }

    private static AVPixelFormat SelectHardwareFormat(
        AVCodecContext* context,
        AVPixelFormat* formats)
    {
        var preferred = context->opaque is null
            ? AVPixelFormat.AV_PIX_FMT_D3D11
            : *(AVPixelFormat*)context->opaque;
        for (var format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
        {
            if (*format == preferred ||
                *format == AVPixelFormat.AV_PIX_FMT_D3D11)
                return *format;
        }
        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    private void ConvertAndPresent(AVFrame* decoded)
    {
        if (_hardwareDevice is null ||
            decoded->format != (int)_hardwarePixelFormat)
            throw new InvalidOperationException(
                "FFmpeg returned a non-D3D11 frame; software decoding is disabled.");

        ffmpeg.av_frame_unref(_softwareFrame);
        ThrowIfError(
            ffmpeg.av_hwframe_transfer_data(_softwareFrame, decoded, 0),
            "download D3D11 frame");
        var source = _softwareFrame;

        var width = source->width;
        var height = source->height;
        var length = checked(width * height * 4);
        var pixels = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            _scaleContext = ffmpeg.sws_getCachedContext(
                _scaleContext,
                width,
                height,
                (AVPixelFormat)source->format,
                width,
                height,
                AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_FAST_BILINEAR,
                null,
                null,
                null);
            if (_scaleContext is null)
                throw new InvalidOperationException("FFmpeg color converter initialization failed.");

            fixed (byte* destination = pixels)
            {
                var destinationData = new byte_ptrArray4 { [0] = destination };
                var destinationLines = new int_array4 { [0] = width * 4 };
                var sourceData = new byte_ptrArray8();
                var sourceLines = new int_array8();
                for (var index = 0; index < 8; index++)
                {
                    sourceData[(uint)index] = source->data[(uint)index];
                    sourceLines[(uint)index] = source->linesize[(uint)index];
                }
                ffmpeg.sws_scale(
                    _scaleContext,
                    sourceData,
                    sourceLines,
                    0,
                    height,
                    destinationData,
                    destinationLines);
            }
            _present(pixels, length, width, height);
            pixels = null!;
        }
        finally
        {
            if (pixels is not null)
                ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static void ThrowIfError(int error, string operation)
    {
        if (error >= 0) return;
        var buffer = stackalloc byte[1024];
        ffmpeg.av_strerror(error, buffer, 1024);
        throw new InvalidOperationException(
            $"FFmpeg could not {operation}: {Marshal.PtrToStringAnsi((nint)buffer)}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_scaleContext is not null)
            ffmpeg.sws_freeContext(_scaleContext);
        if (_packet is not null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }
        if (_decodedFrame is not null)
        {
            var frame = _decodedFrame;
            ffmpeg.av_frame_free(&frame);
            _decodedFrame = null;
        }
        if (_softwareFrame is not null)
        {
            var frame = _softwareFrame;
            ffmpeg.av_frame_free(&frame);
            _softwareFrame = null;
        }
        if (_codecContext is not null)
        {
            var context = _codecContext;
            ffmpeg.avcodec_free_context(&context);
            _codecContext = null;
        }
        if (_hardwareDevice is not null)
        {
            var device = _hardwareDevice;
            ffmpeg.av_buffer_unref(&device);
            _hardwareDevice = null;
        }
    }
}
#endif
