package com.connectipn;

import android.animation.ValueAnimator;
import android.content.Context;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Path;
import android.view.View;
import android.view.animation.DecelerateInterpolator;

import androidx.core.content.ContextCompat;

/**
 * The scanner's animated reticle: four aqua corner brackets around a centered
 * frame, a light that sweeps bottom-to-top inside it on a soft audio-visualizer
 * tail, and a scrim that dims everything outside the frame. Drawn in code so the
 * frame tracks any view size.
 */
class ScanOverlay extends View {

    private final Paint bracket = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint sweep = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint scrim = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint frame = new Paint(Paint.ANTI_ALIAS_FLAG);

    private final ValueAnimator sweepAnim = ValueAnimator.ofFloat(0f, 1f);
    private float sweepT;
    private final Path path = new Path();

    ScanOverlay(Context context) {
        super(context);
        bracket.setStyle(Paint.Style.STROKE);
        bracket.setStrokeWidth(dp(2.4f));
        bracket.setColor(ContextCompat.getColor(context, R.color.ui_accent));
        bracket.setStrokeCap(Paint.Cap.ROUND);

        sweep.setStyle(Paint.Style.STROKE);
        sweep.setStrokeWidth(dp(3f));
        sweep.setColor(ContextCompat.getColor(context, R.color.ui_highlight));
        sweep.setStrokeCap(Paint.Cap.ROUND);

        scrim.setColor(0x88000000);

        frame.setStyle(Paint.Style.STROKE);
        frame.setStrokeWidth(dp(1f));
        frame.setColor(0x33FFFFFF);

        sweepAnim.setDuration(1900);
        sweepAnim.setRepeatCount(ValueAnimator.INFINITE);
        sweepAnim.setRepeatMode(ValueAnimator.RESTART);
        sweepAnim.setInterpolator(new DecelerateInterpolator(1.4f));
        sweepAnim.addUpdateListener(a -> {
            sweepT = (float) a.getAnimatedValue();
            invalidate();
        });
        sweepAnim.start();
    }

    @Override
    protected void onDetachedFromWindow() {
        sweepAnim.cancel();
        super.onDetachedFromWindow();
    }

    @Override
    protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        int w = getWidth();
        int h = getHeight();
        float side = Math.min(w - dp(48f), h - dp(300f));
        side = Math.max(side, dp(200f));
        float left = (w - side) / 2f;
        float top = (h - side) / 2f - dp(40f);
        float right = left + side;
        float bottom = top + side;
        float corner = dp(26f);

        // Scrim: four bands around the frame.
        canvas.drawRect(0, 0, w, top, scrim);
        canvas.drawRect(0, bottom, w, h, scrim);
        canvas.drawRect(0, top, left, bottom, scrim);
        canvas.drawRect(right, top, w, bottom, scrim);
        // Faint full frame, then strong corners on top.
        canvas.drawRoundRect(left, top, right, bottom, dp(10f), dp(10f), frame);

        drawBracket(canvas, left, top, corner, false, false);
        drawBracket(canvas, right, top, corner, true, false);
        drawBracket(canvas, left, bottom, corner, false, true);
        drawBracket(canvas, right, bottom, corner, true, true);

        // Sweep line with a sinusoidal fade at the extremes.
        float cy = top + side * sweepT;
        float alpha = (float) (Math.sin(sweepT * Math.PI) * 255);
        sweep.setAlpha((int) alpha);
        float inset = side * 0.08f;
        canvas.drawLine(left + inset, cy, right - inset, cy, sweep);
    }

    /** One rounded L-bracket, opening inward from the given frame corner. */
    private void drawBracket(Canvas canvas, float cx, float cy, float len,
            boolean right, boolean bottom) {
        float reach = len * 0.34f;
        int dx = right ? -1 : 1;
        int dy = bottom ? -1 : 1;
        path.reset();
        path.moveTo(cx, cy + dy * len);
        path.lineTo(cx, cy + dy * reach);
        path.quadTo(cx, cy, cx + dx * reach, cy);
        path.lineTo(cx + dx * len, cy);
        canvas.drawPath(path, bracket);
    }

    private float dp(float value) {
        return value * getResources().getDisplayMetrics().density;
    }
}
