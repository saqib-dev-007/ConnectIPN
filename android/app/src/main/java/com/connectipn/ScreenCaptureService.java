package com.connectipn;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.PixelFormat;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.IBinder;
import android.os.Looper;
import android.os.SystemClock;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.Gravity;
import android.view.WindowManager;
import android.widget.TextView;

import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;

/**
 * Captures the phone display with MediaProjection, encodes each frame as JPEG and
 * pushes it to the PC over the existing link.
 *
 * <p>Everything that touches the reader or the virtual display runs on one private
 * handler thread. Tearing the reader down from inside its own callback, or from the
 * main thread while a frame was being converted, is what used to crash the process
 * right after the first frame.
 */
public class ScreenCaptureService extends Service {

    public static final String EXTRA_RESULT_CODE = "resultCode";
    public static final String EXTRA_RESULT_DATA = "resultData";
    public static final String EXTRA_WIDTH = "width";
    public static final String EXTRA_HEIGHT = "height";
    public static final String EXTRA_FPS = "fps";
    public static final String EXTRA_QUALITY = "quality";
    public static final String EXTRA_ORIENTATION = "orientation";
    public static final String EXTRA_ASPECT = "aspect";
    public static final String EXTRA_LOCK = "lock";
    public static final String EXTRA_AUDIO = "audio";

    private static final String CHANNEL_ID = "connectipn-screen";
    private static final int NOTIFICATION_ID = 2;
    private static final String TAG = "ConnectIPN-screen";

    private static volatile boolean running;
    private static volatile ScreenCaptureService instance;
    private static volatile int framesSent;
    private static volatile String lastError = "";
    private static volatile boolean audioRunning;

    private MediaProjection projection;
    private VirtualDisplay display;
    private ImageReader reader;
    private HandlerThread thread;
    private Handler handler;

    /** Written by the frame thread on reconfigure, read by the UI for the stats line. */
    private volatile int width;
    private volatile int height;
    private int fps = 24;
    private int quality = 60;
    private int orientation = Protocol.ORIENT_AUTO;
    private int aspect = Protocol.ASPECT_NATIVE;

    private int sequence;
    private long lastFrameAt;
    private boolean lockRequested;

    private final Object bitmapLock = new Object();
    private Bitmap reusable;
    private Bitmap cropped;
    private final ByteArrayOutputStream jpegBuffer = new ByteArrayOutputStream(256 * 1024);

    private final Handler main = new Handler(Looper.getMainLooper());
    private TextView lockOverlay;
    private ScreenAudioCapture audio;
    private boolean audioEnabled;

    /** The PC can change resolution, fps or quality while the stream is running. */
    private final LinkClient.FrameListener configListener = new LinkClient.FrameListener() {
        @Override
        public void onScreenConfig(int mode, int width, int height, int fps,
                                   int quality, int orientation, int aspect) {
            if (mode == Protocol.SCREEN_START_PHONE) {
                if (running) {
                    reconfigure(width, height, fps, quality, orientation, aspect);
                } else {
                    // The projection is gone (refused, or the user stopped it). A
                    // STATUS report keeps the PC from waiting for frames forever.
                    LinkManager.get().sendStatus();
                }
            } else if (mode == Protocol.SCREEN_STOP_PHONE) {
                if (running) {
                    stopSelf();
                }
            }
        }
    };

    public static boolean isRunning() {
        return running;
    }

    public static int framesSent() {
        return framesSent;
    }

    public static String lastError() {
        return lastError;
    }

    public static boolean isAudioRunning() {
        return audioRunning;
    }

    /** Frame size the stream is actually using right now, or 0 when idle. */
    public static int currentWidth() {
        ScreenCaptureService service = instance;
        return service != null && running ? service.width : 0;
    }

    public static int currentHeight() {
        ScreenCaptureService service = instance;
        return service != null && running ? service.height : 0;
    }

