package com.connectipn;

import android.Manifest;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.provider.Settings;
import android.view.View;
import android.widget.Button;
import android.widget.ImageButton;
import android.widget.Switch;
import android.widget.TextView;
import android.widget.Toast;

import androidx.activity.result.ActivityResultLauncher;
import androidx.appcompat.app.AppCompatActivity;
import androidx.core.content.ContextCompat;

import com.journeyapps.barcodescanner.ScanContract;
import com.journeyapps.barcodescanner.ScanOptions;

import java.io.File;
import java.util.ArrayList;
import java.util.List;

public class MainActivity extends AppCompatActivity implements LinkManager.Listener {

    private static final String LINK_INSTAGRAM = "https://www.instagram.com/saqib_mengal_007";
    private static final String LINK_GITHUB = "https://github.com/saqib-dev-007";
    private static final String LINK_LINKEDIN = "https://www.linkedin.com/in/saqib-mengal-76855b389/";
    private static final String LINK_EMAIL = "mailto:saqibmengal7@gmail.com";
    private static final String LINK_PATREON = "https://www.patreon.com/cw/SaqibMengal";

    private TextView statusText;
    private TextView detailText;
    private TextView rootText;
    private RadarView radar;
    private Button scanButton;
    private Button permissionButton;
    private Button disconnectButton;
    private Switch usbSwitch;
    private boolean updatingUi;
    private View mouseTile;
    private View keyboardTile;
    private View gamepadTile;
    private View screenTile;
    private TextView appsHint;

    private final ActivityResultLauncher<ScanOptions> scanner =
            registerForActivityResult(new ScanContract(), result -> {
                if (result.getContents() != null) {
                    onScanned(result.getContents());
                }
            });

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        CrashLogger.install(getApplicationContext());
        setContentView(R.layout.activity_main);
        KeymapStore.init(this);
        LinkManager.get().init(this);

        statusText = findViewById(R.id.statusText);
        detailText = findViewById(R.id.detailText);
        rootText = findViewById(R.id.rootText);
        radar = findViewById(R.id.statusDot);
        scanButton = findViewById(R.id.scanButton);
        permissionButton = findViewById(R.id.permissionButton);
        disconnectButton = findViewById(R.id.disconnectButton);
        usbSwitch = findViewById(R.id.usbSwitch);
        mouseTile = findViewById(R.id.mouseTile);
        keyboardTile = findViewById(R.id.keyboardTile);
        gamepadTile = findViewById(R.id.gamepadTile);
        screenTile = findViewById(R.id.screenTile);
        appsHint = findViewById(R.id.appsHint);

        scanButton.setOnClickListener(v -> startScan());
        usbSwitch.setOnCheckedChangeListener((button, checked) -> {
            if (!updatingUi) {
                LinkManager.get().setUsbMode(checked);
            }
        });
        mouseTile.setOnClickListener(v -> openMouse());
        keyboardTile.setOnClickListener(v -> openKeyboard());
        gamepadTile.setOnClickListener(v -> openGamepad());
        screenTile.setOnClickListener(v -> openScreen());
        permissionButton.setOnClickListener(v -> requestAllFilesAccess());
        disconnectButton.setOnClickListener(v -> {
            LinkManager.get().disconnect();
            stopService(new Intent(this, LinkService.class));
            refreshUi();
        });
        // Long-press forgets the PC, so the next launch waits for a QR scan again.
        disconnectButton.setOnLongClickListener(v -> {
            LinkManager.get().forget();
            stopService(new Intent(this, LinkService.class));
            toast(getString(R.string.pairing_forgotten));
            refreshUi();
            return true;
        });

