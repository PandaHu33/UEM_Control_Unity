using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using LibVLCSharp.Shared;
using UnityEngine;
#if ENABLE_IL2CPP
using AOT;
#endif

[DisallowMultipleComponent]
public sealed class UemNetworkCameraPanel : MonoBehaviour
{
    [SerializeField] string streamUrl = "rtsp://127.0.0.1:8554/usb_camera";
    [SerializeField] Renderer screenRenderer;
    [SerializeField] bool autoStart = true;
    [SerializeField, Min(0)] int networkCachingMilliseconds = 20;
    [SerializeField, Min(0)] int liveCachingMilliseconds = 20;
    [SerializeField, Min(0.1f)] float staleFrameTimeoutSeconds = 0.5f;
    [SerializeField, Min(0.5f)] float initialFrameGraceSeconds = 2f;
    [SerializeField, Min(0.5f)] float reconnectCooldownSeconds = 1f;
    [SerializeField] bool logStatus = true;
    [SerializeField, Min(0.5f)] float statusLogIntervalSeconds = 5f;
    [SerializeField] Color loadingColor = new Color(0.035f, 0.055f, 0.075f, 1f);
    [SerializeField] Color errorColor = new Color(0.18f, 0.035f, 0.035f, 1f);

    readonly object frameLock = new object();

    Material runtimeMaterial;
    Texture2D placeholderTexture;
    Texture2D videoTexture;
    LibVLC libVlc;
    MediaPlayer mediaPlayer;
    Media media;
    IntPtr nativeFrameBuffer;
    byte[] decodedFrame;
    byte[] uploadFrame;
    int frameWidth;
    int frameHeight;
    int frameBytes;
    bool hasNewFrame;
    bool openRequested;
    bool placeholderReady;
    bool firstVideoFormatLogged;
    bool firstVideoFrameLogged;
    string lastStatus;
    DateTime nextStatusLogTimeUtc;
    DateTime nextReconnectTimeUtc;
    long streamOpenedUtcTicks;
    long lastDecodedFrameUtcTicks;

    MediaPlayer.LibVLCVideoLockCb lockCallback;
    MediaPlayer.LibVLCVideoUnlockCb unlockCallback;
    MediaPlayer.LibVLCVideoDisplayCb displayCallback;
    MediaPlayer.LibVLCVideoFormatCb formatCallback;
    MediaPlayer.LibVLCVideoCleanupCb cleanupCallback;
    GCHandle callbackHandle;
    IntPtr callbackOpaque;

    static UemNetworkCameraPanel activeCallbackOwner;

#if UNITY_ANDROID && !UNITY_EDITOR
    [DllImport("vlc", EntryPoint = "libvlc_get_version")]
    static extern IntPtr AndroidLibVlcGetVersion();
#endif

    public string StreamUrl
    {
        get => streamUrl;
        set
        {
            if (streamUrl == value)
                return;

            streamUrl = value;
            if (Application.isPlaying && isActiveAndEnabled)
                RestartStream();
        }
    }

    void Reset()
    {
        screenRenderer = GetComponentInChildren<Renderer>(true);
    }

    void Awake()
    {
        LogStatus($"Awake activeInHierarchy={gameObject.activeInHierarchy}, enabled={enabled}, autoStart={autoStart}, platform={Application.platform}, url={streamUrl}");
    }

    void Start()
    {
        PrepareRenderer();
        SetPlaceholder(loadingColor);
        placeholderReady = true;

        if (autoStart)
            StartStream();
    }

    void OnEnable()
    {
        LogStatus($"OnEnable isPlaying={Application.isPlaying}, autoStart={autoStart}, openRequested={openRequested}");
        if (Application.isPlaying && autoStart)
            StartStream();
    }

    void OnDisable()
    {
        LogStatus("OnDisable stopping stream.");
        StopStream();
    }

    void OnDestroy()
    {
        StopStream();

        if (runtimeMaterial != null)
            Destroy(runtimeMaterial);

        if (placeholderTexture != null)
            Destroy(placeholderTexture);

        if (videoTexture != null)
            Destroy(videoTexture);
    }

