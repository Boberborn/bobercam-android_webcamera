using BobrCam;

Span<byte> headerBytes = stackalloc byte[VideoProtocol.PacketHeaderSize];
var expectedHeader = new VideoPacketHeader(
    VideoPacketType.AccessUnit,
    VideoPacketFlags.KeyFrame,
    512_000,
    42,
    1_500_000,
    16_667);

Require(VideoProtocol.TryWritePacketHeader(headerBytes, expectedHeader), "Header write failed.");
Require(VideoProtocol.TryReadPacketHeader(headerBytes, out var actualHeader), "Header read failed.");
Require(actualHeader == expectedHeader, "Header round trip changed data.");

headerBytes[4] = 99;
Require(!VideoProtocol.TryReadPacketHeader(headerBytes, out _), "Unknown version was accepted.");

Span<byte> configurationBytes = stackalloc byte[VideoProtocol.StreamConfigurationSize];
var expectedConfiguration = new H264StreamConfiguration(
    1920,
    1080,
    60,
    1,
    16_000_000,
    H264Profile.Main,
    42,
    8,
    H264ChromaFormat.Yuv420,
    1000,
    0,
    0);

Require(
    VideoProtocol.TryWriteStreamConfiguration(configurationBytes, expectedConfiguration),
    "Configuration write failed.");
Require(
    VideoProtocol.TryReadStreamConfiguration(configurationBytes, out var actualConfiguration),
    "Configuration read failed.");
Require(actualConfiguration == expectedConfiguration, "Configuration round trip changed data.");

configurationBytes[20] = 1;
Require(
    !VideoProtocol.TryReadStreamConfiguration(configurationBytes, out _),
    "Configuration with B-frames was accepted.");

Console.WriteLine("BobrCam H.264 protocol contract tests passed.");

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
