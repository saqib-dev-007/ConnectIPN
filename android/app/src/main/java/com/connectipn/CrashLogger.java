package com.connectipn;

import android.util.Log;

import java.io.PrintWriter;
import java.io.StringWriter;import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Locale;

/**
 * Writes every uncaught exception to a rolling file in the app's own storage so a
 * "the app just closed" report can be turned into a stack trace without adb.
 * The file lives at <app>/files/crash.log and rotates; no reporting is sent anywhere.
 */
public final class CrashLogger {

    private static final String TAG = "ConnectIPN";

    private static volatile java.io.File[] logFiles = new java.io.File[0];

    private CrashLogger() {
    }

    public static void install(android.content.Context context) {
        try {
            java.io.File[] targets = new java.io.File[] {
                    new java.io.File(context.getApplicationContext().getFilesDir(), "crash.log"),
                    new java.io.File(context.getApplicationContext()
                            .getExternalFilesDir(null), "crash.log"),
            };
            List<java.io.File> writable = new ArrayList<>();
            for (java.io.File target : targets) {
                if (target.getParentFile() != null && (
                        target.getParentFile().mkdirs() || target.getParentFile().exists())) {
                    writable.add(target);
                }
            }
            logFiles = writable.toArray(new java.io.File[0]);
        } catch (Throwable ignored) {
            return;
        }
        Thread.UncaughtExceptionHandler previous =
                Thread.getDefaultUncaughtExceptionHandler();
        Thread.setDefaultUncaughtExceptionHandler((thread, throwable) -> {
            append(throwable);
            Log.e(TAG, "uncaught on " + thread.getName(), throwable);
            if (previous != null) {
                previous.uncaughtException(thread, throwable);
            }
        });
    }

    private static void append(Throwable throwable) {
        try {
            java.io.File[] targets = logFiles;
            if (targets.length == 0) {
                return;
            }
            StringWriter trace = new StringWriter();
            throwable.printStackTrace(new PrintWriter(trace));
            String stamp = new SimpleDateFormat("MM-dd HH:mm:ss", Locale.US)
                    .format(new Date());
            String text = "\n---- " + stamp + " " + LinkManager.get().state() + "\n"
                    + trace;
            for (java.io.File target : targets) {
                try (java.io.FileWriter writer = new java.io.FileWriter(target, true)) {
                    writer.write(text);
                }
            }
        } catch (Throwable ignored) {
        }
    }
}
