#if WINDOWS
using Microsoft.UI.Xaml.Controls;

namespace BobrCam;

public class CameraPreviewHandler : Microsoft.Maui.Handlers.ViewHandler<CameraPreview, Microsoft.UI.Xaml.Controls.Grid>
{
    public static readonly PropertyMapper<CameraPreview> CameraPreviewPropertyMapper = new();

    public CameraPreviewHandler() : base(CameraPreviewPropertyMapper) { }

    protected override Microsoft.UI.Xaml.Controls.Grid CreatePlatformView() => new Microsoft.UI.Xaml.Controls.Grid();
}
#endif