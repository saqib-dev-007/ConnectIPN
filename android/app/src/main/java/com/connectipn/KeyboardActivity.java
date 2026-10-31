package com.connectipn;

import android.os.Bundle;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.View;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;

import androidx.appcompat.app.AppCompatActivity;
import androidx.core.content.ContextCompat;

import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;

/**
 * On-screen keyboard that types into the paired PC. Keys are sent as Windows
 * virtual-key codes, so shortcuts work: latch Ctrl, press C, and the PC sees
 * Ctrl+C rather than a literal character.
 */
public class KeyboardActivity extends AppCompatActivity implements LinkManager.Listener {

    private static final int VK_BACK = 0x08;
    private static final int VK_TAB = 0x09;
    private static final int VK_RETURN = 0x0D;
    private static final int VK_SHIFT = 0x10;
    private static final int VK_CONTROL = 0x11;
    private static final int VK_MENU = 0x12;
    private static final int VK_ESCAPE = 0x1B;
    private static final int VK_SPACE = 0x20;
    private static final int VK_LEFT = 0x25;
    private static final int VK_UP = 0x26;
    private static final int VK_RIGHT = 0x27;
    private static final int VK_DOWN = 0x28;
    private static final int VK_LWIN = 0x5B;

    private static final String[][] ROWS = {
        { "Esc", "Tab", "Ctrl", "Alt", "Win", "\u2190", "\u2191", "\u2193", "\u2192" },
        { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0" },
        { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" },
        { "a", "s", "d", "f", "g", "h", "j", "k", "l", "\u232B" },
        { "\u21E7", "z", "x", "c", "v", "b", "n", "m", "\u23CE" },
        { "Space", "-", "=", ",", ".", "/", ";", "'" },
    };

    private final LinkManager link = LinkManager.get();
    private final Map<String, TextView> modifierKeys = new HashMap<>();

    private boolean shift;
    private boolean ctrl;
    private boolean alt;
    private boolean win;

    private TextView statusText;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_keyboard);
        SystemBars.apply(this, R.id.root);

        statusText = findViewById(R.id.statusText);
        findViewById(R.id.closeButton).setOnClickListener(v -> finish());

        LinearLayout keys = findViewById(R.id.keys);
        for (String[] row : ROWS) {
            keys.addView(buildRow(row));
        }
        updateModifiers();
    }

    @Override
    protected void onDestroy() {
        SystemBars.restore(this);
        super.onDestroy();
    }

    @Override
    protected void onResume() {
        super.onResume();
        link.setListener(this);
    }

    @Override
    protected void onPause() {
        link.clearListener(this);
        super.onPause();
    }

    @Override
    public void onLinkState(LinkManager.State state, String message) {
        if (state != LinkManager.State.CONNECTED) {
            Toast.makeText(this, R.string.keyboard_disconnected, Toast.LENGTH_SHORT).show();
            finish();
        }
    }

