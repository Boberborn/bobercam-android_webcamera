using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DirectN;

namespace VCamNetSampleSource
{
    internal sealed unsafe class GpuNv12FrameRenderer : IDisposable
    {
        private readonly IComObject<ID3D11Device> _device;
        private readonly IComObject<ID3D11VideoDevice> _videoDevice;
        private readonly IComObject<ID3D11DeviceContext> _context;
        private readonly IComObject<ID3D11VideoContext> _videoContext;
        private readonly Dictionary<(IntPtr Texture, int Slice),
            IComObject<ID3D11VideoProcessorInputView>> _inputViews = new();
        private readonly Dictionary<(IntPtr Texture, uint Slice),
            IComObject<ID3D11VideoProcessorOutputView>> _outputViews = new();
        private IComObject<ID3D11VideoProcessorEnumerator>? _enumerator;
        private IComObject<ID3D11VideoProcessor>? _processor;
        private uint _sourceWidth;
        private uint _sourceHeight;
        private uint _outputWidth;
        private uint _outputHeight;

        public GpuNv12FrameRenderer(ID3D11Device device)
        {
            _device = ComObject.WithComPointer(
                device,
                pointer =>
                {
                    Marshal.AddRef(pointer);
                    return ComObject.From<ID3D11Device>(pointer, true);
                });
            _videoDevice = new ComObject<ID3D11VideoDevice>(
                (ID3D11VideoDevice)device, false);
            device.GetImmediateContext(out var context);
            _context = new ComObject<ID3D11DeviceContext>(context);
            _videoContext = new ComObject<ID3D11VideoContext>(
                (ID3D11VideoContext)_context.Object, false);
        }

        public bool TryRender(
            IntPtr sourceTexture,
            int sourceSlice,
            int sourceWidth,
            int sourceHeight,
            int rotationDegrees,
            IComObject<IMFSample> outputSample,
            uint outputWidth,
            uint outputHeight)
        {
            if (sourceTexture == IntPtr.Zero || sourceWidth <= 0 ||
                sourceHeight <= 0 || outputWidth == 0 || outputHeight == 0)
                return false;

            EnsureProcessor(
                (uint)sourceWidth,
                (uint)sourceHeight,
                outputWidth,
                outputHeight);
            using var mediaBuffer = outputSample.GetBufferByIndex(0);
            using var dxgiBuffer = new ComObject<IMFDXGIBuffer>(
                (IMFDXGIBuffer)mediaBuffer.Object, false);
            dxgiBuffer.Object.GetResource(
                typeof(ID3D11Texture2D).GUID,
                out var outputResource).ThrowOnError();
            dxgiBuffer.Object.GetSubresourceIndex(out var outputSlice).ThrowOnError();
            using var outputTexture = new ComObject<ID3D11Texture2D>(
                (ID3D11Texture2D)outputResource);

            var inputView = GetInputView(sourceTexture, sourceSlice);
            var outputView = GetOutputView(outputTexture.Object, outputSlice);
            var processor = _processor!;
            var rotation = NormalizeRotation(rotationDegrees);
            _videoContext.Object.VideoProcessorSetStreamRotation(
                processor.Object,
                0,
                rotationDegrees != 0,
                rotation);

            var quarterTurn = rotationDegrees is 90 or 270;
            var effectiveWidth = quarterTurn ? sourceHeight : sourceWidth;
            var effectiveHeight = quarterTurn ? sourceWidth : sourceHeight;
            var scale = Math.Min(
                outputWidth / (double)effectiveWidth,
                outputHeight / (double)effectiveHeight);
            var renderedWidth = Math.Max(1, (int)Math.Round(effectiveWidth * scale));
            var renderedHeight = Math.Max(1, (int)Math.Round(effectiveHeight * scale));
            var destination = new tagRECT
            {
                left = ((int)outputWidth - renderedWidth) / 2,
                top = ((int)outputHeight - renderedHeight) / 2,
                right = ((int)outputWidth + renderedWidth) / 2,
                bottom = ((int)outputHeight + renderedHeight) / 2
            };
            _videoContext.Object.VideoProcessorSetStreamDestRect(
                processor.Object,
                0,
                true,
                (IntPtr)(&destination));

            ComObject.WithComPointer(inputView.Object, inputPointer =>
            {
                var stream = new D3D11_VIDEO_PROCESSOR_STREAM
                {
                    Enable = true,
                    pInputSurface = inputPointer
                };
                _videoContext.Object.VideoProcessorBlt(
                    processor.Object,
                    outputView.Object,
                    0,
                    1,
                    [stream]).ThrowOnError();
            });
            return true;
        }