    void Update()
    {
        ApplyPendingFrame();
        RestartIfStreamIsStale();
    }

    public void StartStream()
    {
        PrepareRenderer();

        if (!placeholderReady)
        {
            SetPlaceholder(loadingColor);
            placeholderReady = true;
        }

        if (openRequested)
        {
            LogStatus("StartStream ignored because a stream is already open/requested.");
            return;
        }

        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            SetPlaceholder(errorColor);
            LogStatus("RTSP stream URL is empty.");
            return;
        }

        try
        {
            LogStatus("StartStream entering LibVLC initialization.");
            InitializeVlcCore();
            CreateVlcPlayer();
            Interlocked.Exchange(ref streamOpenedUtcTicks, DateTime.UtcNow.Ticks);
            Interlocked.Exchange(ref lastDecodedFrameUtcTicks, 0);
            OpenVlcMedia();
            openRequested = true;
            LogStatus("Opening RTSP stream with LibVLCSharp: " + streamUrl);
        }
        catch (Exception exception)
        {
            SetPlaceholder(errorColor);
            LogStatus("Failed to start LibVLCSharp stream: " + exception);
            StopStream();
        }
    }

    public void StopStream()
    {
        if (openRequested || mediaPlayer != null || media != null || libVlc != null)
            LogStatus("Stopping LibVLCSharp stream.");

        openRequested = false;
        firstVideoFormatLogged = false;
        firstVideoFrameLogged = false;
        Interlocked.Exchange(ref streamOpenedUtcTicks, 0);
        Interlocked.Exchange(ref lastDecodedFrameUtcTicks, 0);

        if (mediaPlayer != null)
        {
            try
            {
                mediaPlayer.Stop();
            }
            catch
            {
            }
        }

        if (media != null)
        {
            media.Dispose();
            media = null;
        }

        if (mediaPlayer != null)
        {
            mediaPlayer.Dispose();
            mediaPlayer = null;
        }

        if (libVlc != null)
        {
            libVlc.Dispose();
            libVlc = null;
        }

        ReleaseFrameBuffer();
        ReleaseCallbackHandle();
    }

    public void RestartStream()
    {
        StopStream();
        SetPlaceholder(loadingColor);
        StartStream();
    }

    void CreateVlcPlayer()
    {
        if (mediaPlayer != null)
            return;

        EnsureCallbackHandle();
        activeCallbackOwner = this;

        lockCallback = OnVideoLockStatic;
        unlockCallback = OnVideoUnlockStatic;
        displayCallback = OnVideoDisplayStatic;
        formatCallback = OnVideoFormatStatic;
        cleanupCallback = OnVideoCleanupStatic;

        libVlc = new LibVLC(new[]
        {
            "--no-audio",
            "--no-video-title-show",
            "--drop-late-frames",
            "--skip-frames",
            "--rtsp-tcp",
            "--network-caching=" + networkCachingMilliseconds,
            "--live-caching=" + liveCachingMilliseconds
        });

        mediaPlayer = new MediaPlayer(libVlc);
        mediaPlayer.Buffering += OnMediaPlayerBuffering;
        mediaPlayer.Playing += OnMediaPlayerPlaying;
        mediaPlayer.EncounteredError += OnMediaPlayerEncounteredError;
        mediaPlayer.Vout += OnMediaPlayerVout;
        mediaPlayer.SetVideoFormatCallbacks(formatCallback, cleanupCallback);
        mediaPlayer.SetVideoCallbacks(lockCallback, unlockCallback, displayCallback);
        LogStatus("LibVLC media player created and callbacks registered.");
    }

    void InitializeVlcCore()
    {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
        var windowsVlcPath = Path.Combine(Application.dataPath, "Plugins/LibVLCSharp/Windows/x86_64");
        if (Directory.Exists(windowsVlcPath))
        {
            LogStatus("Core.Initialize using Windows VLC path: " + windowsVlcPath);
            Core.Initialize(windowsVlcPath);
        }
        else
        {
            LogStatus("Windows VLC path missing, using Core.Initialize default lookup.");
            Core.Initialize();
        }
#elif UNITY_ANDROID
        var preloadSucceeded = PreloadAndroidNativeLibraries();
        if (!preloadSucceeded)
            throw new InvalidOperationException("Android System.loadLibrary or direct P/Invoke could not load libvlc.");

        MarkLibVlcSharpCoreLoaded();
#else
        LogStatus("Core.Initialize using platform default lookup.");
        Core.Initialize();
#endif
    }

