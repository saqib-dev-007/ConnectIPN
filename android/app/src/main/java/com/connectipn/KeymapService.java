package com.connectipn;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.graphics.PixelFormat;
import android.os.Build;
import android.os.IBinder;
import android.provider.Settings;
import android.view.Gravity;
import android.view.WindowManager;

/** Keeps the keymap overlay on top of whatever app the user is playing. */
public class KeymapService extends Service {

    private static final String CHANNEL_ID = "connectipn-keymap";
    private static final int NOTIFICATION_ID = 3;

    private WindowManager manager;
    private KeymapOverlayView view;

    @Override
    public void onCreate() {
        super.onCreate();
        NotificationChannel channel = new NotificationChannel(
                CHANNEL_ID, "Key mapping", NotificationManager.IMPORTANCE_LOW);
        NotificationManager notifications = getSystemService(NotificationManager.class);
        if (notifications != null) {
            notifications.createNotificationChannel(channel);
        }
        Notification notification = new Notification.Builder(this, CHANNEL_ID)
                .setContentTitle("ConnectIPN")
                .setContentText("Key mapping is active")
                .setSmallIcon(android.R.drawable.ic_menu_edit)
                .setOngoing(true)
                .build();
        startForeground(NOTIFICATION_ID, notification);

        manager = (WindowManager) getSystemService(WINDOW_SERVICE);
        if (manager == null || !Settings.canDrawOverlays(this)) {
            stopSelf();
            return;
        }
        showOverlay();
        KeymapStore.setListener(this::reload);
    }

    private void showOverlay() {
        view = new KeymapOverlayView(this);
        view.setControls(KeymapModel.parse(KeymapStore.load()));

        int flags = WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE
                | WindowManager.LayoutParams.FLAG_NOT_TOUCH_MODAL
                | WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN
                | WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS;
        WindowManager.LayoutParams params = new WindowManager.LayoutParams(
                WindowManager.LayoutParams.MATCH_PARENT,
                WindowManager.LayoutParams.MATCH_PARENT,
                Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                        ? WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
                        : WindowManager.LayoutParams.TYPE_PHONE,
                flags,
                PixelFormat.TRANSLUCENT);
        params.gravity = Gravity.TOP | Gravity.START;
        try {
            manager.addView(view, params);
        } catch (Exception e) {
            stopSelf();
        }
    }

    private void reload() {
        new android.os.Handler(getMainLooper()).post(() -> {
            if (view != null) {
                view.setControls(KeymapModel.parse(KeymapStore.load()));
            }
        });
    }

    @Override
    public void onDestroy() {
        KeymapStore.setListener(null);
        if (view != null && manager != null) {
            try {
                manager.removeView(view);
            } catch (Exception e) {
                // already detached
            }
            view = null;
        }
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
