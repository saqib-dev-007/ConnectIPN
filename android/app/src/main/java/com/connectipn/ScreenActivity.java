package com.connectipn;

import android.Manifest;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.media.projection.MediaProjectionManager;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.text.SpannableString;
import android.text.style.ForegroundColorSpan;
import android.util.DisplayMetrics;
import android.widget.Button;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import androidx.activity.result.ActivityResultLauncher;
import androidx.activity.result.contract.ActivityResultContracts;
import androidx.appcompat.app.AlertDialog;
import androidx.appcompat.app.AppCompatActivity;
import androidx.core.content.ContextCompat;

/**
 * Entry point for everything screen related on the phone:
 *  - send this phone's screen to the PC (MediaProjection),
 *  - open the PC's screen on this phone (plain view or gaming mode).
 */
public class ScreenActivity extends AppCompatActivity implements LinkManager.Listener {

    private static final String[] RESOLUTIONS = { "480p", "720p", "1080p", "Native" };
    private static final int[] LONG_EDGE = { 854, 1280, 1920, 0 };
    private static final int[] FRAMES = { 15, 24, 30, 60 };
    private static final int[] QUALITIES = { 40, 60, 80 };
    private static final String[] ORIENTATIONS = { "Auto", "Portrait", "Landscape" };
    private static final String[] ASPECTS = { "Native", "16:9", "4:3" };

    private final LinkManager link = LinkManager.get();
    private final Handler ui = new Handler(Looper.getMainLooper());

    private int resolutionIndex = 1;
    private int fpsIndex = 1;
    private int qualityIndex = 1;
    private int orientationIndex = 0;
    private int aspectIndex = 0;

    private TextView statusText;
    private TextView statsText;
    private TextView accessState;
    private RadarView radar;
    private Button startButton;
    private Button resolutionButton;
    private Button fpsButton;
    private Button qualityButton;
    private Button orientationButton;
    private Button aspectButton;
    private Switch lockSwitch;
    private Switch audioSwitch;

    private int lastSizeWidth;
    private int lastSizeHeight;
    private int lastRadarMode = -1;

    private final Runnable ticker = new Runnable() {
        @Override
        public void run() {
            refreshStatus();
            ui.postDelayed(this, 1000);
        }
    };

    private final ActivityResultLauncher<Intent> projection =
            registerForActivityResult(new ActivityResultContracts.StartActivityForResult(), result -> {
                if (result.getResultCode() != RESULT_OK || result.getData() == null) {
                    toast(getString(R.string.screen_permission_denied));
                    return;
                }
                startCapture(result.getResultCode(), result.getData());
            });

