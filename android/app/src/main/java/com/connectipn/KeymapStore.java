package com.connectipn;

import android.content.Context;
import android.content.SharedPreferences;

/** Persists the keymap and lets the overlay pick up edits without a restart. */
public final class KeymapStore {

    private static final String PREFS = "connectipn_keymap";
    private static final String KEY = "layout";

    private static volatile Context context;
    private static volatile String pending;
    private static volatile Runnable listener;

    private KeymapStore() {
    }

    public static void init(Context value) {
        context = value.getApplicationContext();
        String queued = pending;
        if (queued != null) {
            pending = null;
            save(queued);
        }
    }

    public static String load() {
        Context c = context;
        if (c == null) {
            String queued = pending;
            return queued != null ? queued : KeymapModel.DEFAULT;
        }
        SharedPreferences prefs = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        return prefs.getString(KEY, KeymapModel.DEFAULT);
    }

    public static void save(String json) {
        Context c = context;
        if (c == null) {
            pending = json;
        } else {
            c.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
                    .edit()
                    .putString(KEY, json)
                    .apply();
        }
        notifyListener();
    }

    /** Called when the PC pushes a layout over the link. */
    public static void acceptRemote(String json) {
        save(json);
    }

    public static void setListener(Runnable value) {
        listener = value;
        notifyListener();
    }

    private static void notifyListener() {
        Runnable r = listener;
        if (r != null) {
            r.run();
        }
    }
}
