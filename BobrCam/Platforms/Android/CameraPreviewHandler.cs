#if ANDROID
using Android.Content;
using Android.Views;

namespace BobrCam;

public class CameraPreviewHandler : Microsoft.Maui.Handlers.ViewHandler<CameraPreview, AspectRatioTextureView>
{
    public static readonly PropertyMapper<CameraPreview> CameraPreviewPropertyMapper = new();

    public CameraPreviewHandler() : base(CameraPreviewPropertyMapper) { }

    protected override AspectRatioTextureView CreatePlatformView()
        => new(Context!);
}

public sealed class AspectRatioTextureView : TextureView
{
    private int _targetW = 640;
    private int _targetH = 480;
    private bool _aspectSet;

    public AspectRatioTextureView(Context context) : base(context)
    {
        LayoutParameters = new ViewGroup.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.MatchParent);
    }

    public void SetTargetAspect(int w, int h)
    {
        if (w <= 0 || h <= 0) return;
        _targetW = w;
        _targetH = h;
        _aspectSet = true;
        RequestLayout();
    }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        base.OnMeasure(widthMeasureSpec, heightMeasureSpec);
        if (!_aspectSet) return;

        int parentW = MeasuredWidth;
        int parentH = MeasuredHeight;

        var targetAR = (double)_targetW / _targetH;
        var parentAR = (double)parentW / parentH;

        int newW, newH;
        if (parentAR > targetAR)
        {
            newH = parentH;
            newW = (int)(parentH * targetAR + 0.5);
        }
        else
        {
            newW = parentW;
            newH = (int)(parentW / targetAR + 0.5);
        }
        SetMeasuredDimension(newW, newH);
    }
}
#endif
