package com.connectipn;

import android.content.Context;
import android.content.SharedPreferences;

/**
 * Remembers the last PC this phone paired with, so the link can be restored on the
 * next launch without scanning the QR code again. The PC keeps the same token across
 * restarts, so the saved token stays valid until the user unpairs.
 */
public final class Pairing {

    private static final String PREFS = "connectipn-pairing";
    private static final String KEY_HOST = "host";
    private static final String KEY_PORT = "port";
    private static final String KEY_TOKEN = "token";
    private static final String KEY_NAME = "name";
    private static final String KEY_USB = "usb";

    private Pairing() {
    }

    private static SharedPreferences prefs(Context context) {
        return context.getApplicationContext()
                .getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    public static void save(Context context, String host, int port, String token, String name) {
        prefs(context).edit()
                .putString(KEY_HOST, host)
                .putInt(KEY_PORT, port)
                .putString(KEY_TOKEN, token)
                .putString(KEY_NAME, name == null ? "" : name)
                .apply();
    }

    public static boolean has(Context context) {
        SharedPreferences prefs = prefs(context);
        String host = prefs.getString(KEY_HOST, "");
        String token = prefs.getString(KEY_TOKEN, "");
        return host != null && !host.isEmpty() && token != null && !token.isEmpty();
    }

    public static String host(Context context) {
        return prefs(context).getString(KEY_HOST, "");
    }

    public static int port(Context context) {
        return prefs(context).getInt(KEY_PORT, 47653);
    }

    public static String token(Context context) {
        return prefs(context).getString(KEY_TOKEN, "");
    }

    public static String name(Context context) {
        return prefs(context).getString(KEY_NAME, "");
    }

    /**
     * True when the link should be dialled through the USB cable instead of Wi-Fi.
     * The PC sets this by sending a transport switch; it is cleared automatically the
     * first time a USB connect fails, so unplugging the cable falls back to Wi-Fi
     * without the user having to change anything.
     */
    public static boolean usb(Context context) {
        return prefs(context).getBoolean(KEY_USB, false);
    }

    public static void setUsb(Context context, boolean enabled) {
        prefs(context).edit().putBoolean(KEY_USB, enabled).apply();
    }

    public static void clear(Context context) {
        prefs(context).edit().clear().apply();
    }
}
