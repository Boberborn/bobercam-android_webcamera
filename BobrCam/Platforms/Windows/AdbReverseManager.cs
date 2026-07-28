#if WINDOWS
using System.Diagnostics;

namespace BobrCam;

internal sealed class AdbReverseManager : IAsyncDisposable
{
    private CancellationTokenSource? _cancellation;
    private Task? _monitorTask;
    private int _devicePort;
    private int _hostPort;

    public void Start(int devicePort, int hostPort)
    {
        if (_monitorTask is { IsCompleted: false } &&
            _devicePort == devicePort &&
            _hostPort == hostPort)
            return;

        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _devicePort = devicePort;
        _hostPort = hostPort;
        _cancellation = new CancellationTokenSource();
        _monitorTask = Task.Run(
            () => MonitorAsync(devicePort, hostPort, _cancellation.Token));
    }

    private static async Task MonitorAsync(
        int devicePort,
        int hostPort,
        CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var devices = await GetConnectedDevicesAsync(token);
                foreach (var serial in devices)
                {
                    await EnsureReverseRuleAsync(
                        serial,
                        devicePort,
                        hostPort,
                        token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"BobrCam USB monitor: {exception.Message}");
            }

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

    private static async Task<string[]> GetConnectedDevicesAsync(
        CancellationToken token)
    {
        var result = await RunAdbAsync(["devices"], token);
        if (result.ExitCode != 0)
            return [];

        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length == 2 && parts[1] == "device")
            .Select(parts => parts[0])
            .Where(serial => !string.IsNullOrWhiteSpace(serial))
            .ToArray();
    }

    private static async Task EnsureReverseRuleAsync(
        string serial,
        int devicePort,
        int hostPort,
        CancellationToken token)
    {
        var deviceEndpoint = $"tcp:{devicePort}";
        var hostEndpoint = $"tcp:{hostPort}";
        var list = await RunAdbAsync(["-s", serial, "reverse", "--list"], token);
        if (list.ExitCode == 0 &&
            list.StandardOutput.Contains(
                $"{deviceEndpoint} {hostEndpoint}",
                StringComparison.Ordinal))
        {
            return;
        }

        var apply = await RunAdbAsync(
            ["-s", serial, "reverse", deviceEndpoint, hostEndpoint],
            token);
        if (apply.ExitCode != 0)
            Debug.WriteLine(
                $"BobrCam could not configure USB for {serial}: {apply.StandardError}");
    }

    private static async Task<AdbResult> RunAdbAsync(
        IReadOnlyList<string> arguments,
        CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FindAdbExecutable(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(token);
        var errorTask = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        return new AdbResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static string FindAdbExecutable()
    {
        foreach (var root in new[]
                 {
                     Environment.GetEnvironmentVariable("ANDROID_HOME"),
                     Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
                     Path.Combine(
                         Environment.GetFolderPath(
                             Environment.SpecialFolder.LocalApplicationData),
                         "Android",
                         "Sdk")
                 })
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            var candidate = Path.Combine(root, "platform-tools", "adb.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return "adb.exe";
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation?.Cancel();
        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _cancellation?.Dispose();
        _cancellation = null;
        _monitorTask = null;
    }

    private readonly record struct AdbResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
#endif
