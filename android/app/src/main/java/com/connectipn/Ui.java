package com.connectipn;

import android.animation.ValueAnimator;
import android.view.MotionEvent;
import android.view.View;

/**
 * Small interaction primitives for the instrument design.
 */
public final class Ui {

    private Ui() {
    }

    /**
     * Tactile press: the view shrinks to 0.97 while touched and springs back on
     * release, regardless of what background drawable it carries.
     */
    public static void pressSink(View view) {
        if (view == null) {
            return;
        }
        ValueAnimator sink = ValueAnimator.ofFloat(1f, 0.96f);
        sink.setDuration(80);
        ValueAnimator lift = ValueAnimator.ofFloat(0.96f, 1f);
        lift.setDuration(140);

        view.setOnTouchListener((v, event) -> {
            switch (event.getActionMasked()) {
                case MotionEvent.ACTION_DOWN -> {
                    sink.cancel();
                    sink.start();
                }
                case MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> {
                    sink.cancel();
                    lift.start();
                }
                default -> {
                }
            }
            sink.addUpdateListener(a -> v.setScaleX((float) a.getAnimatedValue()));
            lift.addUpdateListener(a -> v.setScaleX((float) a.getAnimatedValue()));
            sink.addUpdateListener(a -> v.setScaleY((float) a.getAnimatedValue()));
            lift.addUpdateListener(a -> v.setScaleY((float) a.getAnimatedValue()));
            v.setPivotX(v.getWidth() / 2f);
            v.setPivotY(v.getHeight() / 2f);
            return false;
        });
    }
}