#if UNITY_ANDROID && !UNITY_EDITOR
    bool PreloadAndroidNativeLibraries()
    {
        var vlcLoaded = false;
        try
        {
            using var systemClass = new AndroidJavaClass("java.lang.System");
            TryLoadAndroidLibrary(systemClass, "c++_shared");
            vlcLoaded = TryLoadAndroidLibrary(systemClass, "vlc");
            if (vlcLoaded)
                vlcLoaded = LogAndroidLibVlcVersion();
        }
        catch (Exception exception)
        {
            LogStatus("Android native preload failed before Core.Initialize: " + exception);
        }

        return vlcLoaded;
    }

    bool TryLoadAndroidLibrary(AndroidJavaClass systemClass, string libraryName)
    {
        try
        {
            systemClass.CallStatic("loadLibrary", libraryName);
            LogStatus("Android System.loadLibrary succeeded: " + libraryName);
            return true;
        }
        catch (Exception exception)
        {
            LogStatus("Android System.loadLibrary failed for " + libraryName + ": " + exception);
            return false;
        }
    }

    bool LogAndroidLibVlcVersion()
    {
        try
        {
            var versionPtr = AndroidLibVlcGetVersion();
            var version = Marshal.PtrToStringAnsi(versionPtr);
            LogStatus("Android direct P/Invoke libvlc_get_version succeeded: " + version);
            return true;
        }
        catch (Exception exception)
        {
            LogStatus("Android direct P/Invoke libvlc_get_version failed: " + exception);
            return false;
        }
    }

    void MarkLibVlcSharpCoreLoaded()
    {
        try
        {
            var property = typeof(Core).GetProperty("LibVLCLoaded", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanWrite)
            {
                LogStatus("Could not mark LibVLCSharp Core loaded because LibVLCLoaded property is unavailable.");
                return;
            }

            property.SetValue(null, true);
            LogStatus("Marked LibVLCSharp Core.LibVLCLoaded=true after Android native preload.");
        }
        catch (Exception exception)
        {
            LogStatus("Failed to mark LibVLCSharp Core loaded: " + exception);
        }
    }
#else
    bool PreloadAndroidNativeLibraries()
    {
        return false;
    }

    void MarkLibVlcSharpCoreLoaded()
    {
    }
#endif

    void OpenVlcMedia()
    {
        media = new Media(libVlc, new Uri(streamUrl), Array.Empty<string>());
        media.AddOption(":rtsp-tcp");
        media.AddOption(":network-caching=" + networkCachingMilliseconds);
        media.AddOption(":live-caching=" + liveCachingMilliseconds);
        var playResult = mediaPlayer.Play(media);
        LogStatus($"mediaPlayer.Play returned {playResult}. url={streamUrl}");
    }

    void EnsureCallbackHandle()
    {
        if (callbackHandle.IsAllocated)
            return;

        callbackHandle = GCHandle.Alloc(this);
        callbackOpaque = GCHandle.ToIntPtr(callbackHandle);
    }

    void ReleaseCallbackHandle()
    {
        if (activeCallbackOwner == this)
            activeCallbackOwner = null;

        callbackOpaque = IntPtr.Zero;

        if (callbackHandle.IsAllocated)
            callbackHandle.Free();
    }

    static UemNetworkCameraPanel GetCallbackOwner(IntPtr opaque)
    {
        if (opaque != IntPtr.Zero)
        {
            try
            {
                return GCHandle.FromIntPtr(opaque).Target as UemNetworkCameraPanel;
            }
            catch
            {
            }
        }

        return activeCallbackOwner;
    }

#if ENABLE_IL2CPP
    [MonoPInvokeCallback(typeof(MediaPlayer.LibVLCVideoFormatCb))]
