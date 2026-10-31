package com.connectipn;

import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.provider.Settings;
import android.widget.TextView;
import android.widget.Toast;

import androidx.appcompat.app.AppCompatActivity;
import androidx.core.content.ContextCompat;

/**
 * Editor for the on-screen keymap. Controls are dragged into place here, then the
 * overlay service keeps them on top of the game and injects the touches.
 */
public class KeymapActivity extends AppCompatActivity {

    private KeymapOverlayView view;
    private TextView hint;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_keymap);
        KeymapStore.init(this);

        view = findViewById(R.id.keymapView);
        hint = findViewById(R.id.hintText);
        view.setEditMode(true);
        view.setControls(KeymapModel.parse(KeymapStore.load()));
        view.setOnChanged(this::persist);

        findViewById(R.id.backButton).setOnClickListener(v -> finish());
        findViewById(R.id.addButton).setOnClickListener(v -> add("button"));
        findViewById(R.id.addStick).setOnClickListener(v -> add("stick"));
        findViewById(R.id.addLook).setOnClickListener(v -> add("look"));
        findViewById(R.id.clearButton).setOnClickListener(v -> {
            view.getControls().clear();
            view.invalidate();
            persist();
        });
        findViewById(R.id.saveButton).setOnClickListener(v -> save());
        findViewById(R.id.stopButton).setOnClickListener(v -> {
            stopService(new Intent(this, KeymapService.class));
            toast("Overlay stopped");
        });
        findViewById(R.id.accessButton).setOnClickListener(v ->
                startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)));
        findViewById(R.id.overlayButton).setOnClickListener(v -> {
            Intent intent = new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                    Uri.parse("package:" + getPackageName()));
            startActivity(intent);
        });
    }

    @Override
    protected void onResume() {
        super.onResume();
        updateHint();
    }

    private void add(String type) {
        KeymapModel.Control c = new KeymapModel.Control();
        c.type = type;
        if (type.equals("stick")) {
            c.label = "WASD";
            c.x = 0.06f;
            c.y = 0.60f;
            c.size = 0.28f;
            c.cx = 0.20f;
            c.cy = 0.80f;
        } else if (type.equals("look")) {
            c.label = "Look";
            c.x = 0.44f;
            c.y = 0.18f;
            c.w = 0.52f;
            c.h = 0.40f;
        } else {
            c.label = "Key";
            c.key = "Space";
            c.x = 0.74f;
            c.y = 0.72f;
            c.size = 0.13f;
            c.cx = 0.82f;
            c.cy = 0.84f;
        }
        view.getControls().add(c);
        view.invalidate();
        persist();
    }

    private void persist() {
        KeymapStore.save(KeymapModel.toJson(view.getControls()));
    }

    private void save() {
        persist();
        if (!Settings.canDrawOverlays(this)) {
            toast(getString(R.string.keymap_needs_overlay));
            startActivity(new Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION,
                    Uri.parse("package:" + getPackageName())));
            return;
        }
        if (!RemoteControlService.isReady()) {
            toast(getString(R.string.keymap_needs_accessibility));
        }
        ContextCompat.startForegroundService(this, new Intent(this, KeymapService.class));
        toast("Key mapping is on");
    }

    private void updateHint() {
        if (!RemoteControlService.isReady()) {
            hint.setText(R.string.keymap_needs_accessibility);
        } else if (!Settings.canDrawOverlays(this)) {
            hint.setText(R.string.keymap_needs_overlay);
        } else {
            hint.setText(R.string.keymap_edit_hint);
        }
    }

    private void toast(String text) {
        Toast.makeText(this, text, Toast.LENGTH_SHORT).show();
    }
}
