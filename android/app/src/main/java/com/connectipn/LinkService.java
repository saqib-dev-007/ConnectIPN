package com.connectipn;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Intent;
import android.os.IBinder;

/** Keeps the process (and therefore the socket) alive while the PC is mounted. */
public class LinkService extends Service {

    private static final String CHANNEL_ID = "connectipn-link";

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        NotificationChannel channel = new NotificationChannel(
                CHANNEL_ID, "File link", NotificationManager.IMPORTANCE_LOW);
        channel.setDescription("Keeps the ConnectIPN link to your PC running");
        NotificationManager manager = getSystemService(NotificationManager.class);
        if (manager != null) {
            manager.createNotificationChannel(channel);
        }

        Notification notification = new Notification.Builder(this, CHANNEL_ID)
                .setContentTitle("ConnectIPN")
                .setContentText("Your PC can access this phone's storage")
                .setSmallIcon(R.drawable.ic_stat)
                .setOngoing(true)
                .build();
        startForeground(1, notification);
        return START_STICKY;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        LinkManager.get().init(this);
    }

    @Override
    public void onDestroy() {
        LinkManager.get().onServiceStopped();
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
