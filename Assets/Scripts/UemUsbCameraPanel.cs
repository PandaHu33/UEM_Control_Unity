using System;
using System.Text;
using UnityEngine;

[DisallowMultipleComponent]
public sealed class UemUsbCameraPanel : MonoBehaviour
{
    [SerializeField] Renderer screenRenderer;
    [SerializeField] bool autoStart = true;
    [SerializeField] string preferredDeviceNameContains = "DSJ";
    [SerializeField] bool preferNonFrontFacingCamera = true;
    [SerializeField, Min(16)] int requestedWidth = 1280;
    [SerializeField, Min(16)] int requestedHeight = 720;
    [SerializeField, Min(1)] int requestedFps = 30;
    [SerializeField, Min(0.5f)] float retryIntervalSeconds = 2f;
    [SerializeField] bool mirrorX;
    [SerializeField] bool mirrorY;
    [SerializeField] bool logStatus = true;
    [SerializeField, Min(0.5f)] float statusLogIntervalSeconds = 5f;
    [SerializeField] Color loadingColor = new Color(0.035f, 0.055f, 0.075f, 1f);
    [SerializeField] Color errorColor = new Color(0.18f, 0.035f, 0.035f, 1f);

    Material runtimeMaterial;
    Texture2D placeholderTexture;
    WebCamTexture webCamTexture;
    float nextRetryTime;
    string lastStatus;
    DateTime nextStatusLogTimeUtc;
    bool firstFrameLogged;

    void Reset()
    {
        screenRenderer = GetComponentInChildren<Renderer>(true);
    }

    void Awake()
    {
        LogStatus($"Awake activeInHierarchy={gameObject.activeInHierarchy}, enabled={enabled}, autoStart={autoStart}");
    }

    void Start()
    {
        PrepareRenderer();
        SetPlaceholder(loadingColor);

        if (autoStart)
            StartCamera();
    }

    void OnEnable()
    {
        if (Application.isPlaying && autoStart)
            StartCamera();
    }

    void OnDisable()
    {
        StopCamera();
    }

    void OnDestroy()
    {
        StopCamera();

        if (runtimeMaterial != null)
            Destroy(runtimeMaterial);

        if (placeholderTexture != null)
            Destroy(placeholderTexture);
    }

    void Update()
    {
        if (webCamTexture == null || !webCamTexture.isPlaying)
        {
            if (autoStart && Time.unscaledTime >= nextRetryTime)
            {
                nextRetryTime = Time.unscaledTime + retryIntervalSeconds;
                StartCamera();
            }

            return;
        }

        if (!firstFrameLogged && webCamTexture.didUpdateThisFrame && webCamTexture.width > 16 && webCamTexture.height > 16)
        {
            firstFrameLogged = true;
            LogStatus($"Received first USB camera frame {webCamTexture.width}x{webCamTexture.height}, rotation={webCamTexture.videoRotationAngle}, verticallyMirrored={webCamTexture.videoVerticallyMirrored}.");
        }
    }

    public void StartCamera()
    {
        PrepareRenderer();

        if (webCamTexture != null && webCamTexture.isPlaying)
            return;

#if UNITY_ANDROID && !UNITY_EDITOR
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
        {
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera);
            LogStatus("Requested Android camera permission; USB camera start will retry.");
            nextRetryTime = Time.unscaledTime + retryIntervalSeconds;
            return;
        }
#endif

        var devices = WebCamTexture.devices;
        if (devices == null || devices.Length == 0)
        {
            SetPlaceholder(errorColor);
            LogStatus("No WebCamTexture devices found. If the USB camera is connected through a dock, confirm PICO exposes it to Android Camera API.");
            return;
        }

        var deviceIndex = SelectDevice(devices);
        var device = devices[deviceIndex];
        LogStatus($"Starting USB camera device[{deviceIndex}] '{device.name}', frontFacing={device.isFrontFacing}. Devices: {DescribeDevices(devices)}");

        StopCamera();
        firstFrameLogged = false;

