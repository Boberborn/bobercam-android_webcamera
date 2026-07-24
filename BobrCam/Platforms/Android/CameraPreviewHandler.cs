#if ANDROID
using Android.Content;
using Android.Graphics;
using Android.Widget;
using Android.Views;

namespace BobrCam;

public class CameraPreviewHandler : Microsoft.Maui.Handlers.ViewHandler<CameraPreview, AspectRatioTextureView>
{
    public static readonly PropertyMapper<CameraPreview> CameraPreviewPropertyMapper = new();

    public CameraPreviewHandler() : base(CameraPreviewPropertyMapper) { }

    protected override AspectRatioTextureView CreatePlatformView()
    {
        var tv = new AspectRatioTextureView(Context!);
        tv.SurfaceTextureListener = new SurfaceListener(tv, this);
        return tv;
    }

    internal void OnSurfaceReady()
    {
        VirtualView?.OnSurfaceReady();
    }

    private sealed class SurfaceListener : Java.Lang.Object, TextureView.ISurfaceTextureListener
    {
        private readonly AspectRatioTextureView _textureView;
        private readonly CameraPreviewHandler _handler;

        public SurfaceListener(AspectRatioTextureView textureView, CameraPreviewHandler handler)
        {
            _textureView = textureView;
            _handler = handler;
        }

        public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
        {
            AndroidCameraStreamer.SetPreviewSurface(surface, (degrees, w, h) =>
            {
                var ctx = _textureView.Context;
                if (ctx is Android.Content.Context c)
                {
                    var wm = c.GetSystemService(Android.Content.Context.WindowService)
                        as Android.Views.IWindowManager;
                    if (wm is not null)
                    {
                        var rot = wm.DefaultDisplay?.Rotation ?? Android.Views.SurfaceOrientation.Rotation0;
                        int displayRotation = rot switch
                        {
                            Android.Views.SurfaceOrientation.Rotation90 => 90,
                            Android.Views.SurfaceOrientation.Rotation180 => 180,
                            Android.Views.SurfaceOrientation.Rotation270 => 270,
                            _ => 0
                        };
                        AndroidCameraStreamer.ApplyDisplayOrientation(displayRotation);
                        AndroidCameraStreamer.GetRotatedAspect(out var rw, out var rh);
                        _textureView.SetTargetAspect(rw, rh);
                    }
                }
            });
            _handler.OnSurfaceReady();
        }

        public bool OnSurfaceTextureDestroyed(SurfaceTexture surface)
        {
            AndroidCameraStreamer.SetPreviewSurface(null, null);
            return true;
        }

        public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
        public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }
    }
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