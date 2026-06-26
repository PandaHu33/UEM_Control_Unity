using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit;

[DisallowMultipleComponent]
public sealed class UemH5WebViewPanel : MonoBehaviour
{
    [SerializeField] string url = "http://127.0.0.1:8070/control_ui/index.html";
    [SerializeField] Camera targetCamera;
    [SerializeField] Renderer screenRenderer;
    [SerializeField] bool autoShowOnStart = true;

    [SerializeField] bool fitToWorldPanel = true;
    [SerializeField] int fallbackMarginLeft = 80;
    [SerializeField] int fallbackMarginTop = 80;
    [SerializeField] int fallbackMarginRight = 80;
    [SerializeField] int fallbackMarginBottom = 80;

    [SerializeField, Min(64)] int textureWidth = 1280;
    [SerializeField, Min(64)] int textureHeight = 1280;
    [SerializeField, Range(1f, 30f)] float refreshHz = 8f;
    [SerializeField, Range(1f, 30f)] float interactionRefreshHz = 18f;
    [SerializeField, Min(0.1f)] float interactionRefreshSeconds = 0.5f;
    [SerializeField] Color loadingColor = new Color(0.035f, 0.055f, 0.075f, 1f);

    [SerializeField] bool enableVrPointerInput;
    [SerializeField] XRRayInteractor[] rayInteractors;
    [SerializeField, Min(0.5f)] float pointerMaxDistance = 8f;
    [SerializeField, Min(0f)] float pointerMovePixelThreshold = 1f;
    [SerializeField, Min(1f)] float scrollPixelsPerSecond = 900f;
    [SerializeField] bool invertPointerX;
    [SerializeField] bool invertPointerY;

    const string AndroidWebViewClass = "com.uemcontrol.webview.OffscreenWebView";

    Texture2D webTexture;
    Material runtimeMaterial;
    float nextRefreshTime;
    float nextStatusLogTime;
    bool loadedOnce;
    bool visible = true;
    string lastLoggedStatus;
    bool pointerDown;
    float nextRayDiscoveryTime;
    float lastInteractionTime = -100f;
    Vector2Int lastPointerPixel;
    XRRayInteractor activePointer;
    readonly List<XRRayInteractor> discoveredRayInteractors = new List<XRRayInteractor>();

#if UNITY_ANDROID && !UNITY_EDITOR
    AndroidJavaObject androidWebView;
#endif

    void Reset()
    {
        targetCamera = Camera.main;
        screenRenderer = GetComponentInChildren<Renderer>(true);
    }

    void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        RequestAndroidMediaPermissions();
#endif

        PrepareRenderer();
        DiscoverRayInteractors();

        if (autoShowOnStart)
            Show();
    }

    void OnEnable()
    {
        visible = true;

        if (screenRenderer != null)
            screenRenderer.enabled = true;
    }

    void OnDisable()
    {
        ReleasePointer();
        visible = false;
    }

    void Update()
    {
        if (visible)
            HandlePointerInput();

        if (!visible || webTexture == null || Time.unscaledTime < nextRefreshTime)
            return;

        var hz = Time.unscaledTime - lastInteractionTime <= interactionRefreshSeconds
            ? interactionRefreshHz
            : refreshHz;
        nextRefreshTime = Time.unscaledTime + 1f / Mathf.Max(1f, hz);
        RenderWebViewFrame();
    }

    void OnDestroy()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (androidWebView != null)
        {
            androidWebView.Call("dispose");
            androidWebView.Dispose();
            androidWebView = null;
        }
#endif

        if (runtimeMaterial != null)
            Destroy(runtimeMaterial);

        if (webTexture != null)
            Destroy(webTexture);
    }

    public void Show()
    {
        visible = true;
        PrepareRenderer();

        if (screenRenderer != null)
            screenRenderer.enabled = true;

        EnsureWebView();

        if (!loadedOnce)
        {
            LoadUrl(url);
            loadedOnce = true;
        }
    }

    public void Hide()
    {
        ReleasePointer();
        visible = false;

        if (screenRenderer != null)
            screenRenderer.enabled = false;
    }

    public void Reload()
    {
        EnsureWebView();
        LoadUrl(url);
        loadedOnce = true;
    }

    public void EvaluateJavaScript(string javascript)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (androidWebView != null)
            androidWebView.Call("evaluateJavaScript", javascript);
