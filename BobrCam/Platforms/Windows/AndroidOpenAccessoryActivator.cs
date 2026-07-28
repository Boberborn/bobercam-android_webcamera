#if WINDOWS
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Windows.Devices.Enumeration;
using Windows.Devices.Usb;
using Windows.Storage.Streams;

namespace BobrCam;

internal sealed class AndroidOpenAccessoryActivator
{
    private const byte GetProtocolRequest = 51;
    private const byte SendStringRequest = 52;
    private const byte StartAccessoryRequest = 53;
    private const ushort MinimumSupportedProtocol = 1;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    // Android ADB, WinUSB, and the public/private WPD interfaces used by MTP.
    // FromIdAsync simply returns null when the active driver does not expose raw
    // USB access. A future signed BobrCam filter can expose its interface through
    // the same UsbDevice API without changing the AOA protocol implementation.
    private static readonly Guid[] CandidateInterfaceClasses =
    [
        new("F72FE0D4-CBCB-407D-8814-9ED673D0DD6B"),
        new("88BAE032-5A81-49F0-BC3D-A4FF138216D6"),
        new("6AC27878-A6FA-4155-BA85-F98F491D4F33"),
        new("BA0C718F-4DED-49B7-BDD3-FABE28661211")
    ];

    private static readonly string[] IdentificationStrings =
    [
        "BobrCam",
        "BobrCam USB",
        "Android camera for BobrCam",
        "1.0",
        "https://github.com/Boberborn/bobercam",
        "BobrCam"
    ];

    private readonly Dictionary<string, DateTimeOffset> _lastAttempts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly BobrCamUsbFilterClient _filterClient = new();

    public async Task<bool> TryActivateAsync(CancellationToken token)
    {
        if (await _filterClient.TryActivateAsync(token))
            return true;

        foreach (var interfaceClass in CandidateInterfaceClasses)
        {
            var selector = UsbDevice.GetDeviceSelector(interfaceClass);
            var candidates = await DeviceInformation.FindAllAsync(selector)
                .AsTask(token);

            foreach (var candidate in candidates)
            {
                if (ShouldWaitBeforeRetry(candidate.Id))
                    continue;

                _lastAttempts[candidate.Id] = DateTimeOffset.UtcNow;
                using var device = await UsbDevice.FromIdAsync(candidate.Id)
                    .AsTask(token);
                if (device is null)
                    continue;

                var descriptor = device.DeviceDescriptor;
                if (descriptor.VendorId == 0x18D1 &&
                    descriptor.ProductId is 0x2D00 or 0x2D01)
                {
                    continue;
                }

                try
                {
                    await StartAccessoryModeAsync(device, token);
                    return true;
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Another candidate interface may permit device-recipient
                    // control transfers even when this one does not.
                }
            }
        }

        RemoveExpiredAttempts();
        return false;
    }

    private bool ShouldWaitBeforeRetry(string id) =>
        _lastAttempts.TryGetValue(id, out var attemptedAt) &&
        DateTimeOffset.UtcNow - attemptedAt < RetryDelay;

    private void RemoveExpiredAttempts()
    {
        var cutoff = DateTimeOffset.UtcNow - RetryDelay;
        foreach (var id in _lastAttempts
                     .Where(pair => pair.Value < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _lastAttempts.Remove(id);
        }
    }

    private static async Task StartAccessoryModeAsync(
        UsbDevice device,
        CancellationToken token)
    {
        var protocolBuffer = new Windows.Storage.Streams.Buffer(2);
        var protocolResult = await device.SendControlInTransferAsync(
                CreateSetupPacket(
                    UsbTransferDirection.In,
                    GetProtocolRequest,
                    length: 2),
                protocolBuffer)
            .AsTask(token);
        if (protocolResult.Length != 2)
            throw new IOException("Android device did not return its AOA protocol.");

        using var reader = DataReader.FromBuffer(protocolResult);
        reader.ByteOrder = ByteOrder.LittleEndian;
        var protocol = reader.ReadUInt16();
        if (protocol < MinimumSupportedProtocol)
            throw new IOException($"Android device reports unsupported AOA protocol {protocol}.");

        for (ushort index = 0; index < IdentificationStrings.Length; index++)
        {
            var bytes = Encoding.UTF8.GetBytes(
                IdentificationStrings[index] + '\0');
            var written = await device.SendControlOutTransferAsync(
                    CreateSetupPacket(
                        UsbTransferDirection.Out,
                        SendStringRequest,
                        index: index,
                        length: checked((ushort)bytes.Length)),
                    bytes.AsBuffer())
                .AsTask(token);
            if (written != bytes.Length)
                throw new IOException("Android device rejected an AOA identity string.");
        }

        await device.SendControlOutTransferAsync(
                CreateSetupPacket(
                    UsbTransferDirection.Out,
                    StartAccessoryRequest))
            .AsTask(token);
    }

    private static UsbSetupPacket CreateSetupPacket(
        UsbTransferDirection direction,
        byte request,
        ushort index = 0,
        ushort length = 0) =>
        new()
        {
            RequestType = new UsbControlRequestType
            {
                Direction = direction,
                ControlTransferType = UsbControlTransferType.Vendor,
                Recipient = UsbControlRecipient.Device
            },
            Request = request,
            Value = 0,
            Index = index,
            Length = length
        };
}
#endif