        webCamTexture = new WebCamTexture(device.name, requestedWidth, requestedHeight, requestedFps)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };

        ApplyTexture(webCamTexture);
        webCamTexture.Play();
        LogStatus($"WebCamTexture.Play requested {requestedWidth}x{requestedHeight}@{requestedFps}.");
    }

    public void StopCamera()
    {
        if (webCamTexture == null)
            return;

        LogStatus("Stopping USB WebCamTexture.");

        if (webCamTexture.isPlaying)
            webCamTexture.Stop();

        Destroy(webCamTexture);
        webCamTexture = null;
        firstFrameLogged = false;
    }

    int SelectDevice(WebCamDevice[] devices)
    {
        if (!string.IsNullOrWhiteSpace(preferredDeviceNameContains))
        {
            for (var i = 0; i < devices.Length; i++)
            {
                if (devices[i].name.IndexOf(preferredDeviceNameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                    return i;
            }
        }

        for (var i = 0; i < devices.Length; i++)
        {
            var name = devices[i].name;
            if (name.IndexOf("usb", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("uvc", StringComparison.OrdinalIgnoreCase) >= 0)
                return i;
        }

        if (preferNonFrontFacingCamera)
        {
            for (var i = 0; i < devices.Length; i++)
            {
                if (!devices[i].isFrontFacing)
                    return i;
            }
        }

        return 0;
    }

    static string DescribeDevices(WebCamDevice[] devices)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < devices.Length; i++)
        {
            if (i > 0)
                builder.Append("; ");

            builder.Append('[')
                .Append(i)
                .Append("] ")
                .Append(devices[i].name)
                .Append(" front=")
                .Append(devices[i].isFrontFacing);
        }

        return builder.ToString();
    }

    void PrepareRenderer()
    {
        if (screenRenderer == null)
            screenRenderer = GetComponentInChildren<Renderer>(true);

        if (screenRenderer == null)
            return;

        if (runtimeMaterial == null)
        {
            var shader = Resources.Load<Shader>("UemWebViewDoubleSided");
            if (shader == null)
                shader = Shader.Find("UEM/WebViewDoubleSided");
            if (shader == null)
                shader = Shader.Find("UEM/UnlitDoubleSidedTexture");
            if (shader == null)
                shader = Shader.Find("Unlit/Texture");
            if (shader == null)
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null)
                shader = Shader.Find("Standard");

            runtimeMaterial = shader != null
                ? new Material(shader)
                : new Material(screenRenderer.sharedMaterial);
            runtimeMaterial.name = "UEM USB Camera Runtime Material";
        }

        screenRenderer.material = runtimeMaterial;
    }

    void SetPlaceholder(Color color)
    {
        if (placeholderTexture == null)
        {
            placeholderTexture = new Texture2D(16, 16, TextureFormat.RGBA32, false)
            {
                name = "UEM USB Camera Placeholder",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
        }

        var pixels = new Color32[placeholderTexture.width * placeholderTexture.height];
        var pixel = (Color32)color;
        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = pixel;

        placeholderTexture.SetPixels32(pixels);
        placeholderTexture.Apply(false, false);
        ApplyTexture(placeholderTexture);
    }

    void ApplyTexture(Texture texture)
    {
        if (runtimeMaterial == null || texture == null)
            return;

        var scale = new Vector2(mirrorX ? -1f : 1f, mirrorY ? -1f : 1f);
        var offset = new Vector2(mirrorX ? 1f : 0f, mirrorY ? 1f : 0f);

        runtimeMaterial.mainTexture = texture;
        runtimeMaterial.mainTextureScale = scale;
        runtimeMaterial.mainTextureOffset = offset;

        if (runtimeMaterial.HasProperty("_BaseMap"))
        {
            runtimeMaterial.SetTexture("_BaseMap", texture);
            runtimeMaterial.SetTextureScale("_BaseMap", scale);
            runtimeMaterial.SetTextureOffset("_BaseMap", offset);
        }
        if (runtimeMaterial.HasProperty("_Color"))
            runtimeMaterial.SetColor("_Color", Color.white);
        if (runtimeMaterial.HasProperty("_BaseColor"))
            runtimeMaterial.SetColor("_BaseColor", Color.white);
    }

    void LogStatus(string message)
    {
        if (!logStatus)
            return;

        var now = DateTime.UtcNow;
        if (message == lastStatus && now < nextStatusLogTimeUtc)
            return;

        lastStatus = message;
        nextStatusLogTimeUtc = now.AddSeconds(statusLogIntervalSeconds);
        Debug.Log("[UEM USB Camera] " + message, this);
    }
}
