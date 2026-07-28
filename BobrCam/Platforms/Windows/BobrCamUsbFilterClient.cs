#if WINDOWS
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace BobrCam;

internal sealed class BobrCamUsbFilterClient
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint IoctlStartAccessory = 0x0022E000;
    private static readonly Guid FilterInterfaceGuid =
        new("A2C43F18-7E80-46E7-B9B9-5D372D00B861");
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly Dictionary<string, DateTimeOffset> _lastAttempts =
        new(StringComparer.OrdinalIgnoreCase);

    public Task<bool> TryActivateAsync(CancellationToken token) =>
        Task.Run(() => TryActivate(token), token);

    private bool TryActivate(CancellationToken token)
    {
        foreach (var path in EnumerateDevicePaths())
        {
            token.ThrowIfCancellationRequested();
            if (_lastAttempts.TryGetValue(path, out var attemptedAt) &&
                DateTimeOffset.UtcNow - attemptedAt < RetryDelay)
            {
                continue;
            }

            _lastAttempts[path] = DateTimeOffset.UtcNow;
            using var handle = CreateFile(
                path,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                0,
                IntPtr.Zero);
            if (handle.IsInvalid)
                continue;

            if (DeviceIoControl(
                    handle,
                    IoctlStartAccessory,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero,
                    0,
                    out _,
                    IntPtr.Zero))
            {
                return true;
            }

            var error = Marshal.GetLastWin32Error();
            if (error is 1167 or 995)
            {
                // A successful AOA switch removes the original USB device
                // immediately, so the final control request can complete as
                // ERROR_DEVICE_NOT_CONNECTED or ERROR_OPERATION_ABORTED.
                return true;
            }
        }

        RemoveExpiredAttempts();
        return false;
    }

    private void RemoveExpiredAttempts()
    {
        var cutoff = DateTimeOffset.UtcNow - RetryDelay;
        foreach (var path in _lastAttempts
                     .Where(pair => pair.Value < cutoff)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _lastAttempts.Remove(path);
        }
    }

    private static IEnumerable<string> EnumerateDevicePaths()
    {
        var interfaceGuid = FilterInterfaceGuid;
        var deviceInfoSet = SetupDiGetClassDevs(
            ref interfaceGuid,
            null,
            IntPtr.Zero,
            DigcfPresent | DigcfDeviceInterface);
        if (deviceInfoSet == new IntPtr(-1))
            yield break;

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    Size = checked((uint)Marshal.SizeOf<SpDeviceInterfaceData>())
                };
                if (!SetupDiEnumDeviceInterfaces(
                        deviceInfoSet,
                        IntPtr.Zero,
                        ref interfaceGuid,
                        index,
                        ref interfaceData))
                {
                    if (Marshal.GetLastWin32Error() == 259)
                        yield break;
                    continue;
                }

                SetupDiGetDeviceInterfaceDetail(
                    deviceInfoSet,
                    ref interfaceData,
                    IntPtr.Zero,
                    0,
                    out var requiredSize,
                    IntPtr.Zero);
                if (requiredSize == 0)
                    continue;

                var detailBuffer = Marshal.AllocHGlobal(checked((int)requiredSize));
                try
                {
                    Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(
                            deviceInfoSet,
                            ref interfaceData,
                            detailBuffer,
                            requiredSize,
                            out _,
                            IntPtr.Zero))
                    {
                        continue;
                    }

                    var path = Marshal.PtrToStringUni(
                        IntPtr.Add(detailBuffer, sizeof(uint)));
                    if (!string.IsNullOrWhiteSpace(path))
                        yield return path;
                }
                finally
                {
                    Marshal.FreeHGlobal(detailBuffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint Size;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(
        ref Guid classGuid,
        string? enumerator,
        IntPtr parentWindow,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(
        IntPtr deviceInfoSet);

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inputBuffer,
        uint inputBufferSize,
        IntPtr outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);
}
#endif
