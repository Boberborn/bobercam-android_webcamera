#if ANDROID
using Android.Graphics;
using Android.Opengl;
using Android.OS;
using Android.Views;
using Java.Nio;

namespace BobrCam;

internal sealed class AndroidGpuVideoRenderer : IDisposable
{
    private const int EglRecordableAndroid = 0x3142;
    private const int EglOpenGles2Bit = 4;
    private readonly HandlerThread _thread;
    private readonly Handler _handler;
    private readonly object _settingsGate = new();
    private readonly int _width;
    private readonly int _height;
    private EGLDisplay? _display;
    private EGLContext? _context;
    private EGLSurface? _windowSurface;
    private SurfaceTexture? _cameraTexture;
    private int _cameraTextureId;
    private int _program;
    private int _positionLocation;
    private int _textureCoordinateLocation;
    private int _texelLocation;
    private int _modeLocation;
    private int _smoothnessLocation;
    private int _brightnessLocation;
    private int _warmthLocation;
    private int _vignetteLocation;
    private int _faceRectLocation;
    private int _maskStrengthLocation;
    private FloatBuffer? _vertexBuffer;
    private VideoEffectMode _mode;
    private float _smoothness = 0.35f;
    private float _brightness;
    private float _warmth;
    private float _vignette;
    private float _maskStrength = 0.9f;
    private float _faceLeft;
    private float _faceTop;
    private float _faceRight;
    private float _faceBottom;
    private bool _disposed;

    public Surface InputSurface { get; private set; } = null!;

