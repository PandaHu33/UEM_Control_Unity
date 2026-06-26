package com.uemcontrol.webview;

import android.Manifest;
import android.app.Activity;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.os.Build;
import android.os.SystemClock;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.webkit.ConsoleMessage;
import android.webkit.PermissionRequest;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

import java.util.ArrayList;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

public final class OffscreenWebView {
    private static final int MEDIA_PERMISSION_REQUEST_CODE = 58031;

    private final Activity activity;
    private final int width;
    private final int height;

    private WebView webView;
    private Bitmap bitmap;
    private Canvas canvas;
    private int[] argbPixels;
    private byte[] workingRgbaPixels;
    private byte[] latestRgbaPixels;
    private String pendingUrl;
    private String currentUrl = "";
    private String lastError = "";
    private int progress;
    private boolean initialized;
    private boolean loaded;
    private volatile boolean renderPending;
    private long touchDownTime;
    private final Object frameLock = new Object();

    public OffscreenWebView(int width, int height) {
        this.activity = resolveUnityActivity();
        this.width = Math.max(64, width);
        this.height = Math.max(64, height);
        runOnUiThread(this::createWebView);
    }

    public boolean isInitialized() {
        return initialized;
    }

    public boolean isLoaded() {
        return loaded;
    }

    public int getProgress() {
        return progress;
    }

    public String getCurrentUrl() {
        return currentUrl;
    }

    public String getLastError() {
        return lastError;
    }

    public void loadUrl(String url) {
        pendingUrl = url;
        runOnUiThread(() -> {
            if (webView == null) {
                return;
            }

            currentUrl = url;
            loaded = false;
            progress = 0;
            webView.loadUrl(url);
        });
    }

    public void reload() {
        runOnUiThread(() -> {
            if (webView != null) {
                loaded = false;
                webView.reload();
            }
        });
    }

