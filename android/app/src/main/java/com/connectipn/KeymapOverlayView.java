package com.connectipn;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.DashPathEffect;
import android.graphics.Paint;
import android.graphics.RectF;
import android.os.Handler;
import android.os.Looper;
import android.util.AttributeSet;
import android.view.MotionEvent;
import android.view.View;

import java.util.ArrayList;
import java.util.List;

/**
 * Draws the keymap and turns touches on it into gestures on the app underneath.
 *
 * The overlay covers the whole screen, so a touch outside every control is proxied
 * straight through at the same position. That is what makes it usable over a game
 * that knows nothing about ConnectIPN.
 */
public class KeymapOverlayView extends View {

    private final Paint fill = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint border = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint label = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final List<KeymapModel.Control> controls = new ArrayList<>();
    private final Handler handler = new Handler(Looper.getMainLooper());

    private boolean editMode;
    private int active = -1;
    private float downX;
    private float downY;
    private float lastX;
    private float lastY;
    private boolean moved;
    private boolean proxy;
    private float lookX;
    private float lookY;

    private Runnable onChanged;

    private final Runnable remove = () -> {
        if (editMode && active >= 0 && !moved) {
            controls.remove(active);
            active = -1;
            invalidate();
            changed();
        }
    };

    public KeymapOverlayView(Context context) {
        super(context);
        init();
    }

    public KeymapOverlayView(Context context, AttributeSet attrs) {
        super(context, attrs);
        init();
    }

    private void init() {
        float density = getResources().getDisplayMetrics().density;
        fill.setStyle(Paint.Style.FILL);
        fill.setColor(0x662DD4BF);
        border.setStyle(Paint.Style.STROKE);
        border.setStrokeWidth(1.5f * density);
        border.setColor(0xCC5EEAD4);
        border.setPathEffect(new DashPathEffect(new float[] { 8f * density, 6f * density }, 0f));
        label.setColor(Color.WHITE);
        label.setTextSize(12f * density);
        label.setTextAlign(Paint.Align.CENTER);
        label.setFakeBoldText(true);
    }

    public void setControls(List<KeymapModel.Control> value) {
        controls.clear();
        if (value != null) {
            controls.addAll(value);
        }
        invalidate();
    }

    public List<KeymapModel.Control> getControls() {
        return controls;
    }

    public void setEditMode(boolean value) {
        editMode = value;
        invalidate();
    }

    public void setOnChanged(Runnable value) {
        onChanged = value;
    }

    private void changed() {
        Runnable r = onChanged;
        if (r != null) {
            r.run();
        }
    }

    private RectF rectOf(KeymapModel.Control c) {
        float width = getWidth();
        float height = getHeight();
        float w = (c.type.equals("look") ? c.w : c.size) * width;
        float h = (c.type.equals("look") ? c.h : c.size) * width;
        float left = c.x * width;
        float top = c.y * height;
        return new RectF(left, top, left + w, top + h);
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        for (KeymapModel.Control c : controls) {
            RectF r = rectOf(c);
            if (c.type.equals("stick")) {
                float radius = Math.min(r.width(), r.height()) / 2f;
                canvas.drawCircle(r.centerX(), r.centerY(), radius, fill);
                canvas.drawCircle(r.centerX(), r.centerY(), radius, border);
                canvas.drawCircle(r.centerX(), r.centerY(), radius * 0.35f, border);
                drawLabel(canvas, c.label, r.centerX(), r.centerY());
            } else if (c.type.equals("look")) {
                canvas.drawRect(r, fill);
                canvas.drawRect(r, border);
                drawLabel(canvas, c.label, r.centerX(), r.centerY());
            } else {
                float corner = 12f * getResources().getDisplayMetrics().density;
                canvas.drawRoundRect(r, corner, corner, fill);
                canvas.drawRoundRect(r, corner, corner, border);
                drawLabel(canvas, c.label, r.centerX(), r.centerY());
            }
        }
    }

    private void drawLabel(Canvas canvas, String text, float x, float y) {
        if (text == null || text.isEmpty()) {
            return;
        }
        canvas.drawText(text, x, y + label.getTextSize() / 3f, label);
    }