    public AndroidGpuVideoRenderer(
        Surface encoderSurface,
        int width,
        int height)
    {
        ArgumentNullException.ThrowIfNull(encoderSurface);
        _width = width;
        _height = height;
        _thread = new HandlerThread("BobrCam.GpuEffects");
        _thread.Start();
        _handler = new Handler(_thread.Looper!);

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Post(() =>
        {
            try
            {
                Initialize(encoderSurface);
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });
        completion.Task.Wait(TimeSpan.FromSeconds(8));
        if (!completion.Task.IsCompletedSuccessfully)
        {
            Dispose();
            throw completion.Task.Exception?.GetBaseException() ??
                new InvalidOperationException("Phone GPU compositor did not start.");
        }
    }

    public void SetSettings(
        VideoEffectMode mode,
        int smoothness,
        int brightness,
        int warmth,
        int vignette,
        int maskStrength)
    {
        lock (_settingsGate)
        {
            _mode = mode;
            _smoothness = Math.Clamp(smoothness / 100f, 0f, 1f);
            _brightness = Math.Clamp(brightness / 50f, -1f, 1f);
            _warmth = Math.Clamp(warmth / 50f, -1f, 1f);
            _vignette = Math.Clamp(vignette / 100f, 0f, 1f);
            _maskStrength = Math.Clamp(maskStrength / 100f, 0f, 1f);
        }
    }

    public void SetFaceRect(float left, float top, float right, float bottom)
    {
        lock (_settingsGate)
        {
            _faceLeft = Math.Clamp(left, 0f, 1f);
            _faceTop = Math.Clamp(top, 0f, 1f);
            _faceRight = Math.Clamp(right, 0f, 1f);
            _faceBottom = Math.Clamp(bottom, 0f, 1f);
        }
    }

    public void ClearFace()
    {
        lock (_settingsGate)
            _faceLeft = _faceTop = _faceRight = _faceBottom = 0f;
    }

    private void Initialize(Surface encoderSurface)
    {
        _display = EGL14.EglGetDisplay(EGL14.EglDefaultDisplay);
        if (_display == EGL14.EglNoDisplay)
            throw new InvalidOperationException("EGL display is unavailable.");

        var versions = new int[2];
        if (!EGL14.EglInitialize(_display, versions, 0, versions, 1))
            throw new InvalidOperationException("Could not initialize EGL.");

        var attributes = new[]
        {
            EGL14.EglRedSize, 8,
            EGL14.EglGreenSize, 8,
            EGL14.EglBlueSize, 8,
            EGL14.EglAlphaSize, 8,
            EGL14.EglRenderableType, EglOpenGles2Bit,
            EglRecordableAndroid, 1,
            EGL14.EglNone
        };
        var configs = new EGLConfig[1];
        var configCount = new int[1];
        if (!EGL14.EglChooseConfig(
                _display,
                attributes,
                0,
                configs,
                0,
                configs.Length,
                configCount,
                0) ||
            configCount[0] == 0)
        {
            throw new InvalidOperationException(
                "No recordable OpenGL ES configuration is available.");
        }

        var contextAttributes = new[]
        {
            EGL14.EglContextClientVersion, 2,
            EGL14.EglNone
        };
        _context = EGL14.EglCreateContext(
            _display,
            configs[0],
            EGL14.EglNoContext,
            contextAttributes,
            0);
        if (_context == EGL14.EglNoContext)
            throw new InvalidOperationException("Could not create the OpenGL ES context.");

        var surfaceAttributes = new[] { EGL14.EglNone };
        var surfaceError = EGL14.EglSuccess;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            _windowSurface = EGL14.EglCreateWindowSurface(
                _display,
                configs[0],
                encoderSurface,
                surfaceAttributes,
                0);
            if (_windowSurface != EGL14.EglNoSurface)
                break;
            surfaceError = EGL14.EglGetError();
            if (surfaceError is not (0x3003 or 0x300B))
                break;
            System.Threading.Thread.Sleep(100);
        }
        if (_windowSurface == EGL14.EglNoSurface)
        {
            throw new InvalidOperationException(
                $"Could not create the MediaCodec EGL surface (0x{surfaceError:X}).");
        }
        if (!EGL14.EglMakeCurrent(
                _display,
                _windowSurface,
                _windowSurface,
                _context))
        {
            throw new InvalidOperationException(
                $"Could not attach the phone GPU to MediaCodec (0x{EGL14.EglGetError():X}).");
        }

        var textureIds = new int[1];
        GLES20.GlGenTextures(1, textureIds, 0);
        _cameraTextureId = textureIds[0];
        GLES20.GlBindTexture(GLES11Ext.GlTextureExternalOes, _cameraTextureId);
        GLES20.GlTexParameteri(
            GLES11Ext.GlTextureExternalOes,
            GLES20.GlTextureMinFilter,
            GLES20.GlLinear);
        GLES20.GlTexParameteri(
            GLES11Ext.GlTextureExternalOes,
            GLES20.GlTextureMagFilter,
            GLES20.GlLinear);
        GLES20.GlTexParameteri(
            GLES11Ext.GlTextureExternalOes,
            GLES20.GlTextureWrapS,
            GLES20.GlClampToEdge);
        GLES20.GlTexParameteri(
            GLES11Ext.GlTextureExternalOes,
            GLES20.GlTextureWrapT,
            GLES20.GlClampToEdge);

        _cameraTexture = new SurfaceTexture(_cameraTextureId);
        _cameraTexture.SetDefaultBufferSize(_width, _height);
        _cameraTexture.SetOnFrameAvailableListener(
            new FrameListener(this),
            _handler);
        InputSurface = new Surface(_cameraTexture);

        _program = CreateProgram(VertexShader, FragmentShader);
        _positionLocation = GLES20.GlGetAttribLocation(_program, "aPosition");
        _textureCoordinateLocation =
            GLES20.GlGetAttribLocation(_program, "aTextureCoordinate");
        _texelLocation = GLES20.GlGetUniformLocation(_program, "uTexel");
        _modeLocation = GLES20.GlGetUniformLocation(_program, "uMode");
        _smoothnessLocation =
            GLES20.GlGetUniformLocation(_program, "uSmoothness");
        _brightnessLocation =
            GLES20.GlGetUniformLocation(_program, "uBrightness");
        _warmthLocation = GLES20.GlGetUniformLocation(_program, "uWarmth");
        _vignetteLocation = GLES20.GlGetUniformLocation(_program, "uVignette");
        _faceRectLocation = GLES20.GlGetUniformLocation(_program, "uFaceRect");
        _maskStrengthLocation =
            GLES20.GlGetUniformLocation(_program, "uMaskStrength");

        var vertices = new float[]
        {
            -1f, -1f, 0f, 0f,
             1f, -1f, 1f, 0f,
            -1f,  1f, 0f, 1f,
             1f,  1f, 1f, 1f
        };
        _vertexBuffer = ByteBuffer
            .AllocateDirect(vertices.Length * sizeof(float))
            .Order(ByteOrder.NativeOrder()!)
            .AsFloatBuffer();
        _vertexBuffer.Put(vertices);
        _vertexBuffer.Position(0);
    }