    private final ActivityResultLauncher<String> audioPermission =
            registerForActivityResult(new ActivityResultContracts.RequestPermission(), granted -> {
                if (!granted) {
                    audioSwitch.setChecked(false);
                    toast(getString(R.string.screen_audio_denied));
                }
                launchProjection();
            });

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_screen);

        statusText = findViewById(R.id.statusText);
        statsText = findViewById(R.id.statsText);
        accessState = findViewById(R.id.accessState);
        radar = findViewById(R.id.screenRadar);
        startButton = findViewById(R.id.startButton);
        resolutionButton = findViewById(R.id.resolutionButton);
        fpsButton = findViewById(R.id.fpsButton);
        qualityButton = findViewById(R.id.qualityButton);
        orientationButton = findViewById(R.id.orientationButton);
        aspectButton = findViewById(R.id.aspectButton);
        lockSwitch = findViewById(R.id.lockSwitch);
        audioSwitch = findViewById(R.id.audioSwitch);

        findViewById(R.id.backButton).setOnClickListener(v -> finish());
        startButton.setOnClickListener(v -> toggleMirror());
        resolutionButton.setOnClickListener(v -> {
            resolutionIndex = (resolutionIndex + 1) % RESOLUTIONS.length;
            refreshOptions();
            pushOptionsIfRunning();
        });
        fpsButton.setOnClickListener(v -> {
            fpsIndex = (fpsIndex + 1) % FRAMES.length;
            refreshOptions();
            pushOptionsIfRunning();
        });
        qualityButton.setOnClickListener(v -> {
            qualityIndex = (qualityIndex + 1) % QUALITIES.length;
            refreshOptions();
            pushOptionsIfRunning();
        });
        orientationButton.setOnClickListener(v -> {
            orientationIndex = (orientationIndex + 1) % ORIENTATIONS.length;
            refreshOptions();
            pushOptionsIfRunning();
        });
        aspectButton.setOnClickListener(v -> {
            aspectIndex = (aspectIndex + 1) % ASPECTS.length;
            refreshOptions();
            pushOptionsIfRunning();
        });
        lockSwitch.setOnCheckedChangeListener((buttonView, checked) -> {
            ScreenCaptureService.setLockOverlay(checked);
            if (checked && ScreenCaptureService.isRunning()) {
                toast(getString(R.string.screen_locked_hint));
            }
        });

        findViewById(R.id.keymapButton).setOnClickListener(v -> {
            if (!Settings.canDrawOverlays(this)) {
                // Drawing over a game needs its own switch; explain it instead of
                // dropping the user into a settings page with no context.
                showOverlayHelp();
            } else {
                startActivity(new Intent(this, KeymapActivity.class));
            }
        });

        findViewById(R.id.accessButton).setOnClickListener(v -> showAccessHelp());

        findViewById(R.id.viewPcButton).setOnClickListener(v -> openDesktop(false));
        Ui.pressSink(findViewById(R.id.viewPcButton));

        refreshOptions();
        refreshStatus();
    }

    @Override
    protected void onResume() {
        super.onResume();
        link.setListener(this);
        link.sendStatus();
        refreshStatus();
        ui.removeCallbacks(ticker);
        ui.post(ticker);
    }

    @Override
    protected void onPause() {
        ui.removeCallbacks(ticker);
        link.clearListener(this);
        super.onPause();
    }

    @Override
    public void onLinkState(LinkManager.State state, String message) {
        runOnUiThread(this::refreshStatus);
    }

    private void toggleMirror() {
        if (link.state() != LinkManager.State.CONNECTED) {
            toast(getString(R.string.screen_needs_link));
            return;
        }
        if (ScreenCaptureService.isRunning()) {
            stopService(new Intent(this, ScreenCaptureService.class));
            statusText.postDelayed(this::refreshStatus, 400);
            return;
        }

        if (audioSwitch.isChecked()
                && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q
                && checkSelfPermission(Manifest.permission.RECORD_AUDIO)
                        != PackageManager.PERMISSION_GRANTED) {
            audioPermission.launch(Manifest.permission.RECORD_AUDIO);
            return;
        }
        launchProjection();
    }

    private void launchProjection() {
        MediaProjectionManager manager =
                (MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE);
        if (manager == null) {
            toast(getString(R.string.screen_permission_denied));
            return;
        }
        projection.launch(manager.createScreenCaptureIntent());
    }

    private void startCapture(int resultCode, Intent data) {
        int[] size = targetSize();
        lastSizeWidth = size[0];
        lastSizeHeight = size[1];
        Intent service = new Intent(this, ScreenCaptureService.class);
        service.putExtra(ScreenCaptureService.EXTRA_RESULT_CODE, resultCode);
        service.putExtra(ScreenCaptureService.EXTRA_RESULT_DATA, data);
        service.putExtra(ScreenCaptureService.EXTRA_WIDTH, size[0]);
        service.putExtra(ScreenCaptureService.EXTRA_HEIGHT, size[1]);
        service.putExtra(ScreenCaptureService.EXTRA_FPS, FRAMES[fpsIndex]);
        service.putExtra(ScreenCaptureService.EXTRA_QUALITY, QUALITIES[qualityIndex]);
        service.putExtra(ScreenCaptureService.EXTRA_ORIENTATION, orientationIndex);
        service.putExtra(ScreenCaptureService.EXTRA_ASPECT, aspectIndex);
        service.putExtra(ScreenCaptureService.EXTRA_LOCK, lockSwitch.isChecked());
        service.putExtra(ScreenCaptureService.EXTRA_AUDIO, audioSwitch.isChecked()
                && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q);
        ContextCompat.startForegroundService(this, service);
        statusText.postDelayed(this::refreshStatus, 600);
        if (!RemoteControlService.isReady()) {
            toast(getString(R.string.screen_control_access_missing));
        }
    }

    /** Applies new stream settings without restarting the projection prompt. */
    private void pushOptionsIfRunning() {
        if (!ScreenCaptureService.isRunning()) {
            return;
        }
        int[] size = targetSize();
        // reconfigure() announces the new shape to the PC on its own, so this must not
        // also send a SCREEN_CONFIG here.
        ScreenCaptureService.applyConfig(size[0], size[1], FRAMES[fpsIndex],
                QUALITIES[qualityIndex], orientationIndex, aspectIndex);
    }

    private void openDesktop(boolean gaming) {
        if (link.state() != LinkManager.State.CONNECTED) {
            toast(getString(R.string.screen_needs_link));
            return;
        }
        int[] size = targetSize();
        Intent intent = new Intent(this, DesktopViewActivity.class);
        intent.putExtra(DesktopViewActivity.EXTRA_GAMING, gaming);
        intent.putExtra(DesktopViewActivity.EXTRA_WIDTH, size[0]);
        intent.putExtra(DesktopViewActivity.EXTRA_HEIGHT, size[1]);
        intent.putExtra(DesktopViewActivity.EXTRA_FPS, FRAMES[fpsIndex]);
        intent.putExtra(DesktopViewActivity.EXTRA_QUALITY, QUALITIES[qualityIndex]);
        startActivity(intent);
    }

    private int[] targetSize() {
        DisplayMetrics metrics = new DisplayMetrics();
        // Real metrics on purpose: the projection mirrors the whole physical display
        // (status bar included), and RemoteControlService maps PC coordinates to the
        // same real pixels. Window metrics can exclude the bars, which shrank the
        // frame and made every PC tap land slightly off its target.
        android.view.WindowManager windows =
                (android.view.WindowManager) getApplicationContext()
                        .getSystemService(android.content.Context.WINDOW_SERVICE);
        if (windows != null && windows.getDefaultDisplay() != null) {
            windows.getDefaultDisplay().getRealMetrics(metrics);
        } else {
            metrics = getResources().getDisplayMetrics();
        }
        int w = Math.max(1, metrics.widthPixels);
        int h = Math.max(1, metrics.heightPixels);
        int longEdge = LONG_EDGE[resolutionIndex];
        if (longEdge > 0) {
            float scale = Math.min(1f, longEdge / (float) Math.max(w, h));
            w = Math.round(w * scale);
            h = Math.round(h * scale);
        }
        return new int[] { w, h };
    }

    private void refreshOptions() {
        resolutionButton.setText(getString(R.string.screen_resolution, RESOLUTIONS[resolutionIndex]));
        fpsButton.setText(getString(R.string.screen_fps, FRAMES[fpsIndex]));
        qualityButton.setText(getString(R.string.screen_quality, QUALITIES[qualityIndex]));
        orientationButton.setText(getString(R.string.screen_orientation, ORIENTATIONS[orientationIndex]));
        aspectButton.setText(getString(R.string.screen_aspect, ASPECTS[aspectIndex]));
    }

    private void refreshStatus() {
        boolean running = ScreenCaptureService.isRunning();
        boolean connected = link.state() == LinkManager.State.CONNECTED;
        startButton.setText(running ? R.string.screen_stop : R.string.screen_start);
        startButton.setEnabled(connected || running);
        startButton.setAlpha(startButton.isEnabled() ? 1f : 0.45f);

        // Radar: breathing while mirroring, dim standby otherwise.
        int mode = running ? 2 : 0;
        if (mode != lastRadarMode) {
            lastRadarMode = mode;
            radar.start(mode);
        }

        if (running) {
            statusText.setText(R.string.screen_status_running);
            int frames = ScreenCaptureService.framesSent();
            String error = ScreenCaptureService.lastError();
            // Live size, so changing resolution mid-stream updates this line instead
            // of leaving the size the stream started with.
            int liveWidth = ScreenCaptureService.currentWidth();
            int liveHeight = ScreenCaptureService.currentHeight();
            if (liveWidth <= 0) {
                liveWidth = lastSizeWidth > 0 ? lastSizeWidth : targetSize()[0];
            }
            if (liveHeight <= 0) {
                liveHeight = lastSizeHeight > 0 ? lastSizeHeight : targetSize()[1];
            }
            String line = getString(R.string.screen_stats, frames, liveWidth, liveHeight);
            if (error != null && !error.isEmpty()) {
                line = line + "\n" + getString(R.string.screen_error, error);
            }
            statsText.setText(line);
            statsText.setVisibility(android.view.View.VISIBLE);
        } else {
            statsText.setVisibility(android.view.View.GONE);
            String error = ScreenCaptureService.lastError();
            if (!connected) {
                statusText.setText(R.string.screen_needs_link);
            } else if (error != null && !error.isEmpty()) {
                statusText.setText(getString(R.string.screen_error, error));
            } else {
                statusText.setText(R.string.screen_status_idle);
            }
        }

        boolean overlay = Settings.canDrawOverlays(this);
        boolean access = RemoteControlService.isReady();
        accessState.setText(accessLines(access, overlay));

        findViewById(R.id.viewPcButton).setEnabled(connected);
        findViewById(R.id.viewPcButton).setAlpha(connected ? 1f : 0.4f);
    }

    /**
     * Two separate switches, said plainly: one lets the PC tap this phone, the other
     * lets the key-mapping buttons float over a game. Colour carries the state so the
     * page can be read at a glance.
     */
    private CharSequence accessLines(boolean control, boolean overlay) {
        String on = getString(R.string.screen_state_on);
        String off = getString(R.string.screen_state_off);
        String line1 = getString(R.string.screen_access_line_control, control ? on : off);
        String line2 = getString(R.string.screen_access_line_overlay, overlay ? on : off);

        SpannableString text = new SpannableString(line1 + "\n" + line2);
        tintState(text, line1, 0, control, on, off);
        tintState(text, line2, line1.length() + 1, overlay, on, off);
        return text;
    }

    private void tintState(SpannableString text, String line, int offset,
                           boolean enabled, String on, String off) {
        String word = enabled ? on : off;
        int start = line.lastIndexOf(word);
        if (start < 0) {
            return;
        }
        int color = ContextCompat.getColor(this,
                enabled ? R.color.ui_green : R.color.ui_warn);
        text.setSpan(new ForegroundColorSpan(color), offset + start,
                offset + start + word.length(), 0);
    }

    private void showAccessHelp() {
        new AlertDialog.Builder(this)
                .setTitle(R.string.screen_access_help_title)
                .setMessage(R.string.screen_access_help_body)
                .setPositiveButton(R.string.screen_access_help_open,
                        (dialog, which) -> startActivity(
                                new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)))
                .setNegativeButton(R.string.screen_access_help_later, null)
                .show();
    }

    private void showOverlayHelp() {
        new AlertDialog.Builder(this)
                .setTitle(R.string.screen_access_overlay_title)
                .setMessage(R.string.screen_access_overlay_body)
                .setPositiveButton(R.string.screen_access_help_open, (dialog, which) -> {
                    Intent intent = new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                            Uri.parse("package:" + getPackageName()));
                    startActivity(intent);
                })
                .setNegativeButton(R.string.screen_access_help_later, null)
                .show();
    }

    private void toast(String text) {
        Toast.makeText(this, text, Toast.LENGTH_SHORT).show();
    }
}