#endif
    }

    void HandlePointerInput()
    {
        ReleasePointer();
    }

    void ReleasePointer()
    {
        if (!pointerDown)
            return;

        SendWebTouch("mouseUp", lastPointerPixel);
        pointerDown = false;
        activePointer = null;
    }

    void SendWebTouch(string methodName, Vector2Int pixel)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        try
        {
            if (androidWebView != null)
                androidWebView.Call(methodName, pixel.x, pixel.y);
        }
        catch (Exception exception)
        {
            LogStatusThrottled("[UEM WebView] Touch event failed: " + exception.Message);
        }
#else
        _ = methodName;
        _ = pixel;
#endif
    }

    void SendWebScroll(Vector2Int pixel, int deltaX, int deltaY)
    {
        _ = pixel;
        _ = deltaX;
        _ = deltaY;
    }

    void MarkInteraction()
    {
        lastInteractionTime = Time.unscaledTime;
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    static void RequestAndroidMediaPermissions()
    {
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(UnityEngine.Android.Permission.Camera))
            UnityEngine.Android.Permission.RequestUserPermission(UnityEngine.Android.Permission.Camera);
    }
#endif

    bool TryGetBestPointer(out XRRayInteractor rayInteractor, out Vector2Int pixel)
    {
        rayInteractor = null;
        pixel = default(Vector2Int);

        if (activePointer != null && TryGetPanelPixel(activePointer, out pixel))
        {
            rayInteractor = activePointer;
            return true;
        }

        XRRayInteractor firstHover = null;
        var firstHoverPixel = default(Vector2Int);

        foreach (var candidate in GetPointerRays())
        {
            if (!TryGetPanelPixel(candidate, out var candidatePixel))
                continue;

            if (firstHover == null)
            {
                firstHover = candidate;
                firstHoverPixel = candidatePixel;
            }

            if (!IsPressed(candidate))
                continue;

            rayInteractor = candidate;
            pixel = candidatePixel;
            return true;
        }

        if (firstHover == null)
            return false;

        rayInteractor = firstHover;
        pixel = firstHoverPixel;
        return true;
    }

    IEnumerable<XRRayInteractor> GetPointerRays()
    {
        if (rayInteractors != null && rayInteractors.Length > 0)
            return rayInteractors;

        return discoveredRayInteractors;
    }

    bool TryGetPanelPixel(XRRayInteractor rayInteractor, out Vector2Int pixel)
    {
        pixel = default(Vector2Int);

        if (rayInteractor == null || !rayInteractor.isActiveAndEnabled || !rayInteractor.gameObject.activeInHierarchy)
            return false;
        if (screenRenderer == null || webTexture == null)
            return false;

        var screenTransform = screenRenderer.transform;
        var origin = rayInteractor.rayOriginTransform != null ? rayInteractor.rayOriginTransform : rayInteractor.transform;
        var ray = new Ray(origin.position, origin.forward);
        var plane = new Plane(screenTransform.forward, screenTransform.position);

        if (!plane.Raycast(ray, out var distance) || distance < 0f || distance > pointerMaxDistance)
            return false;

        var localPoint = screenTransform.InverseTransformPoint(ray.GetPoint(distance));
        if (localPoint.x < -0.5f || localPoint.x > 0.5f || localPoint.y < -0.5f || localPoint.y > 0.5f)
            return false;

        var u = Mathf.Clamp01(localPoint.x + 0.5f);
        var v = Mathf.Clamp01(localPoint.y + 0.5f);
        var localRayDirection = screenTransform.InverseTransformDirection(ray.direction);
        var isBackFaceHit = localRayDirection.z < 0f;

        if (isBackFaceHit)
            u = 1f - u;
        if (invertPointerX)
            u = 1f - u;
        if (invertPointerY)
            v = 1f - v;

        var x = Mathf.Clamp(Mathf.RoundToInt(u * (textureWidth - 1)), 0, textureWidth - 1);
        var y = Mathf.Clamp(Mathf.RoundToInt((1f - v) * (textureHeight - 1)), 0, textureHeight - 1);
        pixel = new Vector2Int(x, y);
        return true;
    }

    void DiscoverRayInteractors()
    {
        if (rayInteractors != null && rayInteractors.Length > 0)
            return;
        if (Time.unscaledTime < nextRayDiscoveryTime && discoveredRayInteractors.Count > 0)
            return;

        nextRayDiscoveryTime = Time.unscaledTime + 1f;
        discoveredRayInteractors.Clear();

        var sceneRays = FindObjectsOfType<XRRayInteractor>(true);
        foreach (var rayInteractor in sceneRays)
        {
            if (IsUsablePointerRay(rayInteractor))
                discoveredRayInteractors.Add(rayInteractor);
        }

        if (discoveredRayInteractors.Count > 0)
            LogStatusOnce($"[UEM WebView] Found {discoveredRayInteractors.Count} XR pointer rays for WebView input.");
    }

    bool IsUsablePointerRay(XRRayInteractor rayInteractor)
    {
        if (rayInteractor == null)
            return false;

        if (HierarchyContains(rayInteractor.transform, "Teleport"))
            return false;

        return HierarchyContains(rayInteractor.transform, "Controller") ||
               HierarchyContains(rayInteractor.transform, "Hand") ||
               HierarchyContains(rayInteractor.transform, "Ray Interactor") ||
               rayInteractor.GetComponentInParent<ActionBasedController>() != null;
    }

    static bool HierarchyContains(Transform transform, string token)
    {
        for (var current = transform; current != null; current = current.parent)
        {
            if (current.name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    static bool IsPressed(XRRayInteractor rayInteractor)
    {
        var controller = rayInteractor != null ? rayInteractor.GetComponentInParent<ActionBasedController>() : null;
        if (controller == null)
            return false;

        return ReadButton(controller.uiPressAction) ||
               ReadButton(controller.uiPressActionValue) ||
               ReadButton(controller.selectAction) ||
               ReadButton(controller.selectActionValue) ||
               ReadButton(controller.activateAction) ||
               ReadButton(controller.activateActionValue);
    }

    static Vector2 ReadScrollInput(XRRayInteractor rayInteractor)
    {
        var controller = rayInteractor != null ? rayInteractor.GetComponentInParent<ActionBasedController>() : null;
        if (controller == null)
            return Vector2.zero;

        var scroll = ReadVector2(controller.uiScrollAction);
        if (scroll.sqrMagnitude > 0.0001f)
            return scroll;

        scroll = ReadVector2(controller.translateAnchorAction);
        if (scroll.sqrMagnitude > 0.0001f)
            return scroll;

        scroll = ReadVector2(controller.rotateAnchorAction);
        if (scroll.sqrMagnitude > 0.0001f)
            return scroll;

        return ReadLegacyThumbstickScroll(rayInteractor);
    }

    static Vector2 ReadLegacyThumbstickScroll(XRRayInteractor rayInteractor)
    {
        var node = XRNode.RightHand;

        if (rayInteractor != null && HierarchyContains(rayInteractor.transform, "Left"))
            node = XRNode.LeftHand;

        var device = InputDevices.GetDeviceAtXRNode(node);
        if (!device.isValid && node == XRNode.RightHand)
            device = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);

        if (!device.isValid)
            return Vector2.zero;

        if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primary2DAxis, out Vector2 axis) && axis.sqrMagnitude > 0.0001f)
            return axis;
        if (device.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondary2DAxis, out axis) && axis.sqrMagnitude > 0.0001f)
            return axis;

        return Vector2.zero;
    }

    static bool ReadButton(InputActionProperty inputActionProperty)
    {
        var action = inputActionProperty.action;
        if (action == null)
            return false;

        try
        {
            if (action.IsPressed())
                return true;
        }
        catch (InvalidOperationException)
        {
        }

        try
        {
            return action.ReadValue<float>() > 0.35f;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    static Vector2 ReadVector2(InputActionProperty inputActionProperty)
    {
        var action = inputActionProperty.action;
        if (action == null)
            return Vector2.zero;

        try
        {
            return action.ReadValue<Vector2>();
        }
        catch (InvalidOperationException)
        {
            return Vector2.zero;
        }
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

            runtimeMaterial = new Material(shader)
            {
                name = "UEM H5 WebView Runtime Material"
            };
        }

        if (webTexture == null || webTexture.width != textureWidth || webTexture.height != textureHeight)
        {
            if (webTexture != null)
                Destroy(webTexture);

            webTexture = new Texture2D(textureWidth, textureHeight, TextureFormat.RGBA32, false)
            {
                name = "UEM H5 WebView Texture",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };

            FillTexture(loadingColor);
        }

        ApplyTexture(webTexture);
        screenRenderer.material = runtimeMaterial;
    }

    void EnsureWebView()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (androidWebView != null)
            return;

        try
        {
            androidWebView = new AndroidJavaObject(AndroidWebViewClass, textureWidth, textureHeight);
            Debug.Log($"[UEM WebView] Created Android offscreen WebView {textureWidth}x{textureHeight}.", this);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[UEM WebView] Failed to create Android offscreen WebView: {exception.Message}", this);
        }
#else
        LogStatusOnce("[UEM WebView] Android offscreen WebView is only active in an Android player build.");
#endif
    }

    void LoadUrl(string targetUrl)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (androidWebView == null)
            return;

        try
        {
            androidWebView.Call("loadUrl", targetUrl);
            Debug.Log("[UEM WebView] Loading " + targetUrl, this);
        }
        catch (Exception exception)
        {
            Debug.LogError($"[UEM WebView] LoadUrl failed: {exception.Message}", this);
        }