        private void EnsureProcessor(
            uint sourceWidth,
            uint sourceHeight,
            uint outputWidth,
            uint outputHeight)
        {
            if (_processor != null &&
                _sourceWidth == sourceWidth &&
                _sourceHeight == sourceHeight &&
                _outputWidth == outputWidth &&
                _outputHeight == outputHeight)
                return;

            DisposeProcessorResources();
            _sourceWidth = sourceWidth;
            _sourceHeight = sourceHeight;
            _outputWidth = outputWidth;
            _outputHeight = outputHeight;
            var content = new D3D11_VIDEO_PROCESSOR_CONTENT_DESC
            {
                InputFrameFormat =
                    D3D11_VIDEO_FRAME_FORMAT.D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE,
                InputFrameRate = new DXGI_RATIONAL
                    { Numerator = 60, Denominator = 1 },
                InputWidth = sourceWidth,
                InputHeight = sourceHeight,
                OutputFrameRate = new DXGI_RATIONAL
                    { Numerator = 60, Denominator = 1 },
                OutputWidth = outputWidth,
                OutputHeight = outputHeight,
                Usage = D3D11_VIDEO_USAGE.D3D11_VIDEO_USAGE_OPTIMAL_SPEED
            };
            _videoDevice.Object.CreateVideoProcessorEnumerator(
                ref content,
                out var enumerator).ThrowOnError();
            _enumerator = new ComObject<ID3D11VideoProcessorEnumerator>(enumerator);
            _videoDevice.Object.CreateVideoProcessor(
                _enumerator.Object,
                0,
                out var processor).ThrowOnError();
            _processor = new ComObject<ID3D11VideoProcessor>(processor);
            var black = new D3D11_VIDEO_COLOR();
            _videoContext.Object.VideoProcessorSetOutputBackgroundColor(
                _processor.Object,
                false,
                ref black);
            _videoContext.Object.VideoProcessorSetStreamAutoProcessingMode(
                _processor.Object,
                0,
                false);
        }

        private IComObject<ID3D11VideoProcessorInputView> GetInputView(
            IntPtr texturePointer,
            int slice)
        {
            var key = (texturePointer, slice);
            if (_inputViews.TryGetValue(key, out var cached))
                return cached;
            using var texture = ComObject.From<ID3D11Texture2D>(
                texturePointer,
                false);
            var description = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC
            {
                ViewDimension =
                    D3D11_VPIV_DIMENSION.D3D11_VPIV_DIMENSION_TEXTURE2D,
                __union_2 = new D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC__union_0
                {
                    Texture2D = new D3D11_TEX2D_VPIV
                    {
                        MipSlice = 0,
                        ArraySlice = (uint)Math.Max(0, slice)
                    }
                }
            };
            _videoDevice.Object.CreateVideoProcessorInputView(
                texture.Object,
                _enumerator!.Object,
                ref description,
                out var view).ThrowOnError();
            var created = new ComObject<ID3D11VideoProcessorInputView>(view);
            _inputViews.Add(key, created);
            return created;
        }

        private IComObject<ID3D11VideoProcessorOutputView> GetOutputView(
            ID3D11Texture2D texture,
            uint slice)
        {
            var pointer = ComObject.WithComPointer(
                texture,
                static value => value);
            var key = (pointer, slice);
            if (_outputViews.TryGetValue(key, out var cached))
                return cached;

            texture.GetDesc(out var textureDescription);
            var description = new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC();
            if (textureDescription.ArraySize > 1)
            {
                description.ViewDimension =
                    D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2DARRAY;
                description.__union_1 =
                    new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC__union_0
                    {
                        Texture2DArray = new D3D11_TEX2D_ARRAY_VPOV
                        {
                            MipSlice = 0,
                            FirstArraySlice = slice,
                            ArraySize = 1
                        }
                    };
            }
            else
            {
                description.ViewDimension =
                    D3D11_VPOV_DIMENSION.D3D11_VPOV_DIMENSION_TEXTURE2D;
                description.__union_1 =
                    new D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC__union_0
                    {
                        Texture2D = new D3D11_TEX2D_VPOV { MipSlice = 0 }
                    };
            }
            _videoDevice.Object.CreateVideoProcessorOutputView(
                texture,
                _enumerator!.Object,
                ref description,
                out var view).ThrowOnError();
            var created = new ComObject<ID3D11VideoProcessorOutputView>(view);
            _outputViews.Add(key, created);
            return created;
        }

        private static D3D11_VIDEO_PROCESSOR_ROTATION NormalizeRotation(int degrees) =>
            degrees switch
            {
                90 => D3D11_VIDEO_PROCESSOR_ROTATION
                    .D3D11_VIDEO_PROCESSOR_ROTATION_90,
                180 => D3D11_VIDEO_PROCESSOR_ROTATION
                    .D3D11_VIDEO_PROCESSOR_ROTATION_180,
                270 => D3D11_VIDEO_PROCESSOR_ROTATION
                    .D3D11_VIDEO_PROCESSOR_ROTATION_270,
                _ => D3D11_VIDEO_PROCESSOR_ROTATION
                    .D3D11_VIDEO_PROCESSOR_ROTATION_IDENTITY
            };

        private void DisposeProcessorResources()
        {
            foreach (var view in _inputViews.Values)
                view.Dispose();
            _inputViews.Clear();
            foreach (var view in _outputViews.Values)
                view.Dispose();
            _outputViews.Clear();
            _processor?.Dispose();
            _processor = null;
            _enumerator?.Dispose();
            _enumerator = null;
        }

        public void Dispose()
        {
            DisposeProcessorResources();
            _videoContext.Dispose();
            _context.Dispose();
            _videoDevice.Dispose();
            _device.Dispose();
        }
    }
}
