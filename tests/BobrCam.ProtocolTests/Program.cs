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

var requestBytes = new byte[VideoProtocol.StreamRequestSize];
Require(
    VideoProtocol.TryWriteStreamRequest(requestBytes, 30, 1280, 720, false, false),
    "Valid stream request was rejected.");
Require(
    VideoProtocol.TryReadStreamRequest(requestBytes, out _, out _, out _, out _, out _),
    "Valid stream request read failed.");
Require(
    !VideoProtocol.TryWriteStreamRequest(requestBytes, 30, 2560, 720, false, false),
    "Mismatched resolution pair was accepted for write.");
requestBytes[5] = 0x0A;
requestBytes[6] = 0x00;
requestBytes[7] = 0x02;
requestBytes[8] = 0xD0;
Require(
    !VideoProtocol.TryReadStreamRequest(requestBytes, out _, out _, out _, out _, out _),
    "Mismatched resolution pair was accepted for read.");

Console.WriteLine("BobrCam H.264 protocol contract tests passed.");

static void Require(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
