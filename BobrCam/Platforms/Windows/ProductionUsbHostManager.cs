#if WINDOWS
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Enumeration;
using Windows.Devices.Usb;
using Windows.Storage.Streams;

namespace BobrCam;

internal sealed class ProductionUsbHostManager : IAsyncDisposable
{
    private const uint GoogleVendorId = 0x18D1;
    private const uint AccessoryProductId = 0x2D00;
    private const uint AccessoryAdbProductId = 0x2D01;
    private const int TransferBufferSize = 256 * 1024;
    private CancellationTokenSource? _cancellation;
    private Task? _monitorTask;
    private int _localReceiverPort;
    private readonly AndroidOpenAccessoryActivator _activator = new();

    public event Action<string>? StatusChanged;

    public void Start(int localReceiverPort)
    {
        if (_monitorTask is { IsCompleted: false } &&
            _localReceiverPort == localReceiverPort)
        {
            return;
        }

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _localReceiverPort = localReceiverPort;
        _cancellation = new CancellationTokenSource();
        _monitorTask = Task.Run(
            () => MonitorAsync(localReceiverPort, _cancellation.Token));
    }

    private async Task MonitorAsync(int localReceiverPort, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var device = await FindAccessoryAsync(token);
                if (device is null)
                {
                    if (await _activator.TryActivateAsync(token))
                    {
                        StatusChanged?.Invoke(
                            "USB phone detected — switching to BobrCam accessory mode…");
                        await Task.Delay(TimeSpan.FromSeconds(1), token);
                        continue;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    continue;
                }

                StatusChanged?.Invoke(
                    "Production USB accessory detected — connecting locally…");
                await BridgeAccessoryAsync(device, localReceiverPort, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(
                    $"Production USB waiting: {ex.GetBaseException().Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static async Task<UsbDevice?> FindAccessoryAsync(
        CancellationToken token)
    {
        foreach (var productId in new[]
                 {
                     AccessoryProductId,
                     AccessoryAdbProductId
                 })
        {
            var selector = UsbDevice.GetDeviceSelector(
                GoogleVendorId,
                productId);
            var devices = await DeviceInformation.FindAllAsync(selector)
                .AsTask(token);
            foreach (var deviceInfo in devices)
            {
                var device = await UsbDevice.FromIdAsync(deviceInfo.Id)
                    .AsTask(token);
                if (device is not null)
                    return device;
            }
        }
        return null;
    }

    private static async Task BridgeAccessoryAsync(
        UsbDevice device,
        int localReceiverPort,
        CancellationToken token)
    {
        var inputPipe = device.DefaultInterface.BulkInPipes.FirstOrDefault() ??
            throw new IOException("USB accessory has no bulk input endpoint.");
        var outputPipe = device.DefaultInterface.BulkOutPipes.FirstOrDefault() ??
            throw new IOException("USB accessory has no bulk output endpoint.");

        using var localClient = new TcpClient { NoDelay = true };
        await localClient.ConnectAsync(
            IPAddress.Loopback,
            localReceiverPort,
            token);
        using var bridgeCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(token);
        var network = localClient.GetStream();
        var phoneToWindows = PumpUsbToNetworkAsync(
            inputPipe.InputStream,
            network,
            bridgeCancellation.Token);
        var windowsToPhone = PumpNetworkToUsbAsync(
            network,
            outputPipe.OutputStream,
            bridgeCancellation.Token);
        await Task.WhenAny(phoneToWindows, windowsToPhone);
        bridgeCancellation.Cancel();
        try { await Task.WhenAll(phoneToWindows, windowsToPhone); }
        catch (OperationCanceledException) when (bridgeCancellation.IsCancellationRequested) { }
    }

    private static async Task PumpUsbToNetworkAsync(
        IInputStream input,
        NetworkStream output,
        CancellationToken token)
    {
        var rented = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var buffer = rented.AsBuffer(0, TransferBufferSize);
                var result = await input.ReadAsync(
                        buffer,
                        TransferBufferSize,
                        InputStreamOptions.Partial)
                    .AsTask(token);
                var count = checked((int)result.Length);
                if (count == 0)
                    throw new EndOfStreamException("USB phone disconnected.");
                await output.WriteAsync(rented.AsMemory(0, count), token);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async Task PumpNetworkToUsbAsync(
        NetworkStream input,
        IOutputStream output,
        CancellationToken token)
    {
        var rented = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            while (!token.IsCancellationRequested)
            {
                var count = await input.ReadAsync(rented.AsMemory(0, 4096), token);
                if (count == 0)
                    throw new EndOfStreamException("Local receiver disconnected.");
                var buffer = rented.AsBuffer(0, count);
                await output.WriteAsync(buffer).AsTask(token);
                await output.FlushAsync().AsTask(token);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation?.Cancel();
        if (_monitorTask is not null)
        {
            try { await _monitorTask; }
            catch (OperationCanceledException) { }
        }
        _cancellation?.Dispose();
        _cancellation = null;
        _monitorTask = null;
    }
}
#endif
