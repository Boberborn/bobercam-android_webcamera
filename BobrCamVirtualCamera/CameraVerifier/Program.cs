using Windows.Devices.Enumeration;
using Windows.Media.Capture;

var cameras = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
foreach (var camera in cameras)
{
    Console.WriteLine($"{camera.Name}|{camera.IsEnabled}|{camera.Id}");
}

var bobrCam = cameras.FirstOrDefault(camera =>
    camera.Name.Contains("BobrCam", StringComparison.OrdinalIgnoreCase));
if (bobrCam is null)
    return 2;

if (args.Contains("--activate", StringComparer.OrdinalIgnoreCase))
{
    using var capture = new MediaCapture();
    await capture.InitializeAsync(new MediaCaptureInitializationSettings
    {
        VideoDeviceId = bobrCam.Id,
        StreamingCaptureMode = StreamingCaptureMode.Video,
        SharingMode = MediaCaptureSharingMode.SharedReadOnly,
        MemoryPreference = MediaCaptureMemoryPreference.Cpu
    });
    Console.WriteLine("BobrCam activation succeeded.");
}

return 0;
