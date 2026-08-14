#if WINDOWS
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using FFmpeg.AutoGen;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DN = DirectN;
using WinGrid = Microsoft.UI.Xaml.Controls.Grid;
using WinRT;

namespace BobrCam;

public sealed class CameraPreviewHandler
    : Microsoft.Maui.Handlers.ViewHandler<CameraPreview, WinGrid>
{
    public static readonly PropertyMapper<CameraPreview> CameraPreviewPropertyMapper = new();

    public CameraPreviewHandler() : base(CameraPreviewPropertyMapper) { }

    protected override WinGrid CreatePlatformView()
    {
        var panel = new SwapChainPanel
        {
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch
        };
        var grid = new WinGrid();
        grid.Children.Add(panel);
        H264PreviewRenderer.Attach(panel);
        return grid;
    }
}

public static class H264PreviewRenderer
{
    private readonly record struct QueuedAccessUnit(
        EncodedVideoAccessUnit AccessUnit,
        long ReceivedTimestamp);

    public static event Action<string>? StatusChanged;
    public static event Action<double>? FpsChanged;
    private static readonly object Sync = new();
    private static readonly ConcurrentBag<nint> FramePool = new();
    private static SwapChainPanel? _panel;
    private static DispatcherQueue? _dispatcher;
    private static D3D11SwapChainPresenter? _gpuPresenter;
    private static Channel<QueuedAccessUnit>? _queue;
    private static CancellationTokenSource? _cancellation;
    private static nint _pendingGpuFrame;
    private static long _pendingGpuReceivedAt;
    private static int _presentScheduled;
    private static bool _mirrored;
    private static int _rotationDegrees;
    private static long _fpsWindowStart = System.Diagnostics.Stopwatch.GetTimestamp();
    private static int _fpsWindowFrames;
    private static double _latencyTotalMs;

    public static void Attach(SwapChainPanel panel)
    {
        lock (Sync)
        {
            _panel = panel;
            _dispatcher = panel.DispatcherQueue;
            _gpuPresenter = new D3D11SwapChainPresenter(panel);
            panel.SizeChanged += (_, _) => _gpuPresenter?.RequestResize();
            panel.SizeChanged += (_, _) => ApplyPreviewTransform();
        }
    }

