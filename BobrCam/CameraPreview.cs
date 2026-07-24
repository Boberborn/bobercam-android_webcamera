namespace BobrCam;

public class CameraPreview : View
{
    public event Action? SurfaceReady;
    public void OnSurfaceReady() => SurfaceReady?.Invoke();
}