#endif
    static uint OnVideoFormatStatic(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        var owner = GetCallbackOwner(opaque);
        if (owner == null)
            return 0;

        opaque = owner.callbackOpaque;
        return owner.HandleVideoFormat(chroma, ref width, ref height, ref pitches, ref lines);
    }

#if ENABLE_IL2CPP
    [MonoPInvokeCallback(typeof(MediaPlayer.LibVLCVideoCleanupCb))]
#endif
    static void OnVideoCleanupStatic(ref IntPtr opaque)
    {
        var owner = GetCallbackOwner(opaque);
        if (owner != null)
            owner.ReleaseFrameBuffer();

        opaque = IntPtr.Zero;
    }

#if ENABLE_IL2CPP
    [MonoPInvokeCallback(typeof(MediaPlayer.LibVLCVideoLockCb))]
#endif
    static IntPtr OnVideoLockStatic(IntPtr opaque, IntPtr planes)
    {
        var owner = GetCallbackOwner(opaque);
        return owner != null ? owner.HandleVideoLock(planes) : IntPtr.Zero;
    }

#if ENABLE_IL2CPP
    [MonoPInvokeCallback(typeof(MediaPlayer.LibVLCVideoUnlockCb))]
#endif
    static void OnVideoUnlockStatic(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
    }

#if ENABLE_IL2CPP
    [MonoPInvokeCallback(typeof(MediaPlayer.LibVLCVideoDisplayCb))]
