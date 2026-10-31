package com.connectipn;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.GestureDescription;
import android.graphics.Path;
import android.os.Handler;
import android.os.Looper;
import android.util.DisplayMetrics;
import android.view.accessibility.AccessibilityEvent;

/**
 * Injects touches into whatever app is on screen, so the PC can drive the phone.
 * Android only allows this through an accessibility service, which the user enables
 * once in Settings.
 */
public class RemoteControlService extends AccessibilityService {

    private static volatile RemoteControlService instance;

    private final Handler main = new Handler(Looper.getMainLooper());

    public static boolean isReady() {
        return instance != null;
    }

    @Override
    protected void onServiceConnected() {
        super.onServiceConnected();
        instance = this;
        // Tell the PC straight away that it can now tap and swipe this screen.
        LinkManager.get().sendStatus();
    }

    @Override
    public void onDestroy() {
        instance = null;
        super.onDestroy();
    }

    @Override
    public void onAccessibilityEvent(AccessibilityEvent event) {
        // Gestures only; no event stream is needed.
    }

    @Override
    public void onInterrupt() {
        // nothing to interrupt
    }

    /** Called from the link thread; posts to the main thread before dispatching. */
    public static void dispatch(int action, int x1, int y1, int x2, int y2, int duration, int key) {
        RemoteControlService service = instance;
        if (service == null) {
            return;
        }
        service.main.post(() -> service.apply(action, x1, y1, x2, y2, duration, key));
    }

    private void apply(int action, int x1, int y1, int x2, int y2, int duration, int key) {
        if (action == Protocol.PHONE_KEY) {
            switch (key) {
                case Protocol.ANDROID_KEY_BACK:
                    performGlobalAction(GLOBAL_ACTION_BACK);
                    break;
                case Protocol.ANDROID_KEY_HOME:
                    performGlobalAction(GLOBAL_ACTION_HOME);
                    break;
                case Protocol.ANDROID_KEY_RECENTS:
                    performGlobalAction(GLOBAL_ACTION_RECENTS);
                    break;
                default:
                    break;
            }
            return;
        }

        // Real metrics: the gesture has to land on the same pixels the capture saw,
        // which includes the area under the status and navigation bars.
        DisplayMetrics metrics = new DisplayMetrics();
        android.view.WindowManager windows =
                (android.view.WindowManager) getSystemService(WINDOW_SERVICE);
        if (windows != null && windows.getDefaultDisplay() != null) {
            windows.getDefaultDisplay().getRealMetrics(metrics);
        } else {
            metrics = getResources().getDisplayMetrics();
        }
        float ax1 = x1 / 65535f * metrics.widthPixels;
        float ay1 = y1 / 65535f * metrics.heightPixels;
        float ax2 = x2 / 65535f * metrics.widthPixels;
        float ay2 = y2 / 65535f * metrics.heightPixels;
        boolean swipe = action == Protocol.PHONE_SWIPE;
        int ms = Math.max(30, Math.min(1500, duration));

        Path path = new Path();
        path.moveTo(ax1, ay1);
        if (swipe) {
            path.lineTo(ax2, ay2);
        }
        GestureDescription gesture = new GestureDescription.Builder()
                .addStroke(new GestureDescription.StrokeDescription(path, 0, ms))
                .build();
        try {
            dispatchGesture(gesture, null, null);
        } catch (Exception e) {
            // The service can be torn down between the check and the call.
        }
    }
}