#else
        _ = targetUrl;
#endif
    }

    void RenderWebViewFrame()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (androidWebView == null)
            return;

        try
        {
            if (!androidWebView.Call<bool>("isInitialized"))
            {
                LogStatusThrottled("[UEM WebView] Waiting for Android WebView initialization...");
                return;
            }

            var pixels = androidWebView.Call<byte[]>("render");
            var expectedBytes = textureWidth * textureHeight * 4;
            if (pixels == null || pixels.Length != expectedBytes)
            {
                LogAndroidStatus("No WebView bitmap yet.");
                return;
            }

            webTexture.LoadRawTextureData(pixels);
            webTexture.Apply(false, false);
            ApplyTexture(webTexture);

            var error = androidWebView.Call<string>("getLastError");
            if (!string.IsNullOrEmpty(error))
                LogStatusThrottled("[UEM WebView] " + error);
        }
        catch (Exception exception)
        {
            LogStatusThrottled("[UEM WebView] Render failed: " + exception.Message);
        }
#endif
    }

    void LogAndroidStatus(string prefix)
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        var progress = androidWebView.Call<int>("getProgress");
        var error = androidWebView.Call<string>("getLastError");
        var currentUrl = androidWebView.Call<string>("getCurrentUrl");
        var message = $"[UEM WebView] {prefix} progress={progress} url={currentUrl}";
        if (!string.IsNullOrEmpty(error))
            message += " error=" + error;
        LogStatusThrottled(message);