    /**
     * Re-announces an active stream after the link comes back, so a PC that
     * (re)joined late starts receiving frames again without the phone re-scanning
     * anything.
     */
    public static void announce() {
        ScreenCaptureService service = instance;
        if (service == null || !running) {
            return;
        }
        LinkManager.get().sendScreenConfig(
                Protocol.SCREEN_START_PHONE, service.width, service.height, service.fps,
                service.quality, service.orientation, service.aspect);
    }

    /**
     * Applies new stream settings chosen on the phone itself.
     *
     * <p>Without this the resolution / frame rate / quality buttons only told the PC
     * what the stream *claimed* to be: the PC treats SCREEN_START_PHONE as a status
     * flag and never echoes it back, so the phone kept encoding at the size the
     * stream started with. reconfigure() reports the new shape to the PC itself.
     */
    public static void applyConfig(int width, int height, int fps, int quality,
                                   int orientation, int aspect) {
        ScreenCaptureService service = instance;
        if (service == null || !running) {
            return;
        }
        service.reconfigure(width, height, fps, quality, orientation, aspect);
    }

    /** Toggles the dim cover while the projection keeps running. */
    public static void setLockOverlay(boolean on) {
        ScreenCaptureService service = instance;
        if (service == null) {
            return;
        }
        service.lockRequested = on;
        if (on) {
            service.showLockOverlay();
        } else {
            service.hideLockOverlay();
        }
    }