    public void evaluateJavaScript(String js) {
        runOnUiThread(() -> {
            if (webView == null) {
                return;
            }

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.KITKAT) {
                webView.evaluateJavascript(js, null);
            } else {
                webView.loadUrl("javascript:" + js);
            }
        });
    }

    public void mouseDown(int x, int y) {
        touchDownTime = SystemClock.uptimeMillis();
        dispatchTouch(MotionEvent.ACTION_DOWN, x, y, touchDownTime);
    }

    public void mouseMove(int x, int y) {
        long downTime = touchDownTime != 0 ? touchDownTime : SystemClock.uptimeMillis();
        dispatchTouch(MotionEvent.ACTION_MOVE, x, y, downTime);
    }

    public void mouseUp(int x, int y) {
        long downTime = touchDownTime != 0 ? touchDownTime : SystemClock.uptimeMillis();
        dispatchTouch(MotionEvent.ACTION_UP, x, y, downTime);
        touchDownTime = 0;
    }

    public void scrollByPixels(int dx, int dy) {
        // WebView is view-only in the headset scene.
    }

    public void scrollAtPixels(int x, int y, int dx, int dy) {
        // WebView is view-only in the headset scene.
    }

    public byte[] render() {
        if (!initialized || webView == null) {
            return null;
        }

        requestRender();

        synchronized (frameLock) {
            return latestRgbaPixels;
        }
    }

    public void requestRender() {
        if (!initialized || webView == null || renderPending) {
            return;
        }

        renderPending = true;
        runOnUiThread(() -> {
            try {
                ensureBitmap();
                canvas.drawColor(Color.WHITE);
                webView.draw(canvas);
                bitmap.getPixels(argbPixels, 0, width, 0, 0, width, height);
                convertArgbToRgba();

                synchronized (frameLock) {
                    byte[] previousFrame = latestRgbaPixels;
                    latestRgbaPixels = workingRgbaPixels;
                    workingRgbaPixels = previousFrame != null
                            ? previousFrame
                            : new byte[width * height * 4];
                }
            } catch (Throwable throwable) {
                lastError = throwable.getClass().getSimpleName() + ": " + throwable.getMessage();
            } finally {
                renderPending = false;
            }
        });
    }

    public void dispose() {
        runOnUiThread(() -> {
            if (webView != null) {
                webView.stopLoading();
                webView.loadUrl("about:blank");
                webView.destroy();
                webView = null;
            }

            if (bitmap != null) {
                bitmap.recycle();
                bitmap = null;
            }

            canvas = null;
            argbPixels = null;
            workingRgbaPixels = null;
            latestRgbaPixels = null;
            initialized = false;
            loaded = false;
            renderPending = false;
        });
    }

    private void createWebView() {
        try {
            requestRuntimeMediaPermissions(null);
            WebView.setWebContentsDebuggingEnabled(true);

            webView = new WebView(activity);
            webView.setLayoutParams(new ViewGroup.LayoutParams(width, height));
            webView.setVisibility(View.INVISIBLE);
            webView.setBackgroundColor(Color.WHITE);
            webView.setWillNotDraw(false);
            webView.setLayerType(View.LAYER_TYPE_SOFTWARE, null);
            webView.setClickable(true);
            webView.setLongClickable(true);
            webView.setFocusable(true);
            webView.setFocusableInTouchMode(true);
            webView.setInitialScale(100);
            webView.setOverScrollMode(View.OVER_SCROLL_NEVER);
            webView.setVerticalScrollBarEnabled(false);
            webView.setHorizontalScrollBarEnabled(false);

            WebSettings settings = webView.getSettings();
            settings.setJavaScriptEnabled(true);
            settings.setDomStorageEnabled(true);
            settings.setDatabaseEnabled(true);
            settings.setLoadWithOverviewMode(false);
            settings.setUseWideViewPort(true);
            settings.setTextZoom(100);
            settings.setSupportZoom(false);
            settings.setBuiltInZoomControls(false);
            settings.setDisplayZoomControls(false);
            settings.setAllowFileAccess(true);
            settings.setAllowContentAccess(true);
            settings.setMediaPlaybackRequiresUserGesture(false);
            settings.setMixedContentMode(WebSettings.MIXED_CONTENT_ALWAYS_ALLOW);
            settings.setCacheMode(WebSettings.LOAD_NO_CACHE);

            webView.setWebViewClient(new WebViewClient() {
                @Override
                public void onPageFinished(WebView view, String url) {
                    loaded = true;
                    progress = 100;
                    currentUrl = url;
                }

                @Override
                public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                    if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M && error != null) {
                        lastError = error.getErrorCode() + ": " + error.getDescription();
                    } else {
                        lastError = "WebView resource error.";
                    }
                }

                @Override
                public void onReceivedHttpError(WebView view, WebResourceRequest request, WebResourceResponse errorResponse) {
                    if (errorResponse != null) {
                        lastError = "HTTP " + errorResponse.getStatusCode() + ": " + errorResponse.getReasonPhrase();
                    }
                }
            });

            webView.setWebChromeClient(new WebChromeClient() {
                @Override
                public void onPermissionRequest(PermissionRequest request) {
                    if (request == null) {
                        return;
                    }

                    requestRuntimeMediaPermissions(request.getResources());
                    request.grant(request.getResources());
                }

                @Override
                public void onProgressChanged(WebView view, int newProgress) {
                    progress = newProgress;
                }

                @Override
                public boolean onConsoleMessage(ConsoleMessage consoleMessage) {
                    if (consoleMessage != null && consoleMessage.messageLevel() == ConsoleMessage.MessageLevel.ERROR) {
                        lastError = consoleMessage.message();
                    }
                    return false;
                }
            });

            webView.measure(
                    View.MeasureSpec.makeMeasureSpec(width, View.MeasureSpec.EXACTLY),
                    View.MeasureSpec.makeMeasureSpec(height, View.MeasureSpec.EXACTLY));
            webView.layout(0, 0, width, height);
            webView.requestFocus();

            ensureBitmap();
            initialized = true;

            if (pendingUrl != null && pendingUrl.length() > 0) {
                loadUrl(pendingUrl);
            }
        } catch (Throwable throwable) {
            initialized = false;
            lastError = throwable.getClass().getSimpleName() + ": " + throwable.getMessage();
        }
    }

    private void requestRuntimeMediaPermissions(String[] resources) {
        if (activity == null || Build.VERSION.SDK_INT < Build.VERSION_CODES.M) {
            return;
        }

        boolean needsCamera = resources == null;
        boolean needsAudio = false;

        if (resources != null) {
            for (String resource : resources) {
                if (PermissionRequest.RESOURCE_VIDEO_CAPTURE.equals(resource)) {
                    needsCamera = true;
                } else if (PermissionRequest.RESOURCE_AUDIO_CAPTURE.equals(resource)) {
                    needsAudio = true;
                }
            }
        }

        ArrayList<String> missingPermissions = new ArrayList<>();
        if (needsCamera && activity.checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            missingPermissions.add(Manifest.permission.CAMERA);
        }

        if (needsAudio && activity.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            missingPermissions.add(Manifest.permission.RECORD_AUDIO);
        }

        if (!missingPermissions.isEmpty()) {
            activity.requestPermissions(
                    missingPermissions.toArray(new String[0]),
                    MEDIA_PERMISSION_REQUEST_CODE);
        }
    }

    private void dispatchTouch(int action, int x, int y, long downTime) {
        int safeX = clamp(x, 0, width - 1);
        int safeY = clamp(y, 0, height - 1);

        runOnUiThread(() -> {
            if (webView == null) {
                return;
            }

            long eventTime = SystemClock.uptimeMillis();
        MotionEvent event = MotionEvent.obtain(downTime, eventTime, action, safeX, safeY, 0);
            webView.dispatchTouchEvent(event);
            event.recycle();
        });
    }

    private String buildScrollScript(int x, int y, int dx, int dy) {
        return "(function(){"
                + "var x=" + x + ",y=" + y + ",dx=" + dx + ",dy=" + dy + ";"
                + "try{"
                + "if(window.uemVrScrollBy&&window.uemVrScrollBy(dx,dy,x,y))return true;"
                + "}catch(e){}"
                + "var start=document.elementFromPoint(x,y);"
                + "try{"
                + "if(start&&typeof WheelEvent==='function'){"
                + "var wheel=new WheelEvent('wheel',{clientX:x,clientY:y,deltaX:dx,deltaY:dy,bubbles:true,cancelable:true});"
                + "start.dispatchEvent(wheel);"
                + "}"
                + "}catch(e){}"
                + "function tryScroll(el){"
                + "if(!el)return false;"
                + "var bx=el.scrollLeft||0,by=el.scrollTop||0;"
                + "try{el.scrollLeft=bx+dx;el.scrollTop=by+dy;}catch(e){}"
                + "return (el.scrollLeft||0)!==bx||(el.scrollTop||0)!==by;"
                + "}"
                + "var el=start;"
                + "while(el){"
                + "if(tryScroll(el))return true;"
                + "el=el.parentElement;"
                + "}"
                + "var root=document.scrollingElement||document.documentElement||document.body;"
                + "if(tryScroll(root))return true;"
                + "if(tryScroll(document.body))return true;"
                + "if(tryScroll(document.documentElement))return true;"
                + "var wx=window.scrollX||window.pageXOffset||0,wy=window.scrollY||window.pageYOffset||0;"
                + "window.scrollBy(dx,dy);"
                + "return (window.scrollX||window.pageXOffset||0)!==wx||(window.scrollY||window.pageYOffset||0)!==wy;"
                + "})()";
    }

    private int clamp(int value, int min, int max) {
        return Math.max(min, Math.min(max, value));
    }

    private void ensureBitmap() {
        if (bitmap != null && !bitmap.isRecycled()) {
            return;
        }

        bitmap = Bitmap.createBitmap(width, height, Bitmap.Config.ARGB_8888);
        canvas = new Canvas(bitmap);
        argbPixels = new int[width * height];
        if (workingRgbaPixels == null) {
            workingRgbaPixels = new byte[width * height * 4];
        }
    }

    private void convertArgbToRgba() {
        for (int y = 0; y < height; y++) {
            int sourceRow = y * width;
            int targetRow = (height - 1 - y) * width * 4;

            for (int x = 0; x < width; x++) {
                int argb = argbPixels[sourceRow + x];
                int j = targetRow + x * 4;
                workingRgbaPixels[j] = (byte) ((argb >> 16) & 0xff);
                workingRgbaPixels[j + 1] = (byte) ((argb >> 8) & 0xff);
                workingRgbaPixels[j + 2] = (byte) (argb & 0xff);
                workingRgbaPixels[j + 3] = (byte) ((argb >> 24) & 0xff);
            }
        }
    }

    private void runOnUiThread(Runnable runnable) {
        if (activity == null) {
            lastError = "UnityPlayer.currentActivity is null.";
            return;
        }

        activity.runOnUiThread(runnable);
    }

    private Activity resolveUnityActivity() {
        try {
            Class<?> unityPlayer = Class.forName("com.unity3d.player.UnityPlayer");
            Object value = unityPlayer.getField("currentActivity").get(null);
            if (value instanceof Activity) {
                return (Activity)value;
            }
        } catch (Throwable throwable) {
            lastError = throwable.getClass().getSimpleName() + ": " + throwable.getMessage();
        }

        return null;
    }
}
