package com.connectipn;

import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.util.AttributeSet;
import android.view.View;
import android.view.animation.DecelerateInterpolator;

import androidx.core.content.ContextCompat;

/**
 * The live link indicator: a solid core surrounded by expanding radar rings.
 * Rings breathe only while the state is interesting (connecting / mirroring),
 * so the UI's motion doubles as a status signal.
 */
public class RadarView extends View {

    /** Idle — grey core, no rings. Connecting — amber breathing. Connected — steady green. Error — steady red. */
    private static final int[] COLORS = {
            R.color.ui_text_dim,   // 0 idle
            R.color.ui_warn,       // 1 connecting
            R.color.ui_green,      // 2 connected
            R.color.ui_red,        // 3 error
    };
    private static final int[] MODES = { 0, 2, 0, 0 }; // breathing for connecting only

    private final Paint core = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint ring = new Paint(Paint.ANTI_ALIAS_FLAG);

    private int state = 0;
    private float ringAlpha;
    private float ringScale;
    private ValueAnimator pulse;

    public RadarView(Context context) {
        super(context);
        init();
    }

    public RadarView(Context context, AttributeSet attrs) {
        super(context, attrs);
        init();
    }

    private void init() {
        setLayerType(LAYER_TYPE_NONE, null);
        start(0);
    }

    /** state: 0 idle, 1 connecting, 2 connected, 3 error */
    public void start(int newState) {
        state = Math.max(0, Math.min(3, newState));
        int color = ContextCompat.getColor(getContext(), COLORS[state]);
        core.setColor(color);
        ring.setColor(color);
        if (pulse != null) {
            pulse.cancel();
            pulse = null;
        }
        if (MODES[state] == 2) {
            pulse = ValueAnimator.ofFloat(0f, 1f);
            pulse.setDuration(1400);
            pulse.setRepeatMode(ValueAnimator.RESTART);
            pulse.setRepeatCount(ValueAnimator.INFINITE);
            pulse.setStartDelay(400);
            pulse.addUpdateListener(animation -> {
                float t = (float) animation.getAnimatedValue();
                ringScale = 1f + t * 2.1f;
                ringAlpha = (1f - t) * 0.55f;
                invalidate();
            });
            pulse.setInterpolator(new DecelerateInterpolator());
            pulse.start();
        } else {
            ringAlpha = 0f;
            ringScale = 1f;
            invalidate();
        }
    }

    @Override
    protected void onAttachedToWindow() {
        super.onAttachedToWindow();
        if (MODES[state] == 2 && pulse != null && pulse.isPaused()) {
            pulse.resume();
        }
    }

    @Override
    protected void onDetachedFromWindow() {
        if (pulse != null) {
            pulse.pause();
        }
        super.onDetachedFromWindow();
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        float cx = getWidth() / 2f;
        float cy = getHeight() / 2f;
        float radius = Math.min(cx, cy) * 0.24f;
        if (ringAlpha > 0f) {
            ring.setAlpha((int) (ringAlpha * 255));
            canvas.drawCircle(cx, cy, radius * ringScale, ring);
        }
        canvas.drawCircle(cx, cy, radius, core);
    }
}