    @Override
    public void onCreate() {
        super.onCreate();
        instance = this;
        Notification notification = buildNotification();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            startForeground(NOTIFICATION_ID, notification,
                    ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);
        } else {
            startForeground(NOTIFICATION_ID, notification);
        }
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent == null) {
            return START_NOT_STICKY;
        }
        int resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, 0);
        Intent resultData = intent.getParcelableExtra(EXTRA_RESULT_DATA);
        if (resultData == null) {
            stopSelf();
            return START_NOT_STICKY;
        }

        width = intent.getIntExtra(EXTRA_WIDTH, 720);
        height = intent.getIntExtra(EXTRA_HEIGHT, 1280);
        fps = Math.max(1, Math.min(60, intent.getIntExtra(EXTRA_FPS, 24)));
        quality = Math.max(10, Math.min(95, intent.getIntExtra(EXTRA_QUALITY, 60)));
        orientation = intent.getIntExtra(EXTRA_ORIENTATION, Protocol.ORIENT_AUTO);
        aspect = intent.getIntExtra(EXTRA_ASPECT, Protocol.ASPECT_NATIVE);
        lockRequested = intent.getBooleanExtra(EXTRA_LOCK, false);
        audioEnabled = intent.getBooleanExtra(EXTRA_AUDIO, false);

        applyShape();
        teardown();
        startCapture(resultCode, resultData);
        return START_NOT_STICKY;
    }

    /** Swaps the requested size for the chosen orientation and aspect ratio. */
    private void applyShape() {
        if (width <= 0 || height <= 0) {
            DisplayMetrics metrics = getResources().getDisplayMetrics();
            width = Math.min(metrics.widthPixels, 1280);
            height = Math.round(width * (float) metrics.heightPixels / metrics.widthPixels);
        }
        boolean landscape = width > height;
        if (orientation == Protocol.ORIENT_PORTRAIT && landscape) {
            int swap = width;
            width = height;
            height = swap;
        } else if (orientation == Protocol.ORIENT_LANDSCAPE && !landscape) {
            int swap = width;
            width = height;
            height = swap;
        }
        if (aspect == Protocol.ASPECT_16_9) {
            height = Math.round(width * 9f / 16f);
        } else if (aspect == Protocol.ASPECT_4_3) {
            height = Math.round(width * 3f / 4f);
        }
        width = Math.max(160, Math.min(3840, width));
        height = Math.max(160, Math.min(3840, height));
    }

    private void startCapture(int resultCode, Intent resultData) {
        MediaProjectionManager manager =
                (MediaProjectionManager) getSystemService(Context.MEDIA_PROJECTION_SERVICE);
        if (manager == null) {
            fail("This phone has no screen capture service.");
            return;
        }

        thread = new HandlerThread("connectipn-screen");
        thread.start();
        handler = new Handler(thread.getLooper());

        try {
            projection = manager.getMediaProjection(resultCode, resultData);
        } catch (Exception e) {
            projection = null;
            fail("Screen capture was refused: " + e.getMessage());
            return;
        }
        if (projection == null) {
            fail("Screen capture was refused by the system.");
            return;
        }

        // Android 14 refuses createVirtualDisplay until a callback is registered.
        projection.registerCallback(new MediaProjection.Callback() {
            @Override
            public void onStop() {
                stopSelf();
            }
        }, handler);

        handler.post(() -> {
            try {
                buildDisplay(projection);
            } catch (Throwable t) {
                fail("Could not start the virtual display: " + t);
                stopSelf();
                return;
            }

            LinkManager.get().addFrameListener(configListener);
            running = true;
            sequence = 0;
            lastFrameAt = 0;
            framesSent = 0;
            lastError = "";

            if (lockRequested) {
                showLockOverlay();
            }

            startAudio(projection);

            LinkManager.get().sendScreenConfig(
                    Protocol.SCREEN_START_PHONE, width, height, fps, quality, orientation, aspect);
            LinkManager.get().sendStatus();
        });
    }

    /** Creates the reader and the mirrored display. Must run on the handler thread. */
    private void buildDisplay(MediaProjection active) {
        reader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 2);
        reader.setOnImageAvailableListener(this::onImage, handler);
        display = active.createVirtualDisplay(
                "connectipn",
                width,
                height,
                getResources().getDisplayMetrics().densityDpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR,
                reader.getSurface(),
                null,
                handler);
    }

    /**
     * Applies new stream settings without asking for consent again.
     *
     * <p>The virtual display is only rebuilt when the frame size actually changes.
     * Changing fps or JPEG quality used to tear the display down and back up for no
     * reason, which is what made the link look like it had dropped.
     */
    private void reconfigure(int w, int h, int newFps, int newQuality, int newOrientation, int newAspect) {
        Handler target = handler;
        MediaProjection active = projection;
        if (target == null || active == null || !running) {
            return;
        }
        target.post(() -> {
            try {
                int previousWidth = width;
                int previousHeight = height;

                if (w > 0) {
                    width = w;
                }
                if (h > 0) {
                    height = h;
                }
                fps = Math.max(1, Math.min(60, newFps));
                quality = Math.max(10, Math.min(95, newQuality));
                orientation = newOrientation;
                aspect = newAspect;
                applyShape();

                if (width != previousWidth || height != previousHeight) {
                    if (display != null) {
                        display.release();
                        display = null;
                    }
                    if (reader != null) {
                        reader.close();
                        reader = null;
                    }
                    dropBitmaps();
                    buildDisplay(active);
                }

                LinkManager.get().sendScreenConfig(
                        Protocol.SCREEN_START_PHONE, width, height, fps, quality, orientation, aspect);
            } catch (Throwable t) {
                lastError = "Reconfiguring the stream failed: " + t;
                Log.w(TAG, lastError);
                // The old display is gone; restart from scratch on the next request.
                running = false;
                stopSelf();
            }
        });
    }

    private void startAudio(MediaProjection active) {
        if (!audioEnabled) {
            return;
        }
        ScreenAudioCapture capture = new ScreenAudioCapture();
        audioRunning = capture.start(active);
        if (audioRunning) {
            audio = capture;
        } else {
            lastError = "Phone audio capture is not available on this device.";
        }
    }

    private void stopAudio() {
        ScreenAudioCapture capture = audio;
        audio = null;
        audioRunning = false;
        if (capture != null) {
            capture.stop();
        }
    }

    private void onImage(ImageReader source) {
        Image image = null;
        try {
            image = source.acquireLatestImage();
            if (image == null) {
                return;
            }
            if (!running) {
                return;
            }

            long now = SystemClock.elapsedRealtime();
            long interval = 1000L / Math.max(1, fps);
            if (now - lastFrameAt < interval) {
                return;
            }
            lastFrameAt = now;

            if (LinkManager.get().state() != LinkManager.State.CONNECTED) {
                // Post instead of stopping here: tearing the reader down from inside
                // its own callback is what used to crash the process.
                Handler target = handler;
                if (target != null) {
                    target.post(this::stopSelf);
                }
                return;
            }

            Bitmap bitmap = toBitmap(image);
            if (bitmap == null) {
                return;
            }

            jpegBuffer.reset();
            if (!bitmap.compress(Bitmap.CompressFormat.JPEG, quality, jpegBuffer)) {
                lastError = "The frame could not be encoded.";
                return;
            }
            byte[] jpeg = jpegBuffer.toByteArray();
            LinkManager.get().sendScreenFrame(jpeg, bitmap.getWidth(), bitmap.getHeight(),
                    Protocol.SCREEN_PHONE, sequence++);
            framesSent++;
        } catch (Throwable t) {
            // A single bad frame (including an out-of-memory spike) must never kill
            // the process, and it must never stop the reader either.
            lastError = "Frame dropped: " + t;
            Log.w(TAG, lastError);
        } finally {
            if (image != null) {
                try {
                    image.close();
                } catch (Throwable ignored) {
                    // the reader may already be closed by teardown
                }
            }
        }
    }

    /**
     * Converts an RGBA_8888 Image, honouring the row padding some GPUs add.
     *
     * Both bitmaps are reused across frames. Allocating a new one per frame at 24 fps
     * is what made the process run out of memory after the first few frames.
     */
    private Bitmap toBitmap(Image image) {
        Image.Plane plane = image.getPlanes()[0];
        ByteBuffer buffer = plane.getBuffer();
        int pixelStride = plane.getPixelStride();
        int rowStride = plane.getRowStride();
        int imageWidth = image.getWidth();
        int imageHeight = image.getHeight();
        int rowPadding = rowStride - pixelStride * imageWidth;
        int paddedWidth = imageWidth + rowPadding / pixelStride;

        // A teardown on another thread may recycle these; the lock keeps a frame from
        // being drawn into a recycled bitmap.
        synchronized (bitmapLock) {
            if (reusable == null || reusable.getWidth() != paddedWidth
                    || reusable.getHeight() != imageHeight) {
                if (reusable != null) {
                    reusable.recycle();
                }
                reusable = Bitmap.createBitmap(paddedWidth, imageHeight, Bitmap.Config.ARGB_8888);
            }
            buffer.rewind();
            reusable.copyPixelsFromBuffer(buffer);

            if (paddedWidth == imageWidth) {
                return reusable;
            }

            if (cropped == null || cropped.getWidth() != imageWidth
                    || cropped.getHeight() != imageHeight) {
                if (cropped != null) {
                    cropped.recycle();
                }
                cropped = Bitmap.createBitmap(imageWidth, imageHeight, Bitmap.Config.ARGB_8888);
            }
            new Canvas(cropped).drawBitmap(reusable, 0f, 0f, null);
            return cropped;
        }
    }

    private void showLockOverlay() {
        main.post(this::addLockOverlay);
    }

    private void addLockOverlay() {
        if (lockOverlay != null || !android.provider.Settings.canDrawOverlays(this)) {
            return;
        }
        WindowManager manager = (WindowManager) getSystemService(WINDOW_SERVICE);
        if (manager == null) {
            return;
        }
        TextView cover = new TextView(this);
        cover.setBackgroundColor(0xCC000000);
        cover.setText("Screen dimmed\nDouble-tap to restore");
        cover.setTextColor(0xFF3A3A3A);
        cover.setTextSize(13);
        cover.setGravity(Gravity.CENTER);
        cover.setOnClickListener(v -> {
            if (v.getTag() == null) {
                v.setTag(Boolean.TRUE);
                v.postDelayed(() -> v.setTag(null), 1500);
            } else {
                hideLockOverlay();
            }
        });

        WindowManager.LayoutParams params = new WindowManager.LayoutParams(
                WindowManager.LayoutParams.MATCH_PARENT,
                WindowManager.LayoutParams.MATCH_PARENT,
                Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                        ? WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
                        : WindowManager.LayoutParams.TYPE_PHONE,
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN
                        | WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON,
                PixelFormat.OPAQUE);
        try {
            manager.addView(cover, params);
            lockOverlay = cover;
        } catch (Exception e) {
            lockOverlay = null;
        }
    }

    private void hideLockOverlay() {
        main.post(this::removeLockOverlay);
    }

    private void removeLockOverlay() {
        TextView cover = lockOverlay;
        lockOverlay = null;
        if (cover == null) {
            return;
        }
        WindowManager manager = (WindowManager) getSystemService(WINDOW_SERVICE);
        try {
            manager.removeView(cover);
        } catch (Exception e) {
            // already gone
        }
    }

    private void dropBitmaps() {
        synchronized (bitmapLock) {
            if (reusable != null) {
                reusable.recycle();
                reusable = null;
            }
            if (cropped != null) {
                cropped.recycle();
                cropped = null;
            }
        }
    }

    /**
     * Releases the projection. Safe to call from any thread: the work is handed to the
     * frame thread, which is the only owner of the reader and the display.
     */
    private void teardown() {
        running = false;
        LinkManager.get().removeFrameListener(configListener);
        Handler target = handler;
        HandlerThread owner = thread;
        handler = null;
        thread = null;

        // Snapshot everything the worker touches. The instance fields belong to the
        // next capture session the moment they are nulled here, so the runnable must
        // only use its own captures — touching the fields would close a freshly
        // rebuilt display when mirroring is restarted quickly (for example when the
        // PC sends stop/start back to back).
        VirtualDisplay expiredDisplay = display;
        display = null;
        ImageReader expiredReader = reader;
        reader = null;
        Bitmap expiredReusable;
        Bitmap expiredCropped;
        synchronized (bitmapLock) {
            expiredReusable = reusable;
            reusable = null;
            expiredCropped = cropped;
            cropped = null;
        }
        ScreenAudioCapture expiredAudio = audio;
        audio = null;
        audioRunning = false;

        Runnable work = () -> {
            hideLockOverlay();
            if (expiredAudio != null) {
                expiredAudio.stop();
            }
            if (expiredDisplay != null) {
                try {
                    expiredDisplay.release();
                } catch (Throwable ignored) {
                    // already released
                }
            }
            if (expiredReader != null) {
                try {
                    expiredReader.close();
                } catch (Throwable ignored) {
                    // already closed
                }
            }
            synchronized (bitmapLock) {
                if (expiredReusable != null) {
                    expiredReusable.recycle();
                }
                if (expiredCropped != null) {
                    expiredCropped.recycle();
                }
            }
        };

        if (target != null) {
            target.post(work);
        }
        MediaProjection active = projection;
        projection = null;
        if (active != null) {
            try {
                active.stop();
            } catch (Throwable ignored) {
                // the system may have already revoked the projection
            }
        }
        if (owner != null) {
            owner.quitSafely();
        }
    }

    private void fail(String message) {
        lastError = message;
        Log.w(TAG, message);
        running = false;
        stopSelf();
    }

    @Override
    public void onDestroy() {
        instance = null;
        // The frame-listener slot may already belong to DesktopViewActivity by now,
        // so leave it alone; reconfigure() is a no-op once the projection is gone.
        teardown();
        LinkManager.get().sendScreenConfig(
                Protocol.SCREEN_STOP_PHONE, 0, 0, 1, 10, Protocol.ORIENT_AUTO, Protocol.ASPECT_NATIVE);
        super.onDestroy();
    }

    private Notification buildNotification() {
        NotificationChannel channel = new NotificationChannel(
                CHANNEL_ID, "Screen mirroring", NotificationManager.IMPORTANCE_LOW);
        channel.setDescription("Sends this phone's screen to your PC");
        NotificationManager manager = getSystemService(NotificationManager.class);
        if (manager != null) {
            manager.createNotificationChannel(channel);
        }
        return new Notification.Builder(this, CHANNEL_ID)
                .setContentTitle("ConnectIPN")
                .setContentText("Mirroring this screen to your PC")
                .setSmallIcon(R.drawable.ic_stat)
                .setOngoing(true)
                .build();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