    private void RenderFrame()
    {
        if (_disposed ||
            _display is null ||
            _windowSurface is null ||
            _cameraTexture is null ||
            _vertexBuffer is null)
        {
            return;
        }

        try
        {
            _cameraTexture.UpdateTexImage();

            VideoEffectMode mode;
            float smoothness;
            float brightness;
            float warmth;
            float vignette;
            float maskStrength;
            float faceLeft;
            float faceTop;
            float faceRight;
            float faceBottom;
            lock (_settingsGate)
            {
                mode = _mode;
                smoothness = _smoothness;
                brightness = _brightness;
                warmth = _warmth;
                vignette = _vignette;
                maskStrength = _maskStrength;
                faceLeft = _faceLeft;
                faceTop = _faceTop;
                faceRight = _faceRight;
                faceBottom = _faceBottom;
            }

            GLES20.GlViewport(0, 0, _width, _height);
            GLES20.GlUseProgram(_program);
            GLES20.GlActiveTexture(GLES20.GlTexture0);
            GLES20.GlBindTexture(
                GLES11Ext.GlTextureExternalOes,
                _cameraTextureId);

            _vertexBuffer.Position(0);
            GLES20.GlEnableVertexAttribArray(_positionLocation);
            GLES20.GlVertexAttribPointer(
                _positionLocation,
                2,
                GLES20.GlFloat,
                false,
                4 * sizeof(float),
                _vertexBuffer);
            _vertexBuffer.Position(2);
            GLES20.GlEnableVertexAttribArray(_textureCoordinateLocation);
            GLES20.GlVertexAttribPointer(
                _textureCoordinateLocation,
                2,
                GLES20.GlFloat,
                false,
                4 * sizeof(float),
                _vertexBuffer);

            GLES20.GlUniform2f(
                _texelLocation,
                1f / _width,
                1f / _height);
            GLES20.GlUniform1i(_modeLocation, (int)mode);
            GLES20.GlUniform1f(_smoothnessLocation, smoothness);
            GLES20.GlUniform1f(_brightnessLocation, brightness);
            GLES20.GlUniform1f(_warmthLocation, warmth);
            GLES20.GlUniform1f(_vignetteLocation, vignette);
            GLES20.GlUniform4f(
                _faceRectLocation,
                faceLeft,
                faceTop,
                faceRight,
                faceBottom);
            GLES20.GlUniform1f(_maskStrengthLocation, maskStrength);
            GLES20.GlDrawArrays(GLES20.GlTriangleStrip, 0, 4);

            EGLExt.EglPresentationTimeANDROID(
                _display,
                _windowSurface,
                _cameraTexture.Timestamp);
            EGL14.EglSwapBuffers(_display, _windowSurface);
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error(
                "BobrCam",
                $"GPU effect frame failed: {ex.GetBaseException().Message}");
        }
    }

    private static int CreateProgram(string vertexSource, string fragmentSource)
    {
        var vertex = CompileShader(GLES20.GlVertexShader, vertexSource);
        var fragment = CompileShader(GLES20.GlFragmentShader, fragmentSource);
        var program = GLES20.GlCreateProgram();
        GLES20.GlAttachShader(program, vertex);
        GLES20.GlAttachShader(program, fragment);
        GLES20.GlLinkProgram(program);
        var linked = new int[1];
        GLES20.GlGetProgramiv(program, GLES20.GlLinkStatus, linked, 0);
        GLES20.GlDeleteShader(vertex);
        GLES20.GlDeleteShader(fragment);
        if (linked[0] == 0)
        {
            var error = GLES20.GlGetProgramInfoLog(program);
            GLES20.GlDeleteProgram(program);
            throw new InvalidOperationException(
                $"Could not link the beauty/mask shader: {error}");
        }
        return program;
    }

    private static int CompileShader(int type, string source)
    {
        var shader = GLES20.GlCreateShader(type);
        GLES20.GlShaderSource(shader, source);
        GLES20.GlCompileShader(shader);
        var compiled = new int[1];
        GLES20.GlGetShaderiv(shader, GLES20.GlCompileStatus, compiled, 0);
        if (compiled[0] != 0)
            return shader;
        var error = GLES20.GlGetShaderInfoLog(shader);
        GLES20.GlDeleteShader(shader);
        throw new InvalidOperationException($"Could not compile GPU shader: {error}");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.Post(() =>
        {
            try
            {
                InputSurface?.Dispose();
                _cameraTexture?.Release();
                _cameraTexture?.Dispose();
                if (_program != 0)
                    GLES20.GlDeleteProgram(_program);
                if (_cameraTextureId != 0)
                    GLES20.GlDeleteTextures(1, new[] { _cameraTextureId }, 0);
                if (_display is not null)
                {
                    EGL14.EglMakeCurrent(
                        _display,
                        EGL14.EglNoSurface,
                        EGL14.EglNoSurface,
                        EGL14.EglNoContext);
                    if (_windowSurface is not null)
                        EGL14.EglDestroySurface(_display, _windowSurface);
                    if (_context is not null)
                        EGL14.EglDestroyContext(_display, _context);
                    EGL14.EglReleaseThread();
                    EGL14.EglTerminate(_display);
                }
            }
            finally
            {
                completion.TrySetResult();
            }
        });
        try { completion.Task.Wait(TimeSpan.FromSeconds(3)); }
        catch { }
        _thread.QuitSafely();
        try { _thread.Join(1000); }
        catch { }
        _handler.Dispose();
        _thread.Dispose();
    }

