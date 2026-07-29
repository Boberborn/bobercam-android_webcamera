#if WINDOWS
using System.IO.MemoryMappedFiles;

namespace BobrCam;

internal static unsafe class SharedH264StreamWriter
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

    private static readonly object Sync = new();
    private static MemoryMappedFile? _mapping;
    private static MemoryMappedViewAccessor? _view;
    private static long _generation;
    private static long _sequence;
    private static int _rotationDegrees;

    public static void Configure(
        in H264StreamConfiguration configuration,
        byte[] codecData)
    {
        ArgumentNullException.ThrowIfNull(codecData);
        if (codecData.Length > CodecDataCapacity)
            throw new ArgumentOutOfRangeException(
                nameof(codecData),
                "H.264 codec configuration is too large for the shared stream.");

        lock (Sync)
        {
            EnsureMapping();
            var view = _view!;
            var completeGeneration = DateTime.UtcNow.Ticks & ~1L;
            if (completeGeneration <= _generation)
                completeGeneration = _generation + 2;
            view.Write(8, completeGeneration - 1);
            view.Write(0, Magic);
            view.Write(4, Version);
            view.Write(16, 0L);
            view.Write(24, (int)configuration.Width);
            view.Write(28, (int)configuration.Height);
            view.Write(32, (int)configuration.FrameRateNumerator);
            view.Write(36, (int)configuration.FrameRateDenominator);
            view.Write(40, codecData.Length);
            view.Write(44, _rotationDegrees);
            view.Write(48, DateTime.UtcNow.Ticks);
            if (codecData.Length > 0)
                view.WriteArray(64, codecData, 0, codecData.Length);
            Thread.MemoryBarrier();
            view.Write(8, completeGeneration);
            _generation = completeGeneration;
            _sequence = 0;
        }
    }

    public static void SetRotation(int rotationDegrees)
    {
        lock (Sync)
        {
            _rotationDegrees =
                ((rotationDegrees % 360) + 360) % 360;
            _view?.Write(44, _rotationDegrees);
        }
    }

    public static void Publish(EncodedVideoAccessUnit accessUnit)
    {
        ArgumentNullException.ThrowIfNull(accessUnit);
        var data = accessUnit.Data;
        if (data.Length == 0 || data.Length > SlotPayloadCapacity)
            return;

        lock (Sync)
        {
            if (_view is null || _generation == 0)
                return;

            var sequence = ++_sequence;
            var slotOffset = HeaderSize +
                ((sequence - 1) % SlotCount) * (long)SlotSize;
            var view = _view;
            view.Write(slotOffset, -sequence);
            view.Write(slotOffset + 8, _generation);
            view.Write(slotOffset + 16, accessUnit.PresentationTimeMicroseconds);
            view.Write(slotOffset + 24, (int)accessUnit.DurationMicroseconds);
            view.Write(slotOffset + 28, data.Length);
            var flags = (accessUnit.IsKeyFrame ? 1 : 0) |
                (accessUnit.IsDiscontinuity ? 2 : 0);
            view.Write(slotOffset + 32, flags);

            byte* destination = null;
            try
            {
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref destination);
                destination += view.PointerOffset + slotOffset + SlotHeaderSize;
                fixed (byte* source = data)
                    Buffer.MemoryCopy(
                        source,
                        destination,
                        SlotPayloadCapacity,
                        data.Length);
            }
            finally
            {
                if (destination is not null)
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            Thread.MemoryBarrier();
            view.Write(slotOffset, sequence);
            view.Write(16, sequence);
            view.Write(48, DateTime.UtcNow.Ticks);
        }
    }

    private static void EnsureMapping()
    {
        if (_mapping is not null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(StreamFilePath)!);
        using var file = new FileStream(
            StreamFilePath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);
        file.SetLength(MappingSize);
        _mapping = MemoryMappedFile.CreateFromFile(
            file,
            null,
            MappingSize,
            MemoryMappedFileAccess.ReadWrite,
            HandleInheritability.None,
            leaveOpen: false);
        _view = _mapping.CreateViewAccessor(
            0,
            MappingSize,
            MemoryMappedFileAccess.ReadWrite);
    }
}
#endif
