using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using VCamNetSampleSource.Utilities;

namespace VCamNetSampleSource
{
    internal sealed class SharedH264FrameReader : IDisposable
    {
        private const uint Magic = 0x42434853; // "BCHS"
        private const int Version = 1;
        private const int CodecDataCapacity = 256 * 1024;
        private const int HeaderSize = 64 + CodecDataCapacity;
        private const int SlotCount = 8;
        private const int SlotHeaderSize = 64;
        private const int SlotPayloadCapacity = 4 * 1024 * 1024;
        private const int SlotSize = SlotHeaderSize + SlotPayloadCapacity;
        private const long MappingSize = HeaderSize + (long)SlotCount * SlotSize;
        private static readonly string StreamFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "BobrCam",
            "Frames",
            "live.h264");

        private MemoryMappedFile? _mapping;
        private MemoryMappedViewAccessor? _view;
        private HardwareH264Decoder? _decoder;
        private IntPtr _d3dDevice;
        private long _generation;
        private long _nextSequence;
        private bool _waitingForKeyFrame = true;
        private string? _lastError;
        public int RotationDegrees { get; private set; }

        public void SetD3DDevice(IntPtr device)
        {
            if (_d3dDevice == device)
                return;
            _d3dDevice = device;
            _decoder?.Dispose();
            _decoder = null;
            _generation = 0;
        }

        public bool TryGetLatestGpuFrame(
            out IntPtr texture,
            out int arraySlice,
            out int width,
            out int height)
        {
            PumpLatestFrame();
            var decoder = _decoder;
            if (decoder != null && decoder.HasGpuFrame)
            {
                texture = decoder.TexturePointer;
                arraySlice = decoder.ArraySlice;
                width = decoder.FrameWidth;
                height = decoder.FrameHeight;
                return true;
            }

            texture = IntPtr.Zero;
            arraySlice = 0;
            width = 0;
            height = 0;
            return false;
        }

        private void PumpLatestFrame()
        {
            try
            {
                EnsureMapping();
                var view = _view!;
                if (view.ReadUInt32(0) != Magic ||
                    view.ReadInt32(4) != Version)
                    return;

                var generation = view.ReadInt64(8);
                if (generation <= 0 || (generation & 1) != 0)
                    return;
                var rotationDegrees = view.ReadInt32(44);
                RotationDegrees = rotationDegrees is 90 or 180 or 270
                    ? rotationDegrees
                    : 0;

                if (generation != _generation)
                    ConfigureDecoder(view, generation);

                PumpAccessUnits(view, generation);
            }
            catch (Exception exception)
            {
                var error = exception.GetBaseException().Message;
                if (!string.Equals(error, _lastError, StringComparison.Ordinal))
                {
                    _lastError = error;
                    EventProvider.LogError(
                        "Shared H.264 reader: " + exception);
                }
                ResetMapping();
            }
        }

        private void EnsureMapping()
        {
            if (_mapping != null)
                return;

            using var file = new FileStream(
                StreamFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (file.Length < MappingSize)
                throw new InvalidDataException("BobrCam H.264 stream mapping is incomplete.");

            _mapping = MemoryMappedFile.CreateFromFile(
                file,
                null,
                MappingSize,
                MemoryMappedFileAccess.Read,
                HandleInheritability.None,
                leaveOpen: false);
            _view = _mapping.CreateViewAccessor(
                0,
                MappingSize,
                MemoryMappedFileAccess.Read);
        }

        private void ConfigureDecoder(
            MemoryMappedViewAccessor view,
            long generation)
        {
            var width = view.ReadInt32(24);
            var height = view.ReadInt32(28);
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096)
                throw new InvalidDataException("Invalid shared H.264 dimensions.");
            if (view.ReadInt64(8) != generation)
                return;

            _decoder?.Dispose();
            _decoder = new HardwareH264Decoder(
                width,
                height,
                _d3dDevice);
            _generation = generation;
            var latestSequence = view.ReadInt64(16);
            _nextSequence = FindNewestKeyFrame(
                view,
                generation,
                latestSequence);
            _waitingForKeyFrame = true;
            RotationDegrees = 0;
        }