    private int hitTest(float x, float y) {
        for (int i = controls.size() - 1; i >= 0; i--) {
            if (rectOf(controls.get(i)).contains(x, y)) {
                return i;
            }
        }
        return -1;
    }

    @Override
    public boolean onTouchEvent(MotionEvent event) {
        float x = event.getX();
        float y = event.getY();

        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                active = hitTest(x, y);
                downX = lastX = x;
                downY = lastY = y;
                moved = false;
                proxy = false;
                if (active >= 0) {
                    KeymapModel.Control c = controls.get(active);
                    if (editMode) {
                        handler.postDelayed(remove, 650);
                    } else if (c.type.equals("button")) {
                        inject(Protocol.PHONE_TAP, c.cx, c.cy, c.cx, c.cy, 45);
                    } else if (c.type.equals("look")) {
                        RectF r = rectOf(c);
                        lookX = (x - r.left) / Math.max(1f, r.width());
                        lookY = (y - r.top) / Math.max(1f, r.height());
                    }
                } else if (!editMode) {
                    proxy = true;
                }
                return true;

            case MotionEvent.ACTION_MOVE:
                if (active >= 0 && editMode) {
                    if (Math.hypot(x - downX, y - downY) > 6f) {
                        moved = true;
                        handler.removeCallbacks(remove);
                    }
                    KeymapModel.Control c = controls.get(active);
                    c.x = clamp01(c.x + (x - lastX) / Math.max(1f, getWidth()));
                    c.y = clamp01(c.y + (y - lastY) / Math.max(1f, getHeight()));
                    invalidate();
                } else if (active >= 0) {
                    KeymapModel.Control c = controls.get(active);
                    RectF r = rectOf(c);
                    if (c.type.equals("stick")) {
                        float dx = (x - r.centerX()) / Math.max(1f, r.width() / 2f);
                        float dy = (y - r.centerY()) / Math.max(1f, r.height() / 2f);
                        dx = Math.max(-1f, Math.min(1f, dx));
                        dy = Math.max(-1f, Math.min(1f, dy));
                        inject(Protocol.PHONE_SWIPE,
                                c.cx, c.cy,
                                clamp01(c.cx + dx * 0.22f),
                                clamp01(c.cy + dy * 0.22f),
                                90);
                    } else if (c.type.equals("look")) {
                        float nx = (x - r.left) / Math.max(1f, r.width());
                        float ny = (y - r.top) / Math.max(1f, r.height());
                        inject(Protocol.PHONE_SWIPE, lookX, lookY, nx, ny, 40);
                        lookX = nx;
                        lookY = ny;
                    }
                }
                lastX = x;
                lastY = y;
                return true;

            case MotionEvent.ACTION_UP:
                handler.removeCallbacks(remove);
                if (active >= 0 && editMode && moved) {
                    changed();
                } else if (proxy) {
                    float nx = x / Math.max(1f, getWidth());
                    float ny = y / Math.max(1f, getHeight());
                    if (Math.hypot(x - downX, y - downY) < 10f) {
                        inject(Protocol.PHONE_TAP, nx, ny, nx, ny, 45);
                    } else {
                        inject(Protocol.PHONE_SWIPE,
                                downX / Math.max(1f, getWidth()),
                                downY / Math.max(1f, getHeight()),
                                nx, ny,
                                Math.max(60, (int) (event.getEventTime() - event.getDownTime())));
                    }
                }
                reset();
                return true;

            case MotionEvent.ACTION_CANCEL:
                handler.removeCallbacks(remove);
                reset();
                return true;

            default:
                return true;
        }
    }

    private void reset() {
        active = -1;
        moved = false;
        proxy = false;
    }

    private static float clamp01(float value) {
        return Math.max(0.01f, Math.min(0.94f, value));
    }

    private static void inject(int action, float x1, float y1, float x2, float y2, int duration) {
        RemoteControlService.dispatch(action,
                norm(x1), norm(y1), norm(x2), norm(y2), duration, 0);
    }

    private static int norm(float value) {
        return Math.round(Math.max(0f, Math.min(1f, value)) * 65535f);
    }
}
