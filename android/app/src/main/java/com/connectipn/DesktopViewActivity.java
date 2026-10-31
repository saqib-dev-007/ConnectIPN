package com.connectipn;

import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import android.hardware.Sensor;
import android.hardware.SensorEvent;
import android.hardware.SensorEventListener;
import android.hardware.SensorManager;
import android.os.Bundle;
import android.os.SystemClock;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewConfiguration;
import android.view.WindowManager;
import android.view.animation.DecelerateInterpolator;
import android.widget.Button;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;

import androidx.appcompat.app.AppCompatActivity;

import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Shows the PC screen on the phone.
 *
 * Touch drags move the PC pointer, a tap clicks, two fingers right-click and scroll.
 * The "Keys" button adds a transparent keyboard, "Controller" adds a virtual Xbox pad
 * plus mouse-look on the stream, and the quality button trades sharpness for latency.
 */
public class DesktopViewActivity extends AppCompatActivity
        implements LinkManager.Listener, SensorEventListener {

    /** Tilt that counts as a full deflection, about 31 degrees. */
    private static final float MAX_TILT_RAD = 0.55f;
    private static final float DEAD_ZONE = 0.06f;
    private static final int TRIGGER_ON = 255;

    public static final String EXTRA_GAMING = "gaming";
    public static final String EXTRA_WIDTH = "width";
    public static final String EXTRA_HEIGHT = "height";
    public static final String EXTRA_FPS = "fps";
    public static final String EXTRA_QUALITY = "quality";

    private static final int[][] QUALITY = {
            { 2, 15, 40 },   // low latency
            { 1, 24, 60 },   // balanced
            { 1, 30, 80 },   // sharp
    };

    /** Capture sizes for the PC screen; 16:9 keeps the desktop undistorted. */
    private static final int[][] CAPTURE = {
            { 960, 540 },
            { 1280, 720 },
            { 1920, 1080 },
    };
    private static final String[] QUALITY_NAMES = { "Low latency", "Balanced", "Sharp" };

    /** How the PC picture is scaled into the phone screen. */
    private static final String[] ASPECT_NAMES = { "Fit", "Fill", "Stretch" };
    private static final ImageView.ScaleType[] ASPECT_SCALES = {
            ImageView.ScaleType.FIT_CENTER,   // whole desktop, black bars
            ImageView.ScaleType.CENTER_CROP,  // fills the screen, edges cropped
            ImageView.ScaleType.FIT_XY,       // fills the screen, slightly stretched
    };

    private static final String[][] KEY_ROWS = {
            { "Esc", "Tab", "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P" },
            { "A", "S", "D", "F", "G", "H", "J", "K", "L", "Back" },
            { "Shift", "Z", "X", "C", "V", "B", "N", "M", "Enter" },
            { "Ctrl", "Alt", "Space", "\u2190", "\u2191", "\u2193", "\u2192" },
    };

    private final LinkManager link = LinkManager.get();
    private final ExecutorService decoder = Executors.newSingleThreadExecutor(r -> {
        Thread t = new Thread(r, "connectipn-decode");
        t.setDaemon(true);
        return t;
    });
    private final AtomicBoolean decoding = new AtomicBoolean();

    private ImageView image;
    private TextView status;
    private TextView statsText;
    private Button retryButton;
    private FrameLayout overlayLayer;
    private Button qualityButton;
    private Button aspectButton;
    private Button keysButton;
    private Button padButton;
    private LinearLayout topBar;
    private TextView expandButton;
    private TextView hintText;

    private boolean gaming;
    private int baseWidth;
    private int baseHeight;
    private int qualityIndex = 1;
    private int aspectIndex;
    private boolean barHidden;
    private boolean streaming;

    private final android.os.Handler ui = new android.os.Handler(android.os.Looper.getMainLooper());
    private int frameCount;
    private int lastWidth;
    private int lastHeight;
    private long lastFrameAt;
    private long streamStartedAt;
    private long fpsWindowAt;
    private int fpsWindowFrames;

    private final Runnable ticker = new Runnable() {
        @Override
        public void run() {
            updateStatus();
            ui.postDelayed(this, 500);
        }
    };

    private Bitmap current;

    private boolean padVisible;
    private boolean keysVisible;
    private JoystickView stick;
    private TextView gyroButton;
    private TextView mouseButton;

    /**
     * Touch on the video drives the PC pointer. While the controller is up that turns
     * every brush of the screen into a click somewhere on the desktop, so the pad's
     * Mouse button can switch it off. Always restored when the pad closes.
     */
    private boolean mouseEnabled = true;

    private SensorManager sensors;
    private Sensor accelerometer;
    private final float[] gravity = new float[3];
    private boolean haveGravity;
    private boolean calibrated;
    private float neutralSteer;
    private float neutralPitch;
    private int lastSentX;
    private int lastSentY;
    private boolean gyro;

    private float lastX;
    private float lastY;
    private float downX;
    private float downY;
    private float scrollCarry;
    private long downAt;
    private boolean dragging;
    private boolean twoFingers;
    private int touchSlop;

    private final LinkClient.FrameListener frames = new LinkClient.FrameListener() {
        @Override
        public void onScreenFrame(byte[] jpeg, int width, int height, int source, int seq) {
            if (source != Protocol.SCREEN_DESKTOP) {
                return;
            }
            handleFrame(jpeg);
        }

        @Override
        public void onScreenConfig(int mode, int width, int height, int fps,
                                   int quality, int orientation, int aspect) {
            if (mode == Protocol.SCREEN_STOP_DESKTOP) {
                runOnUiThread(DesktopViewActivity.this::finish);
            }
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_desktop);
        SystemBars.apply(this, R.id.root);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);

        image = findViewById(R.id.screenImage);
        status = findViewById(R.id.statusText);
        statsText = findViewById(R.id.statsText);
        retryButton = findViewById(R.id.retryButton);
        retryButton.setOnClickListener(v -> {
            retryButton.setVisibility(View.GONE);
            restartStream();
        });
        overlayLayer = findViewById(R.id.overlayLayer);
        qualityButton = findViewById(R.id.qualityButton);
        aspectButton = findViewById(R.id.aspectButton);
        keysButton = findViewById(R.id.keysButton);
        padButton = findViewById(R.id.padButton);
        topBar = findViewById(R.id.topBar);
        expandButton = findViewById(R.id.expandButton);
        hintText = findViewById(R.id.hintText);
        touchSlop = ViewConfiguration.get(this).getScaledTouchSlop();
        sensors = (SensorManager) getSystemService(SENSOR_SERVICE);
        accelerometer = sensors == null ? null : sensors.getDefaultSensor(Sensor.TYPE_ACCELEROMETER);

        gaming = getIntent().getBooleanExtra(EXTRA_GAMING, false);
        baseWidth = Math.max(320, getIntent().getIntExtra(EXTRA_WIDTH, 1280));
        baseHeight = Math.max(240, getIntent().getIntExtra(EXTRA_HEIGHT, 720));

        ((TextView) findViewById(R.id.titleText)).setText(
                gaming ? R.string.screen_gaming : R.string.desktop_title);

        findViewById(R.id.backButton).setOnClickListener(v -> finish());
        qualityButton.setOnClickListener(v -> {
            qualityIndex = (qualityIndex + 1) % QUALITY.length;
            qualityButton.setText(QUALITY_NAMES[qualityIndex]);
            if (streaming) {
                restartStream();
            }
        });
        keysButton.setOnClickListener(v -> toggleKeyboard());
        padButton.setOnClickListener(v -> togglePad());

        aspectButton.setOnClickListener(v -> {
            aspectIndex = (aspectIndex + 1) % ASPECT_NAMES.length;
            aspectButton.setText(ASPECT_NAMES[aspectIndex]);
            image.setScaleType(ASPECT_SCALES[aspectIndex]);
        });

        findViewById(R.id.collapseButton).setOnClickListener(v -> collapseBar());
        expandButton.setOnClickListener(v -> expandBar());

        wireTouch();

        // A phone cannot send and receive a screen at the same time; stop the
        // outgoing mirror before the incoming stream starts.
        if (ScreenCaptureService.isRunning()) {
            stopService(new android.content.Intent(this, ScreenCaptureService.class));
        }

        if (gaming) {
            togglePad();
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        link.setListener(this);
        link.addFrameListener(frames);
        startStream();
        if (gyro) {
            registerSensor();
        }
        ui.removeCallbacks(ticker);
        ui.post(ticker);
    }

    @Override
    protected void onPause() {
        ui.removeCallbacks(ticker);
        link.clearListener(this);
        link.removeFrameListener(frames);
        if (sensors != null) {
            sensors.unregisterListener(this);
        }
        stopStream();
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        ui.removeCallbacks(ticker);
        stopStream();
        if (padVisible) {
            link.padConnect(false);
        }
        if (current != null) {
            current.recycle();
            current = null;
        }
        decoder.shutdownNow();
        SystemBars.restore(this);
        super.onDestroy();
    }

    @Override
    public void onLinkState(LinkManager.State state, String message) {
        runOnUiThread(() -> {
            switch (state) {
                case CONNECTED:
                    // A dropped link lands back here once the retry succeeds: bring
                    // the stream up again instead of leaving a silent black screen.
                    if (!streaming) {
                        startStream();
                    }
                    break;
                case CONNECTING:
                    status.setText(R.string.desktop_reconnecting);
                    status.setVisibility(View.VISIBLE);
                    break;
                case IDLE:
                case ERROR:
                    if (!link.isPaired()) {
                        // The pairing is truly gone: nothing to wait for.
                        Toast.makeText(this, R.string.desktop_disconnected, Toast.LENGTH_SHORT).show();
                        finish();
                    } else {
                        // The manager retries with backoff; keep the viewer open.
                        status.setText(R.string.desktop_reconnecting);
                        status.setVisibility(View.VISIBLE);
                    }
                    break;
            }
        });
    }

    private void startStream() {
        if (link.state() != LinkManager.State.CONNECTED) {
            status.setText(R.string.desktop_waiting);
            status.setVisibility(View.VISIBLE);
            return;
        }
        int[] q = QUALITY[qualityIndex];
        int[] size = CAPTURE[qualityIndex];
        frameCount = 0;
        fpsWindowFrames = 0;
        fpsWindowAt = SystemClock.elapsedRealtime();
        streamStartedAt = fpsWindowAt;
        lastFrameAt = 0;
        link.sendScreenConfig(Protocol.SCREEN_START_DESKTOP, size[0], size[1], q[1], q[2],
                Protocol.ORIENT_AUTO, Protocol.ASPECT_NATIVE);
        streaming = true;
        status.setText(R.string.desktop_connecting);
        status.setVisibility(View.VISIBLE);
        retryButton.setVisibility(View.GONE);
        statsText.setVisibility(View.GONE);
    }

    /** Keeps the "no picture yet" hint honest instead of leaving a silent black screen. */
    private void updateStatus() {
        if (!streaming) {
            return;
        }
        long now = SystemClock.elapsedRealtime();
        boolean starved = frameCount == 0
                ? now - streamStartedAt > 8000
                : now - lastFrameAt > 3000;
        if (starved) {
            status.setText(R.string.desktop_no_frames);
            status.setVisibility(View.VISIBLE);
            retryButton.setVisibility(View.VISIBLE);
        }
        if (frameCount > 0) {
            long span = now - fpsWindowAt;
            if (span >= 1000) {
                int fps = (int) Math.round(fpsWindowFrames * 1000.0 / Math.max(1, span));
                statsText.setText(getString(R.string.desktop_stats, fps, lastWidth, lastHeight));
                fpsWindowAt = now;
                fpsWindowFrames = 0;
            }
        }
    }

    private void stopStream() {
        if (!streaming) {
            return;
        }
        streaming = false;
        link.sendScreenConfig(Protocol.SCREEN_STOP_DESKTOP, 0, 0, 1, 10,
                Protocol.ORIENT_AUTO, Protocol.ASPECT_NATIVE);
    }

    private void restartStream() {
        stopStream();
        startStream();
    }

    // --- collapsible toolbar ----------------------------------------------------

    /**
     * Slides the toolbar up and out of the way, then leaves a small dim handle so it
     * can come back. The FPS/size read-out and the bottom hint go with it: they are
     * exactly the clutter someone hides the bar to get rid of.
     */
    private void collapseBar() {
        if (barHidden) {
            return;
        }
        barHidden = true;
        final int travel = topBar.getHeight() + dp(20);
        topBar.animate().cancel();
        topBar.animate()
                .translationY(-travel)
                .alpha(0f)
                .setDuration(220)
                .setInterpolator(new DecelerateInterpolator())
                .withEndAction(() -> {
                    topBar.setVisibility(View.GONE);
                    topBar.setTranslationY(0f);
                    topBar.setAlpha(1f);
                    expandButton.setVisibility(View.VISIBLE);
                    expandButton.setAlpha(0f);
                    expandButton.animate().alpha(0.34f).setDuration(180).start();
                })
                .start();
        fadeOut(statsText);
        fadeOut(hintText);
    }

    private void expandBar() {
        if (!barHidden) {
            return;
        }
        barHidden = false;
        expandButton.animate().cancel();
        expandButton.animate().alpha(0f).setDuration(120)
                .withEndAction(() -> expandButton.setVisibility(View.GONE)).start();

        topBar.setVisibility(View.VISIBLE);
        // The height is only known once it is laid out again, so slide in from
        // off-screen after the measure pass rather than from a guessed offset.
        topBar.post(() -> {
            int travel = topBar.getHeight() + dp(20);
            topBar.setTranslationY(-travel);
            topBar.setAlpha(0f);
            topBar.animate()
                    .translationY(0f)
                    .alpha(1f)
                    .setDuration(240)
                    .setInterpolator(new DecelerateInterpolator())
                    .start();
        });

        if (frameCount > 0) {
            fadeIn(statsText);
        }
        fadeIn(hintText);
    }

    private void fadeOut(View view) {
        if (view.getVisibility() != View.VISIBLE) {
            return;
        }
        view.animate().cancel();
        view.animate().alpha(0f).setDuration(160).withEndAction(() -> {
            view.setVisibility(View.GONE);
            view.setAlpha(1f);
        }).start();
    }

    private void fadeIn(View view) {
        view.animate().cancel();
        view.setVisibility(View.VISIBLE);
        view.setAlpha(0f);
        view.animate().alpha(1f).setDuration(200).start();
    }

    private void handleFrame(byte[] jpeg) {
        if (!decoding.compareAndSet(false, true)) {
            // The decoder is behind: dropping the newest frame keeps latency flat.
            return;
        }
        try {
            decoder.execute(() -> decode(jpeg));
        } catch (java.util.concurrent.RejectedExecutionException e) {
            // The activity is going away; release the gate so nothing stays stuck.
            decoding.set(false);
        }
    }

    private void decode(byte[] jpeg) {
        Bitmap bitmap = null;
        try {
            // Never decode bigger than the view can show: a 1920x1080 frame is 8 MB
            // as ARGB_8888 and two of those at once is enough to kill a mid-range phone.
            BitmapFactory.Options bounds = new BitmapFactory.Options();
            bounds.inJustDecodeBounds = true;
            BitmapFactory.decodeByteArray(jpeg, 0, jpeg.length, bounds);
            int targetWidth = Math.max(1, image.getWidth());
            int sample = 1;
            while (bounds.outWidth / (sample * 2) >= targetWidth) {
                sample *= 2;
            }

            BitmapFactory.Options options = new BitmapFactory.Options();
            options.inSampleSize = sample;
            bitmap = BitmapFactory.decodeByteArray(jpeg, 0, jpeg.length, options);
        } catch (Throwable ignored) {
            // a torn frame is not worth reporting
        }
        decoding.set(false);
        if (bitmap == null) {
            return;
        }
        final Bitmap decoded = bitmap;
        runOnUiThread(() -> {
            Bitmap old = current;
            current = decoded;
            image.setImageBitmap(decoded);
            frameCount++;
            fpsWindowFrames++;
            lastWidth = decoded.getWidth();
            lastHeight = decoded.getHeight();
            lastFrameAt = SystemClock.elapsedRealtime();
            status.setVisibility(View.GONE);
            retryButton.setVisibility(View.GONE);
            // While the toolbar is away the read-out stays away too; it comes back
            // with the bar rather than blinking on at the next frame.
            if (!barHidden) {
                statsText.setVisibility(View.VISIBLE);
            }
            if (old != null) {
                old.recycle();
            }
        });
    }

    // --- touch -> mouse ---------------------------------------------------------

    private void wireTouch() {
        image.setOnTouchListener((v, event) -> {
            // The pad's Mouse button owns this: while it is off, a stray thumb must not
            // click, drag or scroll anything on the PC.
            if (!mouseEnabled) {
                return true;
            }
            switch (event.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    lastX = downX = event.getX();
                    lastY = downY = event.getY();
                    downAt = event.getEventTime();
                    dragging = false;
                    twoFingers = false;
                    scrollCarry = 0f;
                    return true;

                case MotionEvent.ACTION_POINTER_DOWN:
                    twoFingers = true;
                    return true;

                case MotionEvent.ACTION_MOVE:
                    float x = event.getX();
                    float y = event.getY();
                    if (twoFingers) {
                        scrollCarry += y - lastY;
                        lastY = y;
                        float step = 36f * getResources().getDisplayMetrics().density;
                        int notches = (int) (scrollCarry / step);
                        if (notches != 0) {
                            scrollCarry -= notches * step;
                            link.scrollMouse(-notches * 120);
                        }
                        return true;
                    }
                    if (!dragging) {
                        if (Math.hypot(x - downX, y - downY) < touchSlop) {
                            return true;
                        }
                        dragging = true;
                    }
                    int dx = clamp((int) ((x - lastX) * 1.8f));
                    int dy = clamp((int) ((y - lastY) * 1.8f));
                    if (dx != 0 || dy != 0) {
                        link.moveMouse(dx, dy);
                    }
                    lastX = x;
                    lastY = y;
                    return true;

                case MotionEvent.ACTION_UP:
                    if (!dragging && event.getEventTime() - downAt < 300) {
                        int mask = twoFingers ? Protocol.BUTTON_RIGHT : Protocol.BUTTON_LEFT;
                        link.mouseButton(mask, true);
                        link.mouseButton(mask, false);
                    }
                    dragging = false;
                    twoFingers = false;
                    return true;

                case MotionEvent.ACTION_CANCEL:
                    dragging = false;
                    twoFingers = false;
                    return true;

                default:
                    return true;
            }
        });
    }

    private static int clamp(int value) {
        return Math.max(-220, Math.min(220, value));
    }

    // --- transparent keyboard ---------------------------------------------------

    private void toggleKeyboard() {
        keysVisible = !keysVisible;
        for (int i = overlayLayer.getChildCount() - 1; i >= 0; i--) {
            View child = overlayLayer.getChildAt(i);
            if (child.getTag() != null && "keys".equals(child.getTag())) {
                overlayLayer.removeViewAt(i);
            }
        }
        if (!keysVisible) {
            return;
        }

        LinearLayout board = new LinearLayout(this);
        board.setTag("keys");
        board.setOrientation(LinearLayout.VERTICAL);
        board.setBackgroundColor(0xCC0B0E13);
        board.setPadding(dp(6), dp(6), dp(6), dp(6));

        for (String[] row : KEY_ROWS) {
            LinearLayout line = new LinearLayout(this);
            line.setOrientation(LinearLayout.HORIZONTAL);
            for (String label : row) {
                TextView key = new TextView(this);
                key.setText(label);
                key.setTextColor(Color.WHITE);
                key.setTextSize(12);
                key.setGravity(Gravity.CENTER);
                key.setBackgroundResource(R.drawable.bg_touch_button);
                LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(42), 1f);
                lp.setMargins(dp(2), dp(2), dp(2), dp(2));
                key.setLayoutParams(lp);
                wireKey(key, label);
                line.addView(key);
            }
            board.addView(line, new LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT));
        }

        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT);
        lp.gravity = Gravity.BOTTOM;
        overlayLayer.addView(board, lp);
    }

    private void wireKey(TextView key, String label) {
        key.setOnTouchListener((v, event) -> {
            int vk = virtualKey(label);
            if (vk == 0) {
                return true;
            }
            switch (event.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    v.setPressed(true);
                    link.sendKey(vk, true);
                    return true;
                case MotionEvent.ACTION_UP:
                case MotionEvent.ACTION_CANCEL:
                    v.setPressed(false);
                    link.sendKey(vk, false);
                    return true;
                default:
                    return true;
            }
        });
    }

    private static int virtualKey(String label) {
        switch (label) {
            case "Esc": return 0x1B;
            case "Tab": return 0x09;
            case "Back": return 0x08;
            case "Enter": return 0x0D;
            case "Space": return 0x20;
            case "Shift": return 0x10;
            case "Ctrl": return 0x11;
            case "Alt": return 0x12;
            case "Win": return 0x5B;
            case "\u2190": return 0x25;
            case "\u2191": return 0x26;
            case "\u2192": return 0x27;
            case "\u2193": return 0x28;
            default:
                if (label.length() == 1) {
                    char c = Character.toUpperCase(label.charAt(0));
                    if (c >= 'A' && c <= 'Z') return c;
                    if (c >= '0' && c <= '9') return c;
                }
                return 0;
        }
    }

    // --- controller overlay -----------------------------------------------------

    /**
     * Inflates the controller layout used over the PC screen: the same sticks,
     * triggers, d-pad and face buttons as the standalone Controller screen, but with
     * the gyro toggle shrunk above Back/Start at the bottom and no Done button —
     * the toolbar's Controller button closes it. The opaque background is dropped so
     * the PC picture stays visible.
     */
    private void togglePad() {
        padVisible = !padVisible;
        for (int i = overlayLayer.getChildCount() - 1; i >= 0; i--) {
            View child = overlayLayer.getChildAt(i);
            if (child.getTag() != null && "pad".equals(child.getTag())) {
                overlayLayer.removeViewAt(i);
            }
        }
        link.padConnect(padVisible);
        if (!padVisible) {
            // Leaving the pad behind must not leave the gyro running or the mouse
            // switched off: both belong to the controller, not to the screen.
            setGyro(false);
            setMouse(true);
            return;
        }

        View pad = getLayoutInflater().inflate(R.layout.activity_gamepad_overlay, overlayLayer, false);
        pad.setTag("pad");
        pad.setBackgroundColor(Color.TRANSPARENT);

        JoystickView left = pad.findViewById(R.id.leftStick);
        left.setListener((x, y) -> {
            if (!gyro) {
                link.padAxis(Protocol.PAD_LEFT_X, Math.round(x * 32767));
                link.padAxis(Protocol.PAD_LEFT_Y, Math.round(y * 32767));
            }
        });
        stick = left;

        JoystickView right = pad.findViewById(R.id.rightStick);
        right.setListener((x, y) -> {
            link.padAxis(Protocol.PAD_RIGHT_X, Math.round(x * 32767));
            link.padAxis(Protocol.PAD_RIGHT_Y, Math.round(y * 32767));
        });

        wirePadButton(pad, R.id.lbButton, Protocol.PAD_LB);
        wirePadButton(pad, R.id.rbButton, Protocol.PAD_RB);
        wirePadButton(pad, R.id.buttonA, Protocol.PAD_A);
        wirePadButton(pad, R.id.buttonB, Protocol.PAD_B);
        wirePadButton(pad, R.id.buttonX, Protocol.PAD_X);
        wirePadButton(pad, R.id.buttonY, Protocol.PAD_Y);
        wirePadButton(pad, R.id.dpadUp, Protocol.PAD_UP);
        wirePadButton(pad, R.id.dpadDown, Protocol.PAD_DOWN);
        wirePadButton(pad, R.id.dpadLeft, Protocol.PAD_LEFT);
        wirePadButton(pad, R.id.dpadRight, Protocol.PAD_RIGHT);
        wirePadButton(pad, R.id.backButton, Protocol.PAD_BACK);
        wirePadButton(pad, R.id.startButton, Protocol.PAD_START);
        wirePadTrigger(pad, R.id.ltButton, Protocol.PAD_LEFT_TRIGGER);
        wirePadTrigger(pad, R.id.rtButton, Protocol.PAD_RIGHT_TRIGGER);

        gyroButton = pad.findViewById(R.id.gyroButton);
        gyroButton.setOnClickListener(v -> setGyro(!gyro));

        mouseButton = pad.findViewById(R.id.mouseButton);
        mouseButton.setOnClickListener(v -> setMouse(!mouseEnabled));

        setGyro(false);
        setMouse(true);

        FrameLayout.LayoutParams lp = new FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT, FrameLayout.LayoutParams.WRAP_CONTENT);
        lp.gravity = Gravity.BOTTOM;
        overlayLayer.addView(pad, lp);
    }

    /**
     * Turns the phone-to-PC pointer on or off. Off is highlighted, because that is the
     * unusual state and the one worth noticing at a glance.
     */
    private void setMouse(boolean enabled) {
        mouseEnabled = enabled;
        if (mouseButton != null) {
            mouseButton.setText(enabled ? R.string.desktop_mouse_on : R.string.desktop_mouse_off);
            mouseButton.setBackgroundResource(
                    enabled ? R.drawable.bg_touch_button : R.drawable.bg_key_active);
        }
    }

    private void wirePadButton(View root, int id, int mask) {
        root.findViewById(id).setOnTouchListener((v, event) -> {
            switch (event.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    v.setPressed(true);
                    link.padButton(mask, true);
                    return true;
                case MotionEvent.ACTION_UP:
                case MotionEvent.ACTION_CANCEL:
                    v.setPressed(false);
                    link.padButton(mask, false);
                    return true;
                default:
                    return true;
            }
        });
    }

    private void wirePadTrigger(View root, int id, int axis) {
        root.findViewById(id).setOnTouchListener((v, event) -> {
            switch (event.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    v.setPressed(true);
                    link.padAxis(axis, TRIGGER_ON);
                    return true;
                case MotionEvent.ACTION_UP:
                case MotionEvent.ACTION_CANCEL:
                    v.setPressed(false);
                    link.padAxis(axis, 0);
                    return true;
                default:
                    return true;
            }
        });
    }

    /** Same gyro steering as the standalone Controller screen. */
    private void setGyro(boolean on) {
        gyro = on;
        if (gyroButton != null) {
            gyroButton.setText(on ? R.string.gamepad_gyro_on : R.string.gamepad_gyro_off);
            gyroButton.setBackgroundResource(on ? R.drawable.bg_key_active : R.drawable.bg_touch_button);
        }
        if (stick != null) {
            stick.setInteractive(!on);
            stick.setPosition(0f, 0f);
        }
        link.padAxis(Protocol.PAD_LEFT_X, 0);
        link.padAxis(Protocol.PAD_LEFT_Y, 0);

        if (on) {
            calibrated = false;
            haveGravity = false;
            lastSentX = 0;
            lastSentY = 0;
            registerSensor();
        } else if (sensors != null) {
            sensors.unregisterListener(this);
        }
    }

    private void registerSensor() {
        if (sensors == null || accelerometer == null) {
            Toast.makeText(this, R.string.gamepad_no_gyro, Toast.LENGTH_SHORT).show();
            return;
        }
        calibrated = false;
        haveGravity = false;
        sensors.registerListener(this, accelerometer, SensorManager.SENSOR_DELAY_GAME);
    }

    @Override
    public void onSensorChanged(SensorEvent event) {
        if (!gyro || event.values.length < 3) {
            return;
        }
        final float alpha = 0.8f;
        if (!haveGravity) {
            gravity[0] = event.values[0];
            gravity[1] = event.values[1];
            gravity[2] = event.values[2];
            haveGravity = true;
            return;
        }
        for (int i = 0; i < 3; i++) {
            gravity[i] = alpha * gravity[i] + (1f - alpha) * event.values[i];
        }

        float steer = (float) Math.atan2(gravity[0], -gravity[1]);
        float pitch = (float) Math.atan2(gravity[2], -gravity[1]);

        if (!calibrated) {
            neutralSteer = steer;
            neutralPitch = pitch;
            calibrated = true;
            return;
        }

        float fx = padAxis(steer - neutralSteer);
        float fy = -padAxis(pitch - neutralPitch);
        if (stick != null) {
            stick.setPosition(fx, fy);
        }

        int x = Math.round(fx * 32767);
        int y = Math.round(fy * 32767);
        if (Math.abs(x - lastSentX) < 400 && Math.abs(y - lastSentY) < 400) {
            return;
        }
        lastSentX = x;
        lastSentY = y;
        link.padAxis(Protocol.PAD_LEFT_X, x);
        link.padAxis(Protocol.PAD_LEFT_Y, y);
    }

    @Override
    public void onAccuracyChanged(Sensor sensor, int accuracy) {
        // nothing to do
    }

    private static float padAxis(float delta) {
        float value = Math.max(-1f, Math.min(1f, delta / MAX_TILT_RAD));
        float magnitude = Math.abs(value);
        if (magnitude < DEAD_ZONE) {
            return 0f;
        }
        return Math.signum(value) * (magnitude - DEAD_ZONE) / (1f - DEAD_ZONE);
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