    public static void Configure(H264StreamConfiguration configuration, byte[] codecData)
    {
        lock (Sync)
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            ResetPendingFrame();
            _dispatcher?.TryEnqueue(() => _gpuPresenter?.Reset());
            Interlocked.Exchange(
                ref _fpsWindowStart,
                System.Diagnostics.Stopwatch.GetTimestamp());
            Interlocked.Exchange(ref _fpsWindowFrames, 0);
            _latencyTotalMs = 0;
            FpsChanged?.Invoke(0);
            _queue = Channel.CreateBounded<QueuedAccessUnit>(new BoundedChannelOptions(3)
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
        Channel<QueuedAccessUnit>? queue;
        lock (Sync) queue = _queue;
        queue?.Writer.TryWrite(new QueuedAccessUnit(
            accessUnit,
            System.Diagnostics.Stopwatch.GetTimestamp()));
    }

    public static void SetPreviewTransform(bool mirrored, int rotationDegrees)
    {
        _mirrored = mirrored;
        _rotationDegrees = ((rotationDegrees % 360) + 360) % 360;
        _dispatcher?.TryEnqueue(ApplyPreviewTransform);
    }

    private static void ApplyPreviewTransform()
    {
        FrameworkElement? element = _panel;
        if (element is null) return;
        var quarterTurn = _rotationDegrees is 90 or 270;
        var fitScale = 1d;
        if (quarterTurn && element.ActualWidth > 0 && element.ActualHeight > 0)
        {
            fitScale = Math.Min(
                element.ActualWidth / element.ActualHeight,
                element.ActualHeight / element.ActualWidth);
        }
        element.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        element.RenderTransform = new Microsoft.UI.Xaml.Media.CompositeTransform
        {
            ScaleX = (_mirrored ? -1 : 1) * fitScale,
            ScaleY = fitScale,
            Rotation = _rotationDegrees
        };
    }

    private static async Task DecodeLoopAsync(
        H264StreamConfiguration configuration,
        byte[] codecData,
        ChannelReader<QueuedAccessUnit> reader,
        CancellationToken token)
    {
        try
        {
            using var decoder = new FfmpegH264Decoder(
                configuration,
                codecData,
                PresentGpuFrame);
            StatusChanged?.Invoke("H.264 D3D11VA zero-copy preview ready.");
            var waitingForKeyFrame = true;
            await foreach (var queued in reader.ReadAllAsync(token))
            {
                var accessUnit = queued.AccessUnit;
                if (accessUnit.IsDiscontinuity)
                {
                    decoder.Flush();
                    waitingForKeyFrame = true;
                }
                if (waitingForKeyFrame && !accessUnit.IsKeyFrame)
                    continue;

                if (decoder.TryDecode(accessUnit, queued.ReceivedTimestamp))
                    waitingForKeyFrame = false;
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

    private static unsafe void PresentGpuFrame(nint decodedPointer, long receivedAt)
    {
        var decoded = (AVFrame*)decodedPointer;
        var framePointer = FramePool.TryTake(out var pooled)
            ? pooled
            : (nint)ffmpeg.av_frame_alloc();
        if (framePointer == 0)
            return;
        var frame = (AVFrame*)framePointer;
        ffmpeg.av_frame_unref(frame);
        if (ffmpeg.av_frame_ref(frame, decoded) < 0)
        {
            FramePool.Add(framePointer);
            return;
        }

        var replaced = Interlocked.Exchange(ref _pendingGpuFrame, framePointer);
        Interlocked.Exchange(ref _pendingGpuReceivedAt, receivedAt);
        if (replaced != 0)
            RecycleFrame(replaced);
        SchedulePresent(PresentLatestGpuOnUiThread);
    }

    private static unsafe void PresentLatestGpuOnUiThread()
    {
        try
        {
            var framePointer = Interlocked.Exchange(ref _pendingGpuFrame, 0);
            var receivedAt = Interlocked.Exchange(ref _pendingGpuReceivedAt, 0);
            if (framePointer == 0) return;
            try
            {
                _gpuPresenter?.Present((AVFrame*)framePointer);
                UpdateFps(receivedAt);
            }
            finally
            {
                RecycleFrame(framePointer);
            }
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke($"GPU preview error: {exception.GetBaseException().Message}");
            _gpuPresenter?.Reset();
        }
        finally
        {
            Interlocked.Exchange(ref _presentScheduled, 0);
            if (_pendingGpuFrame != 0)
                SchedulePresent(PresentLatestGpuOnUiThread);
        }
    }

    private static void SchedulePresent(DispatcherQueueHandler callback)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || Interlocked.Exchange(ref _presentScheduled, 1) != 0)
            return;
        if (!dispatcher.TryEnqueue(callback))
            Interlocked.Exchange(ref _presentScheduled, 0);
    }

    private static void UpdateFps(long receivedAt)
    {
        Interlocked.Increment(ref _fpsWindowFrames);
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (receivedAt > 0)
            _latencyTotalMs += (now - receivedAt) * 1000d /
                System.Diagnostics.Stopwatch.Frequency;
        var started = Interlocked.Read(ref _fpsWindowStart);
        var elapsed = now - started;
        if (elapsed < System.Diagnostics.Stopwatch.Frequency)
            return;
        if (Interlocked.CompareExchange(ref _fpsWindowStart, now, started) != started)
            return;

        var frames = Interlocked.Exchange(ref _fpsWindowFrames, 0);
        var fps = frames * (double)System.Diagnostics.Stopwatch.Frequency / elapsed;
        var latency = frames > 0 ? _latencyTotalMs / frames : 0;
        _latencyTotalMs = 0;
        FpsChanged?.Invoke(fps);
        System.Diagnostics.Debug.WriteLine(
            $"BobrCam preview: {fps:F1} FPS, receiver-to-present {latency:F1} ms, " +
            "D3D11 GPU");
    }

    private static void ResetPendingFrame()
    {
        var pending = Interlocked.Exchange(ref _pendingGpuFrame, 0);
        if (pending != 0)
            RecycleFrame(pending);
    }

    private static unsafe void RecycleFrame(nint framePointer)
    {
        ffmpeg.av_frame_unref((AVFrame*)framePointer);
        FramePool.Add(framePointer);
    }
}

internal sealed unsafe class D3D11SwapChainPresenter : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeVideoProcessorStream
    {
        public int Enable;
        public uint OutputIndex;
        public uint InputFrameOrField;
        public uint PastFrames;
        public uint FutureFrames;
        public nint PastSurfaces;
        public nint InputSurface;
        public nint FutureSurfaces;
        public nint PastSurfacesRight;
        public nint InputSurfaceRight;
        public nint FutureSurfacesRight;
    }

    private readonly SwapChainPanel _panel;
    private readonly Dictionary<(nint Texture, int Slice), DN.IComObject<DN.ID3D11VideoProcessorInputView>> _inputViews = new();
    private DN.IComObject<DN.ID3D11Device>? _device;
    private DN.IComObject<DN.ID3D11DeviceContext>? _context;
    private DN.IComObject<DN.ID3D11VideoDevice>? _videoDevice;
    private DN.IComObject<DN.ID3D11VideoContext>? _videoContext;
    private DN.IComObject<DN.ID3D11VideoProcessorEnumerator>? _enumerator;
    private DN.IComObject<DN.ID3D11VideoProcessor>? _processor;
    private DN.IComObject<DN.IDXGISwapChain1>? _swapChain;
    private DN.IComObject<DN.ID3D11VideoProcessorOutputView>? _outputView;
    private uint _sourceWidth;
    private uint _sourceHeight;
    private uint _outputWidth;
    private uint _outputHeight;
    private bool _resizeRequested = true;

    public D3D11SwapChainPresenter(SwapChainPanel panel) => _panel = panel;

    public void RequestResize() => _resizeRequested = true;

    public void Present(AVFrame* frame)
    {
        if (frame == null || frame->data[0] == null)
            return;
        var texturePointer = (nint)frame->data[0];
        var slice = (int)(nint)frame->data[1];
        var width = (uint)frame->width;
        var height = (uint)frame->height;
        EnsureResources(texturePointer, width, height);

        var inputView = GetInputView(texturePointer, slice);
        var outputView = _outputView ?? throw new InvalidOperationException("GPU preview output is unavailable.");
        var videoContext = _videoContext ?? throw new InvalidOperationException("GPU preview context is unavailable.");
        var processor = _processor ?? throw new InvalidOperationException("GPU preview processor is unavailable.");

        var scale = Math.Min(_outputWidth / (double)width, _outputHeight / (double)height);
        var renderedWidth = Math.Max(1, (int)Math.Round(width * scale));
        var renderedHeight = Math.Max(1, (int)Math.Round(height * scale));
        var destination = new DN.tagRECT
        {
            left = ((int)_outputWidth - renderedWidth) / 2,
            top = ((int)_outputHeight - renderedHeight) / 2,
            right = ((int)_outputWidth + renderedWidth) / 2,
            bottom = ((int)_outputHeight + renderedHeight) / 2
        };
        var source = new DN.tagRECT
        {
            right = (int)width,
            bottom = (int)height
        };
        var target = new DN.tagRECT
        {
            right = (int)_outputWidth,
            bottom = (int)_outputHeight
        };
        videoContext.Object.VideoProcessorSetOutputTargetRect(
            processor.Object, true, (nint)(&target));
        videoContext.Object.VideoProcessorSetStreamFrameFormat(
            processor.Object, 0,
            DN.D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE);
        videoContext.Object.VideoProcessorSetStreamSourceRect(
            processor.Object, 0, true, (nint)(&source));
        videoContext.Object.VideoProcessorSetStreamDestRect(
            processor.Object, 0, true, (nint)(&destination));

        VideoProcessorBlt(
            videoContext.Object,
            processor.Object,
            outputView.Object,
            inputView.Object);
        _swapChain!.Object.Present(0, 0).ThrowOnError();
    }

    private void EnsureResources(nint texturePointer, uint width, uint height)
    {
        var requestedWidth = Math.Max(1u, (uint)Math.Round(
            _panel.ActualWidth * (_panel.XamlRoot?.RasterizationScale ?? 1d)));
        var requestedHeight = Math.Max(1u, (uint)Math.Round(
            _panel.ActualHeight * (_panel.XamlRoot?.RasterizationScale ?? 1d)));
        if (_device is null)
            CreateDeviceResources(texturePointer);
        if (_swapChain is null || _resizeRequested ||
            requestedWidth != _outputWidth || requestedHeight != _outputHeight ||
            width != _sourceWidth || height != _sourceHeight)
            CreateSizeResources(width, height, requestedWidth, requestedHeight);
    }

    private void CreateDeviceResources(nint texturePointer)
    {
        using var source = DN.ComObject.From<DN.ID3D11Texture2D>(
            texturePointer, false);
        source.Object.GetDevice(out var device);
        _device = new DN.ComObject<DN.ID3D11Device>(device);
        _device.Object.GetImmediateContext(out var context);
        _context = new DN.ComObject<DN.ID3D11DeviceContext>(context);
        using var multithread = _context.AsComObject<DN.ID3D10Multithread>(true, true, false);
        multithread.Object.SetMultithreadProtected(true);
        _videoDevice = _device.AsComObject<DN.ID3D11VideoDevice>(true, true, false);
        _videoContext = _context.AsComObject<DN.ID3D11VideoContext>(true, true, false);
    }

    private void CreateSizeResources(
        uint sourceWidth,
        uint sourceHeight,
        uint outputWidth,
        uint outputHeight)
    {
        DisposeSizeResources();
        _sourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        _outputWidth = outputWidth;
        _outputHeight = outputHeight;
        _resizeRequested = false;

        using var dxgiDevice = _device!.AsComObject<DN.IDXGIDevice>(true, true, false);
        dxgiDevice.Object.GetAdapter(out var adapterObject).ThrowOnError();
        using var adapter = new DN.ComObject<DN.IDXGIAdapter>(adapterObject);
        adapter.Object.GetParent(typeof(DN.IDXGIFactory2).GUID, out var factoryObject).ThrowOnError();
        using var factory = DN.ComObject.From<DN.IDXGIFactory2>(
            DN.ComObject.QueryObjectInterface<DN.IDXGIFactory2>(factoryObject, true),
            false);
        var swapDescription = new DN.DXGI_SWAP_CHAIN_DESC1
        {
            Width = outputWidth,
            Height = outputHeight,
            Format = DN.DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new DN.DXGI_SAMPLE_DESC { Count = 1 },
            BufferUsage = 0x20,
            BufferCount = 2,
            Scaling = DN.DXGI_SCALING.DXGI_SCALING_STRETCH,
            SwapEffect = DN.DXGI_SWAP_EFFECT.DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
            AlphaMode = DN.DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE
        };
        factory.Object.CreateSwapChainForComposition(
            _device.Object, ref swapDescription, null!, out var swapChain).ThrowOnError();
        _swapChain = new DN.ComObject<DN.IDXGISwapChain1>(swapChain);
        var interfaceId = new Guid("63aad0b8-7c24-40ff-85a8-640d944cc325");
        var nativeObject = ((IWinRTObject)_panel).NativeObject;
        Marshal.ThrowExceptionForHR(nativeObject.TryAs(interfaceId, out var native));
        try
        {
            DN.ComObject.WithComPointer<DN.IDXGISwapChain1>(
                _swapChain.Object,
                swapChainPointer => SetSwapChain(native, swapChainPointer));
        }
        finally
        {
            Marshal.Release(native);
        }

        _swapChain.Object.GetBuffer(0, typeof(DN.ID3D11Texture2D).GUID, out var bufferObject).ThrowOnError();
        using var backBuffer = DN.ComObject.From<DN.ID3D11Texture2D>(
            DN.ComObject.QueryObjectInterface<DN.ID3D11Texture2D>(bufferObject, true),
            false);
        var content = new DN.D3D11_VIDEO_PROCESSOR_CONTENT_DESC
        {
            InputFrameFormat = DN.D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
            InputFrameRate = new DN.DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
            InputWidth = sourceWidth,
            InputHeight = sourceHeight,
            OutputFrameRate = new DN.DXGI_RATIONAL { Numerator = 60, Denominator = 1 },
            OutputWidth = outputWidth,
            OutputHeight = outputHeight,
            Usage = DN.D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_OPTIMAL_SPEED
        };
        _videoDevice!.Object.CreateVideoProcessorEnumerator(ref content, out var enumerator).ThrowOnError();
        _enumerator = new DN.ComObject<DN.ID3D11VideoProcessorEnumerator>(enumerator);
        _videoDevice.Object.CreateVideoProcessor(_enumerator.Object, 0, out var processor).ThrowOnError();
        _processor = new DN.ComObject<DN.ID3D11VideoProcessor>(processor);

        var outputDescription = new DN.D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC
        {
            ViewDimension = DN.D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D,
            __union_1 = new DN.D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC__union_0
            {
                Texture2D = new DN.D3D11_TEX2D_VPOV { MipSlice = 0 }
            }
        };
        _videoDevice.Object.CreateVideoProcessorOutputView(
            backBuffer.Object, _enumerator.Object, ref outputDescription, out var outputView).ThrowOnError();
        _outputView = new DN.ComObject<DN.ID3D11VideoProcessorOutputView>(outputView);
        var background = new DN.D3D11_VIDEO_COLOR();
        _videoContext!.Object.VideoProcessorSetOutputBackgroundColor(
            _processor.Object, false, ref background);
        _videoContext.Object.VideoProcessorSetStreamAutoProcessingMode(
            _processor.Object, 0, false);
    }

    private static void SetSwapChain(nint panel, nint swapChain)
    {
        var vtable = *(nint**)panel;
        var setSwapChain = (delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[3];
        Marshal.ThrowExceptionForHR(setSwapChain(panel, swapChain));
    }

    private static void VideoProcessorBlt(
        DN.ID3D11VideoContext context,
        DN.ID3D11VideoProcessor processor,
        DN.ID3D11VideoProcessorOutputView output,
        DN.ID3D11VideoProcessorInputView input)
    {
        DN.ComObject.WithComPointer<DN.ID3D11VideoContext>(context, contextPointer =>
            DN.ComObject.WithComPointer<DN.ID3D11VideoProcessor>(processor, processorPointer =>
                DN.ComObject.WithComPointer<DN.ID3D11VideoProcessorOutputView>(output, outputPointer =>
                    DN.ComObject.WithComPointer<DN.ID3D11VideoProcessorInputView>(input, inputPointer =>
                    {
                        var stream = new NativeVideoProcessorStream
                        {
                            Enable = 1,
                            InputSurface = inputPointer
                        };
                        var vtable = *(nint**)contextPointer;
                        var blit = (delegate* unmanaged[Stdcall]<
                            nint, nint, nint, uint, uint,
                            NativeVideoProcessorStream*, int>)vtable[53];
                        Marshal.ThrowExceptionForHR(blit(
                            contextPointer,
                            processorPointer,
                            outputPointer,
                            0,
                            1,
                            &stream));
                    }))));
    }

    private DN.IComObject<DN.ID3D11VideoProcessorInputView> GetInputView(
        nint texturePointer,
        int slice)
    {
        var key = (texturePointer, slice);
        if (_inputViews.TryGetValue(key, out var cached))
            return cached;
        using var texture = DN.ComObject.From<DN.ID3D11Texture2D>(
            texturePointer, false);
        var description = new DN.D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC
        {
            ViewDimension = DN.D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D,
            __union_2 = new DN.D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC__union_0
            {
                Texture2D = new DN.D3D11_TEX2D_VPIV
                {
                    MipSlice = 0,
                    ArraySlice = (uint)Math.Max(0, slice)
                }
            }
        };
        _videoDevice!.Object.CreateVideoProcessorInputView(
            texture.Object, _enumerator!.Object, ref description, out var view).ThrowOnError();
        var created = new DN.ComObject<DN.ID3D11VideoProcessorInputView>(view);
        _inputViews.Add(key, created);
        return created;
    }

    private void DisposeSizeResources()
    {
        foreach (var view in _inputViews.Values)
            view.Dispose();
        _inputViews.Clear();
        _outputView?.Dispose();
        _outputView = null;
        _processor?.Dispose();
        _processor = null;
        _enumerator?.Dispose();
        _enumerator = null;
        _swapChain?.Dispose();
        _swapChain = null;
    }

    public void Reset()
    {
        DisposeSizeResources();
        _videoContext?.Dispose();
        _videoContext = null;
        _videoDevice?.Dispose();
        _videoDevice = null;
        _context?.Dispose();
        _context = null;
        _device?.Dispose();
        _device = null;
        _resizeRequested = true;
    }

    public void Dispose() => Reset();
}

internal sealed unsafe class FfmpegH264Decoder : IDisposable
{
    private static readonly AVCodecContext_get_format GetFormatCallback = SelectHardwareFormat;

    private readonly Action<nint, long> _presentGpu;
    private AVCodecContext* _codecContext;
    private AVFrame* _decodedFrame;
    private AVPacket* _packet;
    private AVBufferRef* _hardwareDevice;
    private AVPixelFormat _hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private bool _disposed;

    public FfmpegH264Decoder(
        H264StreamConfiguration configuration,
        byte[] codecData,
        Action<nint, long> presentGpu)
    {
        _presentGpu = presentGpu;
        ffmpeg.RootPath = Path.Combine(AppContext.BaseDirectory, "libs");
        var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
        if (codec is null)
            throw new InvalidOperationException("FFmpeg H.264 decoder is unavailable.");
        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        _decodedFrame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        if (_codecContext is null || _decodedFrame is null || _packet is null)
            throw new OutOfMemoryException("FFmpeg decoder allocation failed.");

        _codecContext->width = configuration.Width;
        _codecContext->height = configuration.Height;
        _codecContext->coded_width = configuration.Width;
        _codecContext->coded_height = configuration.Height;
        _codecContext->pkt_timebase = new AVRational { num = 1, den = 1_000_000 };
        _codecContext->time_base = new AVRational { num = 1, den = 1_000_000 };
        _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _codecContext->thread_count = 1;

        if (codecData is { Length: > 0 })
        {
            const int extradataPadding = 64;
            _codecContext->extradata = (byte*)ffmpeg.av_mallocz((nuint)(codecData.Length + extradataPadding));
            _codecContext->extradata_size = codecData.Length;
            fixed (byte* source = codecData)
                Buffer.MemoryCopy(
                    source,
                    _codecContext->extradata,
                    codecData.Length,
                    codecData.Length);
        }
        EnableD3D11va(codec);
        ThrowIfError(ffmpeg.avcodec_open2(_codecContext, codec, null), "open H.264 decoder");
    }

    public bool TryDecode(EncodedVideoAccessUnit accessUnit, long receivedAt)
    {
        if (_disposed || accessUnit.Data.Length == 0) return false;
        ffmpeg.av_packet_unref(_packet);
        ThrowIfError(ffmpeg.av_new_packet(_packet, accessUnit.Data.Length), "allocate H.264 packet");
        fixed (byte* source = accessUnit.Data)
            Buffer.MemoryCopy(source, _packet->data, accessUnit.Data.Length, accessUnit.Data.Length);
        _packet->pts = accessUnit.PresentationTimeMicroseconds;
        _packet->dts = accessUnit.PresentationTimeMicroseconds;
        _packet->duration = accessUnit.DurationMicroseconds;
        if (accessUnit.IsKeyFrame) _packet->flags |= ffmpeg.AV_PKT_FLAG_KEY;

        var sent = ffmpeg.avcodec_send_packet(_codecContext, _packet);
        if (sent == ffmpeg.AVERROR_INVALIDDATA) return false;
        if (sent < 0 && sent != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            ThrowIfError(sent, "send H.264 packet");
        while (true)
        {
            var received = ffmpeg.avcodec_receive_frame(_codecContext, _decodedFrame);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF)
                break;
            if (received == ffmpeg.AVERROR_INVALIDDATA) return false;
            ThrowIfError(received, "receive H.264 frame");
            Present(_decodedFrame, receivedAt);
            ffmpeg.av_frame_unref(_decodedFrame);
        }
        return true;
    }

    public void Flush()
    {
        if (!_disposed && _codecContext is not null)
            ffmpeg.avcodec_flush_buffers(_codecContext);
    }

    private void Present(AVFrame* decoded, long receivedAt)
    {
        if (_hardwareDevice is null || decoded->format != (int)_hardwarePixelFormat)
            throw new InvalidOperationException("FFmpeg returned a non-D3D11 frame.");
        _presentGpu((nint)decoded, receivedAt);
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
            ThrowIfError(ffmpeg.av_hwdevice_ctx_create(&hardwareDevice,
                AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0),
                "create D3D11VA device");
            _hardwareDevice = hardwareDevice;
            _hardwarePixelFormat = config->pix_fmt;
            _codecContext->hw_device_ctx = ffmpeg.av_buffer_ref(_hardwareDevice);
            _codecContext->get_format.Pointer = Marshal.GetFunctionPointerForDelegate(GetFormatCallback);
            return;
        }
        throw new PlatformNotSupportedException("D3D11VA H.264 decoding is unavailable.");
    }

    private static AVPixelFormat SelectHardwareFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        for (var format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
            if (*format == AVPixelFormat.AV_PIX_FMT_D3D11) return *format;
        return AVPixelFormat.AV_PIX_FMT_NONE;
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
        if (_packet is not null) { var value = _packet; ffmpeg.av_packet_free(&value); _packet = null; }
        if (_decodedFrame is not null) { var value = _decodedFrame; ffmpeg.av_frame_free(&value); _decodedFrame = null; }
        if (_codecContext is not null) { var value = _codecContext; ffmpeg.avcodec_free_context(&value); _codecContext = null; }
        if (_hardwareDevice is not null) { var value = _hardwareDevice; ffmpeg.av_buffer_unref(&value); _hardwareDevice = null; }
    }
}
#endif
