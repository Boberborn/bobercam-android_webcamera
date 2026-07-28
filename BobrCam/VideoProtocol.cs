using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace BobrCam;

public enum VideoPacketType : byte
{
    StreamConfiguration = 1,
    AccessUnit = 2,
    EndOfStream = 3
}

[Flags]
public enum VideoPacketFlags : ushort
{
    None = 0,
    KeyFrame = 1 << 0,
    CodecConfiguration = 1 << 1,
    Discontinuity = 1 << 2
}

public enum H264Profile : byte
{
    Baseline = 66,
    Main = 77,
    High = 100
}

public enum H264ChromaFormat : byte
{
    Yuv420 = 1
}

public readonly record struct VideoPacketHeader(
    VideoPacketType Type,
    VideoPacketFlags Flags,
    int PayloadLength,
    uint Sequence,
    long PresentationTimeMicroseconds,
    uint DurationMicroseconds);

public readonly record struct H264StreamConfiguration(
    ushort Width,
    ushort Height,
    ushort FrameRateNumerator,
    ushort FrameRateDenominator,
    uint Bitrate,
    H264Profile Profile,
    byte Level,
    byte BitDepth,
    H264ChromaFormat ChromaFormat,
    ushort KeyFrameIntervalMilliseconds,
    ushort RotationDegrees,
    byte MaxBFrames);

public sealed record EncodedVideoAccessUnit(
    byte[] Data,
    uint Sequence,
    long PresentationTimeMicroseconds,
    uint DurationMicroseconds,
    bool IsKeyFrame,
    bool IsDiscontinuity);

public static class VideoProtocol
{
    public const int Port = 28444;
    public const int DiscoveryPort = 28445;

    public const byte Version = 2;
    public const int PacketHeaderSize = 32;
    public const int StreamConfigurationSize = 24;
    public const int StreamRequestSize = 10;
    public const int MaxPayloadBytes = 8 * 1024 * 1024;

    private const uint PacketMagic = 0x42434832; // "BCH2"
    private const uint StreamRequestMagic = 0x42435231; // "BCR1"

    public static bool TryWriteStreamRequest(
        Span<byte> destination,
        byte frameRate,
        ushort width,
        ushort height,
        bool prioritizeResolution)
    {
        if (destination.Length < StreamRequestSize ||
            frameRate is not (30 or 60) ||
            width is not (1280 or 1920 or 2560 or 3840) ||
            height is not (720 or 1080 or 1440 or 2160))
            return false;

        BinaryPrimitives.WriteUInt32BigEndian(destination, StreamRequestMagic);
        destination[4] = frameRate;
        BinaryPrimitives.WriteUInt16BigEndian(destination[5..], width);
        BinaryPrimitives.WriteUInt16BigEndian(destination[7..], height);
        destination[9] = prioritizeResolution ? (byte)1 : (byte)0;
        return true;
    }

    public static bool TryReadStreamRequest(
        ReadOnlySpan<byte> source,
        out byte frameRate,
        out ushort width,
        out ushort height,
        out bool prioritizeResolution)
    {
        frameRate = 0;
        width = 0;
        height = 0;
        prioritizeResolution = false;
        if (source.Length < StreamRequestSize ||
            BinaryPrimitives.ReadUInt32BigEndian(source) != StreamRequestMagic ||
            source[4] is not (30 or 60) ||
            source[9] > 1)
        {
            return false;
        }

        frameRate = source[4];
        width = BinaryPrimitives.ReadUInt16BigEndian(source[5..]);
        height = BinaryPrimitives.ReadUInt16BigEndian(source[7..]);
        prioritizeResolution = source[9] == 1;
        if ((width, height) is not (
                (1280, 720) or
                (1920, 1080) or
                (2560, 1440) or
                (3840, 2160)))
            return false;
        return true;
    }