    private LinearLayout buildRow(String[] labels) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        // Height 0 with weight 1: the six rows divide whatever height is left, so
        // they always fit no matter how tall the screen is in landscape.
        row.setLayoutParams(new LinearLayout.LayoutParams(
                LinearLayout.LayoutParams.MATCH_PARENT, 0, 1f));
        for (String label : labels) {
            row.addView(buildKey(label, "Space".equals(label) ? 3f : 1f));
        }
        return row;
    }

    private TextView buildKey(String label, float weight) {
        TextView key = new TextView(this);
        LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(
                0, LinearLayout.LayoutParams.MATCH_PARENT, weight);
        int margin = dp(2);
        params.setMargins(margin, margin, margin, margin);
        key.setLayoutParams(params);
        key.setGravity(Gravity.CENTER);
        key.setText("Space".equals(label) ? "space" : label);
        key.setTextSize(TypedValue.COMPLEX_UNIT_SP, 15);
        key.setTextColor(ContextCompat.getColor(this,
                isModifier(label) ? R.color.ui_text_muted : R.color.ui_text));
        key.setBackgroundResource(R.drawable.bg_touch_button);
        key.setContentDescription(label);
        key.setSoundEffectsEnabled(false);
        key.setOnClickListener(v -> onKey(label));
        if (isModifier(label)) {
            modifierKeys.put(label, key);
        }
        return key;
    }

    private static boolean isModifier(String label) {
        return "\u21E7".equals(label) || "Ctrl".equals(label) || "Alt".equals(label) || "Win".equals(label);
    }

    private void onKey(String label) {
        switch (label) {
            case "\u21E7":
                shift = !shift;
                updateModifiers();
                return;
            case "Ctrl":
                ctrl = !ctrl;
                updateModifiers();
                return;
            case "Alt":
                alt = !alt;
                updateModifiers();
                return;
            case "Win":
                win = !win;
                updateModifiers();
                return;
            case "\u232B":
                type(VK_BACK);
                return;
            case "\u23CE":
                type(VK_RETURN);
                return;
            case "Space":
                type(VK_SPACE);
                return;
            case "Esc":
                type(VK_ESCAPE);
                return;
            case "Tab":
                type(VK_TAB);
                return;
            case "\u2190":
                type(VK_LEFT);
                return;
            case "\u2191":
                type(VK_UP);
                return;
            case "\u2193":
                type(VK_DOWN);
                return;
            case "\u2192":
                type(VK_RIGHT);
                return;
            default:
                type(vkOf(label));
        }
    }

    /**
     * Presses any latched modifiers around the key, then releases everything in
     * reverse. The PC sees a normal chord, so Ctrl+C behaves like the real thing.
     */
    private void type(int vk) {
        if (vk == 0) {
            return;
        }
        List<Integer> modifiers = latchedModifiers();
        for (int modifier : modifiers) {
            link.sendKey(modifier, true);
        }
        link.sendKey(vk, true);
        link.sendKey(vk, false);
        for (int i = modifiers.size() - 1; i >= 0; i--) {
            link.sendKey(modifiers.get(i), false);
        }
        if (!modifiers.isEmpty()) {
            shift = false;
            ctrl = false;
            alt = false;
            win = false;
            updateModifiers();
        }
    }

    private List<Integer> latchedModifiers() {
        List<Integer> modifiers = new ArrayList<>(4);
        if (shift) {
            modifiers.add(VK_SHIFT);
        }
        if (ctrl) {
            modifiers.add(VK_CONTROL);
        }
        if (alt) {
            modifiers.add(VK_MENU);
        }
        if (win) {
            modifiers.add(VK_LWIN);
        }
        return modifiers;
    }

    private void updateModifiers() {
        for (Map.Entry<String, TextView> entry : modifierKeys.entrySet()) {
            boolean latched = isLatched(entry.getKey());
            entry.getValue().setBackgroundResource(
                    latched ? R.drawable.bg_key_active : R.drawable.bg_touch_button);
            entry.getValue().setTextColor(ContextCompat.getColor(this,
                    latched ? R.color.ui_text : R.color.ui_text_muted));
        }

        StringBuilder text = new StringBuilder();
        for (String label : new String[] { "\u21E7", "Ctrl", "Alt", "Win" }) {
            if (isLatched(label)) {
                if (text.length() > 0) {
                    text.append(" + ");
                }
                text.append("\u21E7".equals(label) ? "Shift" : label);
            }
        }
        statusText.setText(text.length() == 0
                ? getString(R.string.keyboard_hint)
                : getString(R.string.keyboard_latched, text.toString()));
    }

    private boolean isLatched(String label) {
        switch (label) {
            case "\u21E7":
                return shift;
            case "Ctrl":
                return ctrl;
            case "Alt":
                return alt;
            case "Win":
                return win;
            default:
                return false;
        }
    }

    /** Maps a key label to its Windows virtual-key code (US layout). */
    private static int vkOf(String label) {
        char c = label.charAt(0);
        if (c >= 'a' && c <= 'z') {
            return Character.toUpperCase(c);
        }
        if (c >= '0' && c <= '9') {
            return c;
        }
        switch (label) {
            case "-":
                return 0xBD; // VK_OEM_MINUS
            case "=":
                return 0xBB; // VK_OEM_PLUS
            case ",":
                return 0xBC; // VK_OEM_COMMA
            case ".":
                return 0xBE; // VK_OEM_PERIOD
            case "/":
                return 0xBF; // VK_OEM_2
            case ";":
                return 0xBA; // VK_OEM_1
            case "'":
                return 0xDE; // VK_OEM_7
            default:
                return 0;
        }
    }

    private int dp(int value) {
        return Math.round(value * getResources().getDisplayMetrics().density);
    }
}
