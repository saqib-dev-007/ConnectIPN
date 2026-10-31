package com.connectipn;

import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Paint;
import android.util.AttributeSet;
import android.view.MotionEvent;
import android.view.View;

import androidx.core.content.ContextCompat;

/**
 * Round analog stick. Reports a normalised position where x and y are -1..1 and
 * y is positive upwards, which is the convention the wire protocol uses.
 */
public class JoystickView extends View {

    public interface Listener {
        void onMove(float x, float y);
    }

    private final Paint basePaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint ringPaint = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint knobPaint = new Paint(Paint.ANTI_ALIAS_FLAG);

    private float centerX;
    private float centerY;
    private float travel;
    private float knobRadius;
    private float knobX;
    private float knobY;
    private float lastX;
    private float lastY;

    private Listener listener;
    private boolean interactive = true;

    public JoystickView(Context context, AttributeSet attrs) {
        super(context, attrs);
        float density = context.getResources().getDisplayMetrics().density;

        basePaint.setStyle(Paint.Style.FILL);
        basePaint.setColor(ContextCompat.getColor(context, R.color.ui_card));

        ringPaint.setStyle(Paint.Style.STROKE);
        ringPaint.setStrokeWidth(1.5f * density);
        ringPaint.setColor(ContextCompat.getColor(context, R.color.ui_surface_stroke));

        knobPaint.setStyle(Paint.Style.FILL);
        knobPaint.setColor(ContextCompat.getColor(context, R.color.ui_accent));
    }

    public void setListener(Listener value) {
        listener = value;
    }

    @Override
    protected void onSizeChanged(int w, int h, int oldw, int oldh) {
        super.onSizeChanged(w, h, oldw, oldh);
        centerX = w / 2f;
        centerY = h / 2f;
        knobRadius = Math.min(w, h) * 0.18f;
        travel = Math.min(w, h) / 2f - knobRadius - 2f * getResources().getDisplayMetrics().density;
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        float radius = Math.min(getWidth(), getHeight()) / 2f - 1f;
        canvas.drawCircle(centerX, centerY, radius, basePaint);
        canvas.drawCircle(centerX, centerY, radius, ringPaint);
        canvas.drawCircle(centerX + knobX, centerY + knobY, knobRadius, knobPaint);
    }

    /**
     * Places the knob without reporting anything. Used when the gyro drives the
     * stick so the on-screen knob follows the phone.
     */
    public void setPosition(float x, float y) {
        knobX = Math.max(-1f, Math.min(1f, x)) * travel;
        knobY = -Math.max(-1f, Math.min(1f, y)) * travel;
        invalidate();
    }

    /** When false the stick still draws, but touches are ignored. */
    public void setInteractive(boolean value) {
        interactive = value;
        if (!value) {
            knobX = 0f;
            knobY = 0f;
            invalidate();
        }
    }

    @Override
    public boolean onTouchEvent(MotionEvent event) {
        if (!isEnabled() || !interactive) {
            return false;
        }
        switch (event.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
            case MotionEvent.ACTION_MOVE:
                track(event.getX(), event.getY());
                return true;
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                release();
                return true;
            default:
                return true;
        }
    }

    private void track(float x, float y) {
        float dx = x - centerX;
        float dy = y - centerY;
        float length = (float) Math.hypot(dx, dy);
        if (length > travel) {
            dx = dx / length * travel;
            dy = dy / length * travel;
        }
        knobX = dx;
        knobY = dy;
        invalidate();
        notifyMove();
    }

    /** Returns the knob to the middle without reporting it twice. */
    public void release() {
        if (knobX == 0f && knobY == 0f) {
            return;
        }
        knobX = 0f;
        knobY = 0f;
        invalidate();
        notifyMove();
    }

    private void notifyMove() {
        if (listener == null || travel <= 0f) {
            return;
        }
        float x = knobX / travel;
        // Screen y grows downwards; the protocol wants it growing upwards.
        float y = -knobY / travel;
        if (Math.abs(x - lastX) < 0.01f && Math.abs(y - lastY) < 0.01f) {
            return;
        }
        lastX = x;
        lastY = y;
        listener.onMove(x, y);
    }

    @Override
    public void setEnabled(boolean enabled) {
        super.setEnabled(enabled);
        setAlpha(enabled ? 1f : 0.35f);
        if (!enabled) {
            release();
        }
    }
}