        ImageButton instagram = findViewById(R.id.instagramButton);
        ImageButton github = findViewById(R.id.githubButton);
        ImageButton linkedin = findViewById(R.id.linkedinButton);
        ImageButton email = findViewById(R.id.emailButton);
        ImageButton patreon = findViewById(R.id.patreonButton);
        instagram.setOnClickListener(v -> openLink(LINK_INSTAGRAM));
        github.setOnClickListener(v -> openLink(LINK_GITHUB));
        linkedin.setOnClickListener(v -> openLink(LINK_LINKEDIN));
        email.setOnClickListener(v -> openLink(LINK_EMAIL));
        patreon.setOnClickListener(v -> openLink(LINK_PATREON));

        // Tactile press feedback for the deck and the primary actions.
        for (View tile : new View[] { mouseTile, keyboardTile, gamepadTile, screenTile,
                scanButton, permissionButton, disconnectButton }) {
            Ui.pressSink(tile);
        }

        requestRuntimePermissions();
        refreshUi();
    }

    @Override
    protected void onResume() {
        super.onResume();
        LinkManager.get().init(this);
        LinkManager.get().setListener(this);
        LinkManager.get().sendStatus();
        // A paired PC is reconnected silently; the QR code is only for the first time.
        if (LinkManager.get().state() == LinkManager.State.IDLE) {
            LinkManager.get().autoConnect();
        }
        refreshUi();
    }

    @Override
    protected void onPause() {
        LinkManager.get().clearListener(this);
        super.onPause();
    }

    private void startScan() {
        ScanOptions options = new ScanOptions();
        options.setDesiredBarcodeFormats(ScanOptions.QR_CODE);
        options.setPrompt(getString(R.string.scan_prompt));
        options.setBeepEnabled(false);
        options.setBarcodeImageEnabled(false);
        // Keep the scanner upright: the library otherwise follows the sensor.
        options.setOrientationLocked(true);
        // My own capture activity: animated scan frame instead of the stock laser.
        options.setCaptureActivity(ScannerActivity.class);
        scanner.launch(options);
    }

    private void onScanned(String contents) {
        try {
            Uri uri = Uri.parse(contents);
            if (!"connectipn".equals(uri.getScheme())) {
                throw new IllegalArgumentException("not a ConnectIPN code");
            }
            String host = uri.getQueryParameter("h");
            String portText = uri.getQueryParameter("p");
            String token = uri.getQueryParameter("t");
            if (host == null || portText == null || token == null) {
                throw new IllegalArgumentException("code is missing fields");
            }
            int port = Integer.parseInt(portText);

            File root = storageRoot();
            if (root == null) {
                toast("Storage access is not available");
                return;
            }

            ContextCompat.startForegroundService(this, new Intent(this, LinkService.class));
            LinkManager.get().connect(host, port, token, root);
        } catch (Exception e) {
            toast("Invalid code: " + e.getMessage());
        }
    }

    private void openLink(String url) {
        try {
            Intent intent = new Intent(Intent.ACTION_VIEW, Uri.parse(url));
            intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            startActivity(intent);
        } catch (ActivityNotFoundException e) {
            toast("No app can open this link");
        }
    }

    private File storageRoot() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            if (Environment.isExternalStorageManager()) {
                return Environment.getExternalStorageDirectory();
            }
            return getExternalFilesDir(null);
        }
        return Environment.getExternalStorageDirectory();
    }

    private boolean hasAllFilesAccess() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            return Environment.isExternalStorageManager();
        }
        return true;
    }

    private void requestAllFilesAccess() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            Intent intent = new Intent(Settings.ACTION_MANAGE_APP_ALL_FILES_ACCESS_PERMISSION,
                    Uri.parse("package:" + getPackageName()));
            try {
                startActivity(intent);
            } catch (Exception e) {
                startActivity(new Intent(Settings.ACTION_MANAGE_ALL_FILES_ACCESS_PERMISSION));
            }
        } else {
            requestRuntimePermissions();
        }
    }

    private void requestRuntimePermissions() {
        List<String> missing = new ArrayList<>();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.POST_NOTIFICATIONS);
        }
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R
                && checkSelfPermission(Manifest.permission.READ_EXTERNAL_STORAGE) != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.READ_EXTERNAL_STORAGE);
        }
        if (Build.VERSION.SDK_INT <= Build.VERSION_CODES.P
                && checkSelfPermission(Manifest.permission.WRITE_EXTERNAL_STORAGE) != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.WRITE_EXTERNAL_STORAGE);
        }
        if (!missing.isEmpty()) {
            requestPermissions(missing.toArray(new String[0]), 1);
        }
    }

    private void refreshUi() {
        LinkManager manager = LinkManager.get();
        LinkManager.State state = manager.state();

        switch (state) {
            case CONNECTED:
                statusText.setText(R.string.status_connected);
                detailText.setText(R.string.detail_connected);
                radarStart(2);
                break;
            case CONNECTING:
                statusText.setText(R.string.status_connecting);
                detailText.setText(manager.message());
                radarStart(1);
                break;
            case ERROR:
                statusText.setText(R.string.status_error);
                detailText.setText(manager.message() + "\n"
                        + (manager.isPaired() ? "Reconnecting automatically…" : "Scan the code to connect."));
                radarStart(3);
                break;
            default:
                statusText.setText(R.string.status_idle);
                detailText.setText(R.string.detail_idle);
                radarStart(0);
                break;
        }

        boolean connected = state == LinkManager.State.CONNECTED;
        boolean busy = state == LinkManager.State.CONNECTING;
        scanButton.setEnabled(!connected && !busy);
        disconnectButton.setEnabled(connected || busy);

        updatingUi = true;
        usbSwitch.setChecked(LinkManager.get().isUsbPreferred());
        updatingUi = false;
        usbSwitch.setAlpha(connected || busy ? 1f : 0.45f);
        usbSwitch.setEnabled(connected || busy);

        permissionButton.setVisibility(hasAllFilesAccess() ? View.GONE : View.VISIBLE);

        // Custom button backgrounds do not dim themselves when disabled.
        scanButton.setAlpha(scanButton.isEnabled() ? 1f : 0.45f);
        disconnectButton.setAlpha(disconnectButton.isEnabled() ? 1f : 0.45f);

        if (connected) {
            rootText.setText("Sharing: " + manager.rootPath());
        } else if (!hasAllFilesAccess()) {
            rootText.setText("Without all-files access only this app's own folder is shared.");
        } else {
            rootText.setText("");
        }

        // The apps only work while a phone is paired to a PC.
        for (View tile : new View[] { mouseTile, keyboardTile, gamepadTile, screenTile }) {
            tile.setEnabled(connected);
            tile.setAlpha(connected ? 1f : 0.4f);
        }
        appsHint.setVisibility(connected ? View.GONE : View.VISIBLE);
    }

    private void openMouse() {
        if (LinkManager.get().state() != LinkManager.State.CONNECTED) {
            toast(getString(R.string.apps_needs_link));
            return;
        }
        startActivity(new Intent(this, TouchpadActivity.class));
    }

    private void openKeyboard() {
        if (LinkManager.get().state() != LinkManager.State.CONNECTED) {
            toast(getString(R.string.apps_needs_link));
            return;
        }
        startActivity(new Intent(this, KeyboardActivity.class));
    }

    private void openScreen() {
        if (LinkManager.get().state() != LinkManager.State.CONNECTED) {
            toast(getString(R.string.apps_needs_link));
            return;
        }
        startActivity(new Intent(this, ScreenActivity.class));
    }

    private void openGamepad() {
        if (LinkManager.get().state() != LinkManager.State.CONNECTED) {
            toast(getString(R.string.apps_needs_link));
            return;
        }
        startActivity(new Intent(this, GamepadActivity.class));
    }

    private void radarStart(int mode) {
        if (radar != null) {
            radar.start(mode);
        }
    }

    @Override
    public void onLinkState(LinkManager.State state, String message) {
        runOnUiThread(this::refreshUi);
    }

    private void toast(String text) {
        Toast.makeText(this, text, Toast.LENGTH_LONG).show();
    }
}
