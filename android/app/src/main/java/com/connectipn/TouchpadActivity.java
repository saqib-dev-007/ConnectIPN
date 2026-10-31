package com.connectipn;

import android.os.Bundle;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewConfiguration;
import android.widget.TextView;
import android.widget.Toast;

import androidx.appcompat.app.AppCompatActivity;

/**
 * Full-screen trackpad for the paired PC. One finger moves the pointer, a tap is a
 * left click, two fingers tap for a right click, and the middle strip scrolls.
 * Every event goes through LinkManager, which queues it so the UI thread never
 * blocks on the socket.
 */
public class TouchpadActivity extends AppCompatActivity implements LinkManager.Listener {

    private static final int WHEEL_NOTCH = 120;
    private static final float SCROLL_STEP_DP = 26f;
    private static final long TAP_TIMEOUT_MS = 400;

    private final LinkManager link = LinkManager.get();

    private float density;
    private int touchSlop;

    private float carryX;
    private float carryY;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_touchpad);

        density = getResources().getDisplayMetrics().density;
        touchSlop = ViewConfiguration.get(this).getScaledTouchSlop();

        findViewById(R.id.closeButton).setOnClickListener(v -> finish());
        wireButton(findViewById(R.id.leftButton), Protocol.BUTTON_LEFT);
        wireButton(findViewById(R.id.rightButton), Protocol.BUTTON_RIGHT);
        wireTouchpad(findViewById(R.id.touchpad));
        wireScroll(findViewById(R.id.scrollStrip));
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
            Toast.makeText(this, R.string.mouse_disconnected, Toast.LENGTH_SHORT).show();
            finish();
        }
    }

    private void wireButton(View view, final int mask) {
        view.setOnTouchListener((v, event) -> {
            switch (event.getActionMasked()) {
                case MotionEvent.ACTION_DOWN:
                    v.setPressed(true);
                    link.mouseButton(mask, true);
                    return true;
                case MotionEvent.ACTION_UP:
                    v.setPressed(false);
                    link.mouseButton(mask, false);
                    return true;
                case MotionEvent.ACTION_CANCEL:
                    v.setPressed(false);
                    link.mouseButton(mask, false);
                    return true;
                default:
                    return true;
            }
        });
    }

    private void wireTouchpad(View pad) {
        pad.setOnTouchListener(new View.OnTouchListener() {
            private float lastX;
            private float lastY;
            private float downX;
            private float downY;
            private boolean dragging;
            private boolean twoFingers;
            private long downAt;

            @Override
            public boolean onTouch(View v, MotionEvent event) {
                switch (event.getActionMasked()) {
                    case MotionEvent.ACTION_DOWN:
                        lastX = downX = event.getX();
                        lastY = downY = event.getY();
                        dragging = false;
                        twoFingers = false;
                        downAt = event.getEventTime();
                        return true;

                    case MotionEvent.ACTION_POINTER_DOWN:
                        // A second finger only means "right click" on release; it
                        // must not also drag the pointer.
                        twoFingers = true;
                        return true;

                    case MotionEvent.ACTION_MOVE:
                        float x = event.getX();
                        float y = event.getY();
                        if (twoFingers) {
                            lastX = x;
                            lastY = y;
                            return true;
                        }
                        if (!dragging) {
                            float travel = (float) Math.hypot(x - downX, y - downY);
                            if (travel < touchSlop) {
                                return true;
                            }
                            dragging = true;
                        }
                        moveBy(x - lastX, y - lastY);
                        lastX = x;
                        lastY = y;
                        return true;

                    case MotionEvent.ACTION_UP:
                        if (!dragging && event.getEventTime() - downAt < TAP_TIMEOUT_MS) {
                            int mask = twoFingers ? Protocol.BUTTON_RIGHT : Protocol.BUTTON_LEFT;
                            link.mouseButton(mask, true);
                            link.mouseButton(mask, false);
                        }
                        return true;

                    case MotionEvent.ACTION_CANCEL:
                        dragging = false;
                        twoFingers = false;
                        return true;

                    default:
                        return true;
                }
            }
        });
    }

    /**
     * Finger travel is converted from pixels to density-independent units, then
     * scaled up so a screen-sized swipe still crosses a desktop. Faster strokes get
     * more gain, which is what makes a small phone usable as a pointer.
     */
    private void moveBy(float dxPx, float dyPx) {
        float dpX = dxPx / density;
        float dpY = dyPx / density;
        float speed = (float) Math.hypot(dpX, dpY);
        float gain = 2.2f + Math.min(2.8f, speed * 0.35f);

        carryX += dpX * gain;
        carryY += dpY * gain;

        int stepX = (int) carryX;
        int stepY = (int) carryY;
        if (stepX == 0 && stepY == 0) {
            // Sub-pixel travel is kept so a very slow drag still moves the pointer.
            return;
        }
        carryX -= stepX;
        carryY -= stepY;
        link.moveMouse(clamp(stepX), clamp(stepY));
    }

    private void wireScroll(View strip) {
        strip.setOnTouchListener(new View.OnTouchListener() {
            private float lastY;
            private float travel;

            @Override
            public boolean onTouch(View v, MotionEvent event) {
                switch (event.getActionMasked()) {
                    case MotionEvent.ACTION_DOWN:
                        lastY = event.getY();
                        travel = 0f;
                        return true;

                    case MotionEvent.ACTION_MOVE:
                        float y = event.getY();
                        travel += y - lastY;
                        lastY = y;

                        float step = Math.max(1f, SCROLL_STEP_DP * density);
                        // Whole notches only; the remainder stays in travel. The
                        // division truncates towards zero, so this works in both
                        // directions and cannot loop.
                        int notches = (int) (travel / step);
                        if (notches != 0) {
                            travel -= notches * step;
                            // Dragging down scrolls the page down, which is a
                            // negative wheel delta on Windows.
                            link.scrollMouse(-notches * WHEEL_NOTCH);
                        }
                        return true;

                    default:
                        return true;
                }
            }
        });
    }

    private static int clamp(int value) {
        return Math.max(-160, Math.min(160, value));
    }
}
