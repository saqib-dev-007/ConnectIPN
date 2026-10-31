package com.connectipn;

import android.app.Activity;
import android.view.View;
import android.view.Window;

import androidx.core.graphics.Insets;
import androidx.core.view.ViewCompat;
import androidx.core.view.WindowCompat;
import androidx.core.view.WindowInsetsCompat;
import androidx.core.view.WindowInsetsControllerCompat;

/**
 * Full-screen mode for the keyboard and controller: the system bars hide and come
 * back with a swipe, and the layout keeps clear of whatever space they still take
 * so nothing sits under the navigation bar.
 */
final class SystemBars {

    private SystemBars() {
    }

    static void apply(Activity activity, int rootId) {
        Window window = activity.getWindow();
        WindowCompat.setDecorFitsSystemWindows(window, false);

        WindowInsetsControllerCompat controller =
                WindowCompat.getInsetsController(window, window.getDecorView());
        controller.setSystemBarsBehavior(
                WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE);
        controller.hide(WindowInsetsCompat.Type.systemBars());

        View root = activity.findViewById(rootId);
        int baseLeft = root.getPaddingLeft();
        int baseTop = root.getPaddingTop();
        int baseRight = root.getPaddingRight();
        int baseBottom = root.getPaddingBottom();

        ViewCompat.setOnApplyWindowInsetsListener(root, (view, insets) -> {
            Insets bars = insets.getInsets(WindowInsetsCompat.Type.systemBars());
            view.setPadding(
                    baseLeft + bars.left,
                    baseTop + bars.top,
                    baseRight + bars.right,
                    baseBottom + bars.bottom);
            return insets;
        });
    }

    static void restore(Activity activity) {
        WindowInsetsControllerCompat controller = WindowCompat.getInsetsController(
                activity.getWindow(), activity.getWindow().getDecorView());
        controller.show(WindowInsetsCompat.Type.systemBars());
    }
}