#else
        _ = prefix;
#endif
    }

    void ApplyTexture(Texture texture)
    {
        if (runtimeMaterial == null)
            return;

        runtimeMaterial.mainTexture = texture;

        if (runtimeMaterial.HasProperty("_BaseMap"))
            runtimeMaterial.SetTexture("_BaseMap", texture);
        if (runtimeMaterial.HasProperty("_Color"))
            runtimeMaterial.SetColor("_Color", Color.white);
        if (runtimeMaterial.HasProperty("_BaseColor"))
            runtimeMaterial.SetColor("_BaseColor", Color.white);
    }

    void FillTexture(Color color)
    {
        var pixels = new Color32[textureWidth * textureHeight];
        var pixel = (Color32)color;

        for (var i = 0; i < pixels.Length; i++)
            pixels[i] = pixel;

        webTexture.SetPixels32(pixels);
        webTexture.Apply(false, false);
    }

    void LogStatusOnce(string message)
    {
        if (lastLoggedStatus == message)
            return;

        lastLoggedStatus = message;
        Debug.Log(message, this);
    }

    void LogStatusThrottled(string message)
    {
        if (lastLoggedStatus == message && Time.unscaledTime < nextStatusLogTime)
            return;

        lastLoggedStatus = message;
        nextStatusLogTime = Time.unscaledTime + 3f;
        Debug.Log(message, this);
    }

    void OnValidate()
    {
        textureWidth = Mathf.Max(64, textureWidth);
        textureHeight = Mathf.Max(64, textureHeight);
        refreshHz = Mathf.Clamp(refreshHz, 1f, 30f);
        interactionRefreshHz = Mathf.Clamp(interactionRefreshHz, 1f, 30f);
        interactionRefreshSeconds = Mathf.Max(0.1f, interactionRefreshSeconds);
        pointerMaxDistance = Mathf.Max(0.5f, pointerMaxDistance);
        pointerMovePixelThreshold = Mathf.Max(0f, pointerMovePixelThreshold);
        scrollPixelsPerSecond = Mathf.Max(1f, scrollPixelsPerSecond);

        _ = targetCamera;
        _ = fitToWorldPanel;
        _ = fallbackMarginLeft;
        _ = fallbackMarginTop;
        _ = fallbackMarginRight;
        _ = fallbackMarginBottom;
    }
}