        private void PumpAccessUnits(
            MemoryMappedViewAccessor view,
            long generation)
        {
            var decoder = _decoder;
            if (decoder == null)
                return;

            var latestSequence = view.ReadInt64(16);
            var oldestAvailable = Math.Max(1, latestSequence - SlotCount + 1);
            if (_nextSequence < oldestAvailable)
            {
                decoder.Flush();
                _waitingForKeyFrame = true;
                _nextSequence = FindNewestKeyFrame(
                    view,
                    generation,
                    latestSequence);
            }

            while (_nextSequence <= latestSequence)
            {
                var sequence = _nextSequence++;
                var slotOffset = HeaderSize +
                    ((sequence - 1) % SlotCount) * (long)SlotSize;
                if (view.ReadInt64(slotOffset) != sequence ||
                    view.ReadInt64(slotOffset + 8) != generation)
                    continue;

                var presentationTime = view.ReadInt64(slotOffset + 16);
                var duration = view.ReadInt32(slotOffset + 24);
                var length = view.ReadInt32(slotOffset + 28);
                var flags = view.ReadInt32(slotOffset + 32);
                if (length <= 0 || length > SlotPayloadCapacity)
                    continue;

                var isKeyFrame = (flags & 1) != 0;
                var isDiscontinuity = (flags & 2) != 0;
                if (isDiscontinuity)
                {
                    decoder.Flush();
                    _waitingForKeyFrame = true;
                }
                if (_waitingForKeyFrame && !isKeyFrame)
                    continue;

                var payload = ArrayPool<byte>.Shared.Rent(length);
                try
                {
                    view.ReadArray(
                        slotOffset + SlotHeaderSize,
                        payload,
                        0,
                        length);
                    if (view.ReadInt64(slotOffset) != sequence)
                    {
                        decoder.Flush();
                        _waitingForKeyFrame = true;
                        continue;
                    }

                    if (decoder.TryDecode(
                            payload,
                            length,
                            presentationTime,
                            Math.Max(0, duration),
                            isKeyFrame))
                        _waitingForKeyFrame = false;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(payload);
                }
            }
        }

        private static long FindNewestKeyFrame(
            MemoryMappedViewAccessor view,
            long generation,
            long latestSequence)
        {
            var oldestSequence = Math.Max(
                1,
                latestSequence - SlotCount + 1);
            for (var sequence = latestSequence;
                sequence >= oldestSequence;
                sequence--)
            {
                var slotOffset = HeaderSize +
                    ((sequence - 1) % SlotCount) * (long)SlotSize;
                if (view.ReadInt64(slotOffset) == sequence &&
                    view.ReadInt64(slotOffset + 8) == generation &&
                    (view.ReadInt32(slotOffset + 32) & 1) != 0)
                    return sequence;
            }

            return latestSequence + 1;
        }

        private void ResetMapping()
        {
            _view?.Dispose();
            _view = null;
            _mapping?.Dispose();
            _mapping = null;
            _decoder?.Dispose();
            _decoder = null;
            _generation = 0;
            _nextSequence = 0;
            _waitingForKeyFrame = true;
        }