#endif
    static void OnVideoDisplayStatic(IntPtr opaque, IntPtr picture)
    {
        var owner = GetCallbackOwner(opaque);
        if (owner != null)
            owner.HandleVideoDisplay();
    }

    uint HandleVideoFormat(IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        if (width == 0 || height == 0)
            return 0;

        var outputWidth = checked((int)width);
        var outputHeight = checked((int)height);
        var pitch = checked(outputWidth * 4);
        var byteCount = checked(pitch * outputHeight);

        if (!firstVideoFormatLogged)
        {
            firstVideoFormatLogged = true;
            LogStatus($"OnVideoFormat width={outputWidth}, height={outputHeight}, pitch={pitch}, bytes={byteCount}");
        }

        lock (frameLock)
        {
            ReleaseFrameBufferLocked();
            nativeFrameBuffer = Marshal.AllocHGlobal(byteCount);
            decodedFrame = new byte[byteCount];
            uploadFrame = new byte[byteCount];
            frameWidth = outputWidth;
            frameHeight = outputHeight;
            frameBytes = byteCount;
            hasNewFrame = false;
        }

        var format = Encoding.ASCII.GetBytes("RV32");
        Marshal.Copy(format, 0, chroma, format.Length);
        pitches = checked((uint)pitch);
        lines = checked((uint)outputHeight);
        return 1;
    }

    IntPtr HandleVideoLock(IntPtr planes)
    {
        lock (frameLock)
        {
            if (nativeFrameBuffer != IntPtr.Zero)
                Marshal.WriteIntPtr(planes, nativeFrameBuffer);
        }

        return IntPtr.Zero;
    }

    void HandleVideoDisplay()
    {
        lock (frameLock)
        {
            if (nativeFrameBuffer == IntPtr.Zero || decodedFrame == null || frameBytes <= 0)
                return;

            Marshal.Copy(nativeFrameBuffer, decodedFrame, 0, frameBytes);
            hasNewFrame = true;
        }

        Interlocked.Exchange(ref lastDecodedFrameUtcTicks, DateTime.UtcNow.Ticks);

        if (!firstVideoFrameLogged)
        {
            firstVideoFrameLogged = true;
            LogStatus("OnVideoDisplay received first decoded frame.");
        }
    }

    void ApplyPendingFrame()
    {
        byte[] frameToUpload = null;
        int width = 0;
        int height = 0;

        lock (frameLock)
        {
            if (!hasNewFrame || decodedFrame == null || uploadFrame == null)
                return;

            Buffer.BlockCopy(decodedFrame, 0, uploadFrame, 0, frameBytes);
            frameToUpload = uploadFrame;
            width = frameWidth;
            height = frameHeight;
            hasNewFrame = false;
        }

        if (frameToUpload == null || width <= 0 || height <= 0)
            return;

        if (videoTexture == null || videoTexture.width != width || videoTexture.height != height)
        {
            if (videoTexture != null)
                Destroy(videoTexture);

            videoTexture = new Texture2D(width, height, TextureFormat.BGRA32, false)
            {
                name = "UEM LibVLC RTSP Camera Texture",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            LogStatus($"Created video Texture2D {width}x{height}.");
        }

        videoTexture.LoadRawTextureData(frameToUpload);
        videoTexture.Apply(false, false);
        ApplyTexture(videoTexture);
    }

    void RestartIfStreamIsStale()
    {
        if (!openRequested || mediaPlayer == null)
            return;

        var now = DateTime.UtcNow;
        if (now < nextReconnectTimeUtc)
            return;

        var lastFrameTicks = Interlocked.Read(ref lastDecodedFrameUtcTicks);
        var referenceTicks = lastFrameTicks > 0
            ? lastFrameTicks
            : Interlocked.Read(ref streamOpenedUtcTicks);
        if (referenceTicks <= 0)
            return;

        var timeoutSeconds = lastFrameTicks > 0
            ? staleFrameTimeoutSeconds
            : initialFrameGraceSeconds;
        var ageSeconds = TimeSpan.FromTicks(Math.Max(0, now.Ticks - referenceTicks)).TotalSeconds;
        if (ageSeconds <= timeoutSeconds)
            return;

        nextReconnectTimeUtc = now.AddSeconds(reconnectCooldownSeconds);
        LogStatus($"RTSP frame stale for {ageSeconds:0.000}s; restarting to discard queued frames.");
        RestartStream();
    }

    void ReleaseFrameBuffer()
    {
        lock (frameLock)
        {
            ReleaseFrameBufferLocked();
        }
    }

    void ReleaseFrameBufferLocked()
    {
        if (nativeFrameBuffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(nativeFrameBuffer);
            nativeFrameBuffer = IntPtr.Zero;
        }

        decodedFrame = null;
        uploadFrame = null;
        frameWidth = 0;
        frameHeight = 0;
        frameBytes = 0;
        hasNewFrame = false;
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
            runtimeMaterial.name = "UEM LibVLC RTSP Camera Runtime Material";
        }

        screenRenderer.material = runtimeMaterial;
    }

    void SetPlaceholder(Color color)
    {
        if (placeholderTexture == null)
        {
            placeholderTexture = new Texture2D(16, 16, TextureFormat.RGBA32, false)
            {
                name = "UEM RTSP Camera Placeholder",
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

        runtimeMaterial.mainTexture = texture;
        runtimeMaterial.mainTextureScale = new Vector2(-1f, -1f);
        runtimeMaterial.mainTextureOffset = Vector2.one;

        if (runtimeMaterial.HasProperty("_BaseMap"))
        {
            runtimeMaterial.SetTexture("_BaseMap", texture);
            runtimeMaterial.SetTextureScale("_BaseMap", new Vector2(-1f, -1f));
            runtimeMaterial.SetTextureOffset("_BaseMap", Vector2.one);
        }
        if (runtimeMaterial.HasProperty("_Color"))
            runtimeMaterial.SetColor("_Color", Color.white);
        if (runtimeMaterial.HasProperty("_BaseColor"))
            runtimeMaterial.SetColor("_BaseColor", Color.white);
    }

    void OnMediaPlayerBuffering(object sender, MediaPlayerBufferingEventArgs args)
    {
        LogStatus($"LibVLC buffering {args.Cache:0.0}%.");
    }

    void OnMediaPlayerPlaying(object sender, EventArgs args)
    {
        LogStatus("LibVLC state changed to Playing.");
    }

    void OnMediaPlayerEncounteredError(object sender, EventArgs args)
    {
        LogStatus("LibVLC encountered an error while opening or decoding the stream.");
    }

    void OnMediaPlayerVout(object sender, MediaPlayerVoutEventArgs args)
    {
        LogStatus($"LibVLC video output count changed: {args.Count}.");
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
        Debug.Log("[UEM RTSP Camera] " + message, this);
    }
}