    private sealed class FrameListener(AndroidGpuVideoRenderer owner)
        : Java.Lang.Object, SurfaceTexture.IOnFrameAvailableListener
    {
        public void OnFrameAvailable(SurfaceTexture? surfaceTexture) =>
            owner.RenderFrame();
    }

    private const string VertexShader = """
        attribute vec4 aPosition;
        attribute vec2 aTextureCoordinate;
        varying vec2 vTextureCoordinate;
        void main() {
            gl_Position = aPosition;
            vTextureCoordinate = vec2(
                aTextureCoordinate.x,
                1.0 - aTextureCoordinate.y);
        }
        """;

    private const string FragmentShader = """
        #extension GL_OES_EGL_image_external : require
        precision mediump float;
        uniform samplerExternalOES uTexture;
        uniform vec2 uTexel;
        uniform int uMode;
        uniform float uSmoothness;
        uniform float uBrightness;
        uniform float uWarmth;
        uniform float uVignette;
        uniform vec4 uFaceRect;
        uniform float uMaskStrength;
        varying vec2 vTextureCoordinate;

        float ellipse(vec2 point, vec2 center, vec2 radius) {
            vec2 value = (point - center) / radius;
            return 1.0 - smoothstep(0.92, 1.0, dot(value, value));
        }

        void main() {
            vec2 uv = vTextureCoordinate;
            vec3 color = texture2D(uTexture, uv).rgb;

            if (uMode == 1) {
                vec2 sampleOffset = uTexel * 1.35;
                vec3 blur = (
                    color * 2.0 +
                    texture2D(uTexture, uv + sampleOffset).rgb +
                    texture2D(uTexture, uv - sampleOffset).rgb) * 0.25;
                float edge = clamp(length(color - blur) * 5.0, 0.0, 1.0);
                color = mix(color, blur, uSmoothness * (1.0 - edge) * 0.78);
                color += uBrightness * 0.16;
                color.r += uWarmth * 0.09;
                color.b -= uWarmth * 0.06;
                float distanceFromCenter =
                    length((uv - vec2(0.5)) * vec2(1.0, 0.82));
                float vignette = smoothstep(0.28, 0.72, distanceFromCenter);
                color *= 1.0 - vignette * uVignette * 0.38;
            } else if (uMode == 2 &&
                       uFaceRect.z > uFaceRect.x &&
                       uFaceRect.w > uFaceRect.y) {
                vec2 center = vec2(
                    (uFaceRect.x + uFaceRect.z) * 0.5,
                    (uFaceRect.y + uFaceRect.w) * 0.5);
                vec2 size = vec2(
                    uFaceRect.z - uFaceRect.x,
                    uFaceRect.w - uFaceRect.y);
                size *= vec2(0.72, 0.80);
                float head = ellipse(uv, center, size);
                float leftEar = ellipse(
                    uv,
                    center + vec2(-size.x * 0.78, -size.y * 0.72),
                    size * vec2(0.35, 0.30));
                float rightEar = ellipse(
                    uv,
                    center + vec2(size.x * 0.78, -size.y * 0.72),
                    size * vec2(0.35, 0.30));
                float muzzle = ellipse(
                    uv,
                    center + vec2(0.0, size.y * 0.30),
                    size * vec2(0.58, 0.43));
                float nose = ellipse(
                    uv,
                    center + vec2(0.0, size.y * 0.12),
                    size * vec2(0.19, 0.14));
                float leftEye = ellipse(
                    uv,
                    center + vec2(-size.x * 0.33, -size.y * 0.18),
                    size * vec2(0.10, 0.13));
                float rightEye = ellipse(
                    uv,
                    center + vec2(size.x * 0.33, -size.y * 0.18),
                    size * vec2(0.10, 0.13));
                float teeth = ellipse(
                    uv,
                    center + vec2(0.0, size.y * 0.55),
                    size * vec2(0.20, 0.18));

                vec3 maskColor = vec3(0.91, 0.36, 0.12);
                float maskAlpha = max(head, max(leftEar, rightEar));
                maskColor = mix(maskColor, vec3(0.98, 0.78, 0.54), muzzle);
                maskColor = mix(maskColor, vec3(0.15, 0.07, 0.04), nose);
                maskColor = mix(
                    maskColor,
                    vec3(0.04, 0.02, 0.01),
                    max(leftEye, rightEye));
                maskColor = mix(maskColor, vec3(1.0), teeth);
                color = mix(
                    color,
                    maskColor,
                    clamp(maskAlpha * uMaskStrength, 0.0, 0.94));
            }

            gl_FragColor = vec4(clamp(color, 0.0, 1.0), 1.0);
        }
        """;
}
#endif