        public void Dispose() => ResetMapping();
    }

    internal sealed unsafe class HardwareH264Decoder : IDisposable
    {
        private static readonly AVCodecContext_get_format GetFormatCallback =
            SelectHardwareFormat;

        private AVCodecContext* _codecContext;
        private AVFrame* _decodedFrame;
        private AVFrame* _outputFrame;
        private AVPacket* _packet;
        private AVBufferRef* _hardwareDevice;
        private AVPixelFormat _hardwarePixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
        private bool _disposed;
        private readonly IntPtr _externalDevice;

        public int FrameWidth { get; private set; }
        public int FrameHeight { get; private set; }
        public bool HasGpuFrame =>
            _outputFrame != null && _outputFrame->data[0] != null;
        public IntPtr TexturePointer => HasGpuFrame
            ? (IntPtr)_outputFrame->data[0]
            : IntPtr.Zero;
        public int ArraySlice => HasGpuFrame
            ? (int)(IntPtr)_outputFrame->data[1]
            : 0;

        public HardwareH264Decoder(
            int width,
            int height,
            IntPtr externalDevice)
        {
            _externalDevice = externalDevice;
            var assemblyDirectory = Path.GetDirectoryName(
                typeof(HardwareH264Decoder).Assembly.Location)
                ?? AppContext.BaseDirectory;
            ffmpeg.RootPath = Path.Combine(assemblyDirectory, "libs");

            var codec = ffmpeg.avcodec_find_decoder(AVCodecID.AV_CODEC_ID_H264);
            if (codec == null)
                throw new InvalidOperationException("FFmpeg H.264 decoder is unavailable.");

            _codecContext = ffmpeg.avcodec_alloc_context3(codec);
            _decodedFrame = ffmpeg.av_frame_alloc();
            _outputFrame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            if (_codecContext == null || _decodedFrame == null ||
                _outputFrame == null || _packet == null)
                throw new OutOfMemoryException("FFmpeg decoder allocation failed.");

            _codecContext->width = width;
            _codecContext->height = height;
            _codecContext->pkt_timebase =
                new AVRational { num = 1, den = 1_000_000 };
            _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
            _codecContext->thread_count = 1;

            EnableD3D11va(codec);
            ThrowIfError(
                ffmpeg.avcodec_open2(_codecContext, codec, null),
                "open H.264 decoder");
        }

        public bool TryDecode(
            byte[] data,
            int length,
            long presentationTime,
            int duration,
            bool isKeyFrame)
        {
            if (_disposed || length <= 0)
                return false;

            ffmpeg.av_packet_unref(_packet);
            ThrowIfError(
                ffmpeg.av_new_packet(_packet, length),
                "allocate H.264 packet");
            fixed (byte* source = data)
                Buffer.MemoryCopy(source, _packet->data, length, length);
            _packet->pts = presentationTime;
            _packet->dts = presentationTime;
            _packet->duration = duration;
            if (isKeyFrame)
                _packet->flags |= ffmpeg.AV_PKT_FLAG_KEY;

            var sendResult = ffmpeg.avcodec_send_packet(_codecContext, _packet);
            if (sendResult == ffmpeg.AVERROR_INVALIDDATA)
                return false;
            if (sendResult < 0 &&
                sendResult != ffmpeg.AVERROR(ffmpeg.EAGAIN))
                ThrowIfError(sendResult, "send H.264 packet");

            var accepted = true;
            while (true)
            {
                var receiveResult = ffmpeg.avcodec_receive_frame(
                    _codecContext,
                    _decodedFrame);
                if (receiveResult == ffmpeg.AVERROR(ffmpeg.EAGAIN) ||
                    receiveResult == ffmpeg.AVERROR_EOF)
                    break;
                if (receiveResult == ffmpeg.AVERROR_INVALIDDATA)
                {
                    accepted = false;
                    break;
                }

                ThrowIfError(receiveResult, "receive H.264 frame");
                ConvertFrame(_decodedFrame);
                ffmpeg.av_frame_unref(_decodedFrame);
            }
            return accepted;
        }

        public void Flush()
        {
            if (!_disposed && _codecContext != null)
                ffmpeg.avcodec_flush_buffers(_codecContext);
        }

        private void EnableD3D11va(AVCodec* codec)
        {
            for (var index = 0; ; index++)
            {
                var config = ffmpeg.avcodec_get_hw_config(codec, index);
                if (config == null)
                    break;
                if ((config->methods &
                        (int)AvCodecHwConfigMethod
                            .AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) == 0 ||
                    config->device_type !=
                        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)
                    continue;

                AVBufferRef* hardwareDevice;
                if (_externalDevice != IntPtr.Zero)
                {
                    hardwareDevice = ffmpeg.av_hwdevice_ctx_alloc(
                        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
                    if (hardwareDevice == null)
                        throw new OutOfMemoryException(
                            "D3D11VA device context allocation failed.");
                    var deviceContext =
                        (AVHWDeviceContext*)hardwareDevice->data;
                    var d3dContext =
                        (AVD3D11VADeviceContext*)deviceContext->hwctx;
                    Marshal.AddRef(_externalDevice);
                    d3dContext->device =
                        (FFmpeg.AutoGen.ID3D11Device*)_externalDevice;
                    ThrowIfError(
                        ffmpeg.av_hwdevice_ctx_init(hardwareDevice),
                        "initialize shared D3D11VA device");
                }
                else
                {
                    hardwareDevice = null;
                    if (ffmpeg.av_hwdevice_ctx_create(
                            &hardwareDevice,
                            AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA,
                            null,
                            null,
                            0) < 0)
                        throw new InvalidOperationException(
                            "D3D11VA hardware device creation failed.");
                }

                _hardwareDevice = hardwareDevice;
                _hardwarePixelFormat = config->pix_fmt;
                _codecContext->hw_device_ctx =
                    ffmpeg.av_buffer_ref(_hardwareDevice);
                _codecContext->get_format.Pointer =
                    Marshal.GetFunctionPointerForDelegate(GetFormatCallback);
                return;
            }

            throw new PlatformNotSupportedException(
                "D3D11VA H.264 decoding is unavailable.");
        }

        private static AVPixelFormat SelectHardwareFormat(
            AVCodecContext* context,
            AVPixelFormat* formats)
        {
            for (var format = formats;
                *format != AVPixelFormat.AV_PIX_FMT_NONE;
                format++)
            {
                if (*format == AVPixelFormat.AV_PIX_FMT_D3D11)
                    return *format;
            }
            return AVPixelFormat.AV_PIX_FMT_NONE;
        }

        private void ConvertFrame(AVFrame* decoded)
        {
            if (_hardwareDevice == null ||
                decoded->format != (int)_hardwarePixelFormat)
                throw new InvalidOperationException(
                    "FFmpeg returned a non-D3D11 frame.");

            ffmpeg.av_frame_unref(_outputFrame);
            ThrowIfError(
                ffmpeg.av_frame_ref(_outputFrame, decoded),
                "retain D3D11 frame");
            FrameWidth = decoded->width;
            FrameHeight = decoded->height;
        }

        private static void ThrowIfError(int error, string operation)
        {
            if (error >= 0)
                return;
            var buffer = stackalloc byte[1024];
            ffmpeg.av_strerror(error, buffer, 1024);
            throw new InvalidOperationException(
                $"FFmpeg could not {operation}: " +
                Marshal.PtrToStringAnsi((nint)buffer));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_packet != null)
            {
                var packet = _packet;
                ffmpeg.av_packet_free(&packet);
                _packet = null;
            }
            if (_decodedFrame != null)
            {
                var frame = _decodedFrame;
                ffmpeg.av_frame_free(&frame);
                _decodedFrame = null;
            }
            if (_outputFrame != null)
            {
                var frame = _outputFrame;
                ffmpeg.av_frame_free(&frame);
                _outputFrame = null;
            }
            if (_codecContext != null)
            {
                var context = _codecContext;
                ffmpeg.avcodec_free_context(&context);
                _codecContext = null;
            }
            if (_hardwareDevice != null)
            {
                var device = _hardwareDevice;
                ffmpeg.av_buffer_unref(&device);
                _hardwareDevice = null;
            }
        }
    }
}
