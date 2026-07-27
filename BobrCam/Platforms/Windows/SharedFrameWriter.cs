#if WINDOWS
using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace BobrCam;

internal static unsafe class SharedFrameWriter
{
    internal const int HeaderSize = 64;
    private const int MaxFrameBytes = 1920 * 1920 * 4;
    private const int MappingSize = HeaderSize + MaxFrameBytes;
    private static readonly long MinimumFrameTicks = Stopwatch.Frequency / 30;
    private static readonly string FrameFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "BobrCam",
        "Frames",
        "live.bgra");

    private static readonly object Sync = new();
    private static MemoryMappedFile? _mapping;
    private static MemoryMappedViewAccessor? _view;
    private static long _sequence;
    private static long _lastPublishTicks;

    public static void Publish(byte[] pixels, int length, int width, int height)
    {
        if (length <= 0 || length > MaxFrameBytes || length != checked(width * height * 4))
            return;

        var now = Stopwatch.GetTimestamp();
        if (now - Interlocked.Read(ref _lastPublishTicks) < MinimumFrameTicks)
            return;

        lock (Sync)
        {
            now = Stopwatch.GetTimestamp();
            if (now - _lastPublishTicks < MinimumFrameTicks)
                return;
            _lastPublishTicks = now;

            EnsureMapping();
            var view = _view!;
            var writingSequence = Interlocked.Add(ref _sequence, 2) - 1;
            view.Write(0, 0x42434631u); // "BCF1"
            view.Write(4, 1);
            view.Write(8, writingSequence);
            view.Write(16, width);
            view.Write(20, height);
            view.Write(24, width * 4);
            view.Write(28, length);

            byte* destination = null;
            try
            {
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref destination);
                destination += view.PointerOffset + HeaderSize;
                fixed (byte* source = pixels)
                    Buffer.MemoryCopy(source, destination, MaxFrameBytes, length);
            }
            finally
            {
                if (destination is not null)
                    view.SafeMemoryMappedViewHandle.ReleasePointer();
            }

            Thread.MemoryBarrier();
            view.Write(8, writingSequence + 1);
        }
    }

    private static void EnsureMapping()
    {
        if (_mapping is not null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FrameFilePath)!);
        using var file = new FileStream(
            FrameFilePath,
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