    public static bool TryWritePacketHeader(Span<byte> destination, in VideoPacketHeader header)
    {
        if (destination.Length < PacketHeaderSize ||
            !IsKnownPacketType(header.Type) ||
            header.PayloadLength < 0 ||
            header.PayloadLength > MaxPayloadBytes)
        {
            return false;
        }

        destination[..PacketHeaderSize].Clear();
        BinaryPrimitives.WriteUInt32BigEndian(destination, PacketMagic);
        destination[4] = Version;
        destination[5] = (byte)header.Type;
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], (ushort)header.Flags);
        BinaryPrimitives.WriteInt32BigEndian(destination[8..], header.PayloadLength);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], header.Sequence);
        BinaryPrimitives.WriteInt64BigEndian(destination[16..], header.PresentationTimeMicroseconds);
        BinaryPrimitives.WriteUInt32BigEndian(destination[24..], header.DurationMicroseconds);
        return true;
    }

    public static bool TryReadPacketHeader(ReadOnlySpan<byte> source, out VideoPacketHeader header)
    {
        header = default;
        if (source.Length < PacketHeaderSize ||
            BinaryPrimitives.ReadUInt32BigEndian(source) != PacketMagic ||
            source[4] != Version)
        {
            return false;
        }

        var type = (VideoPacketType)source[5];
        var payloadLength = BinaryPrimitives.ReadInt32BigEndian(source[8..]);
        if (!IsKnownPacketType(type) || payloadLength < 0 || payloadLength > MaxPayloadBytes)
            return false;

        header = new VideoPacketHeader(
            type,
            (VideoPacketFlags)BinaryPrimitives.ReadUInt16BigEndian(source[6..]),
            payloadLength,
            BinaryPrimitives.ReadUInt32BigEndian(source[12..]),
            BinaryPrimitives.ReadInt64BigEndian(source[16..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[24..]));
        return true;
    }

    public static bool TryWriteStreamConfiguration(
        Span<byte> destination,
        in H264StreamConfiguration configuration)
    {
        if (destination.Length < StreamConfigurationSize ||
            !IsValidConfiguration(configuration))
        {
            return false;
        }

        destination[..StreamConfigurationSize].Clear();
        BinaryPrimitives.WriteUInt16BigEndian(destination, configuration.Width);
        BinaryPrimitives.WriteUInt16BigEndian(destination[2..], configuration.Height);
        BinaryPrimitives.WriteUInt16BigEndian(destination[4..], configuration.FrameRateNumerator);
        BinaryPrimitives.WriteUInt16BigEndian(destination[6..], configuration.FrameRateDenominator);
        BinaryPrimitives.WriteUInt32BigEndian(destination[8..], configuration.Bitrate);
        destination[12] = (byte)configuration.Profile;
        destination[13] = configuration.Level;
        destination[14] = configuration.BitDepth;
        destination[15] = (byte)configuration.ChromaFormat;
        BinaryPrimitives.WriteUInt16BigEndian(destination[16..], configuration.KeyFrameIntervalMilliseconds);
        BinaryPrimitives.WriteUInt16BigEndian(destination[18..], configuration.RotationDegrees);
        destination[20] = configuration.MaxBFrames;
        return true;
    }

    public static bool TryReadStreamConfiguration(
        ReadOnlySpan<byte> source,
        out H264StreamConfiguration configuration)
    {
        configuration = default;
        if (source.Length < StreamConfigurationSize)
            return false;

        var candidate = new H264StreamConfiguration(
            BinaryPrimitives.ReadUInt16BigEndian(source),
            BinaryPrimitives.ReadUInt16BigEndian(source[2..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[4..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[6..]),
            BinaryPrimitives.ReadUInt32BigEndian(source[8..]),
            (H264Profile)source[12],
            source[13],
            source[14],
            (H264ChromaFormat)source[15],
            BinaryPrimitives.ReadUInt16BigEndian(source[16..]),
            BinaryPrimitives.ReadUInt16BigEndian(source[18..]),
            source[20]);

        if (!IsValidConfiguration(candidate))
            return false;

        configuration = candidate;
        return true;
    }

    private static bool IsKnownPacketType(VideoPacketType type) =>
        type is VideoPacketType.StreamConfiguration or
            VideoPacketType.AccessUnit or
            VideoPacketType.EndOfStream;

    private static bool IsValidConfiguration(in H264StreamConfiguration configuration) =>
        configuration.Width > 0 &&
        configuration.Height > 0 &&
        configuration.FrameRateNumerator > 0 &&
        configuration.FrameRateDenominator > 0 &&
        configuration.Bitrate > 0 &&
        configuration.Profile is H264Profile.Baseline or H264Profile.Main or H264Profile.High &&
        configuration.Level > 0 &&
        configuration.BitDepth == 8 &&
        configuration.ChromaFormat == H264ChromaFormat.Yuv420 &&
        configuration.KeyFrameIntervalMilliseconds > 0 &&
        configuration.RotationDegrees is 0 or 90 or 180 or 270 &&
        configuration.MaxBFrames == 0;
}

public static class NetworkAddress
{
    public static string GetLocalIPv4Address() => Dns.GetHostEntry(Dns.GetHostName()).AddressList
        .FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))?.ToString() ?? "No Wi-Fi address found";
}
