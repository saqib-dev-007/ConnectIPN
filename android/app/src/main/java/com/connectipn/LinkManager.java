package com.connectipn;

import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;

import java.io.File;
import java.util.concurrent.CopyOnWriteArrayList;

/** Process-wide owner of the socket link, so the UI and the service agree on state. */
public final class LinkManager {

    public enum State { IDLE, CONNECTING, CONNECTED, ERROR }

    public interface Listener {
        void onLinkState(State state, String message);
    }

    private static final LinkManager INSTANCE = new LinkManager();

    private final LinkClient client = new LinkClient();
    private final Handler main = new Handler(Looper.getMainLooper());

    private volatile Listener listener;
    private volatile State state = State.IDLE;
    private volatile String message = "Not connected";
    private volatile String rootPath = "";
    private volatile boolean stopping;
    private volatile boolean autoReconnect;
    private volatile boolean userStopped;
    private Thread worker;
    private Context context;

    /** Retry backoff in milliseconds; reset on every successful connect. */
    private static final long[] BACKOFF = { 1500, 3000, 5000, 8000, 12000, 15000 };
    private int attempt;

    /**
     * Where the USB tunnel listens on the phone. The PC runs
     * {@code adb reverse tcp:47653 tcp:<its port>}, which makes this loopback address
     * land on the PC's listener through the cable instead of the network.
     */
    private static final String USB_HOST = "127.0.0.1";
    private static final int USB_PORT = 47653;

    /** True while the link is meant to travel over USB rather than Wi-Fi. */
    private volatile boolean usbMode;

    /**
     * Several screens can be interested in the same stream: the capture service wants
     * the PC's start/stop commands while a viewer wants the frames. A fan-out keeps
     * one of them from silently unregistering the other.
     */
    private final CopyOnWriteArrayList<LinkClient.FrameListener> frameListeners =
            new CopyOnWriteArrayList<>();
    private final LinkClient.FrameListener fanout = new LinkClient.FrameListener() {
        @Override
        public void onScreenFrame(byte[] jpeg, int width, int height, int source, int seq) {
            for (LinkClient.FrameListener listener : frameListeners) {
                listener.onScreenFrame(jpeg, width, height, source, seq);
            }
        }

        @Override
        public void onScreenConfig(int mode, int width, int height, int fps,
                                   int quality, int orientation, int aspect) {
            for (LinkClient.FrameListener listener : frameListeners) {
                listener.onScreenConfig(mode, width, height, fps, quality, orientation, aspect);
            }
        }

        @Override
        public void onKeymap(String json) {
            for (LinkClient.FrameListener listener : frameListeners) {
                listener.onKeymap(json);
            }
        }
    };

    private final Runnable retry = new Runnable() {
        @Override
        public void run() {
            if (!autoReconnect || stopping || state == State.CONNECTED) {
                return;
            }
            if (!Pairing.has(context)) {
                return;
            }
            attempt++;
            connectInternal(Pairing.host(context), Pairing.port(context),
                    Pairing.token(context), storageRoot(), false);
        }
    };

    private LinkManager() {
    }

    public static LinkManager get() {
        return INSTANCE;
    }

    /** Must be called once before connecting; keeps a Context for the pairing store. */
    public void init(Context value) {
        if (value != null) {
            context = value.getApplicationContext();
        }
    }

    public State state() {
        return state;
    }

    public String message() {
        return message;
    }

    public String rootPath() {
        return rootPath;
    }

    /** True when the PC carried the link over the USB cable. */
    public boolean isUsbMode() {
        return usbMode && state == State.CONNECTED;
    }

    /** True while USB is the chosen transport, even before it is connected. */
    public boolean isUsbPreferred() {
        return usbMode;
    }

    /** The connection tab switch: on moves the link to USB, off returns it to Wi-Fi. */
    public void setUsbMode(boolean usb) {
        if (usb == usbMode) {
            return;
        }
        if (usb) {
            switchToUsb(USB_PORT);
        } else {
            switchToWifi();
        }
    }

    public boolean isPaired() {
        return context != null && Pairing.has(context);
    }

    public String pairedName() {
        return context == null ? "" : Pairing.name(context);
    }

    public void addFrameListener(LinkClient.FrameListener value) {
        if (value != null && !frameListeners.contains(value)) {
            frameListeners.add(value);
        }
    }

    public void removeFrameListener(LinkClient.FrameListener value) {
        frameListeners.remove(value);
    }

    public void sendScreenFrame(byte[] jpeg, int width, int height, int source, int seq) {
        client.sendScreenFrame(jpeg, width, height, source, seq);
    }

    public void sendScreenConfig(int mode, int width, int height, int fps,
                                 int quality, int orientation, int aspect) {
        client.sendScreenConfig(mode, width, height, fps, quality, orientation, aspect);
    }

    /** Raw 16-bit stereo PCM, 48 kHz, straight from the phone's speaker mix. */
    public void sendAudio(byte[] pcm, int length) {
        client.sendAudio(pcm, length);
    }

    /** Tells the PC whether accessibility, overlay and audio are available. */
    public void sendStatus() {
        client.sendStatus(statusFlags());
    }

    public int statusFlags() {
        int flags = 0;
        if (RemoteControlService.isReady()) {
            flags |= Protocol.STATUS_CONTROL;
        }
        if (context != null && Settings.canDrawOverlays(context)) {
            flags |= Protocol.STATUS_OVERLAY;
        }
        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.Q) {
            flags |= Protocol.STATUS_AUDIO_CAPABLE;
        }
        if (ScreenCaptureService.isAudioRunning()) {
            flags |= Protocol.STATUS_AUDIO_RUNNING;
        }
        return flags;
    }

    public void setListener(Listener value) {
        listener = value;
        if (value != null) {
            value.onLinkState(state, message);
        }
    }

    public void clearListener(Listener value) {
        if (listener == value) {
            listener = null;
        }
    }

    /** Pairing from the QR code: remembers the PC and keeps the link alive. */
    public void connect(String host, int port, String token, File root) {
        autoReconnect = true;
        userStopped = false;
        attempt = 0;
        connectInternal(host, port, token, root, true);
    }

    /**
     * Restores the last pairing without any user action. Safe to call on every resume:
     * it does nothing while connected or already trying.
     */
    public void autoConnect() {
        if (userStopped || context == null || !Pairing.has(context)) {
            return;
        }
        if (state == State.CONNECTED || state == State.CONNECTING) {
            return;
        }
        autoReconnect = true;
        stopping = false;
        attempt = 0;
        connectInternal(Pairing.host(context), Pairing.port(context), Pairing.token(context),
                storageRoot(), false);
    }

    private void connectInternal(String lanHost, int lanPort, String token, File root, boolean manual) {
        if (worker != null && worker.isAlive()) {
            return;
        }
        if (!usbMode && context != null) {
            // Restores the choice across launches: once the PC moved the link onto the
            // cable, later reconnects keep using it until the cable goes away.
            usbMode = Pairing.usb(context);
        }
        final boolean viaUsb = usbMode;
        final String hostArg = viaUsb ? USB_HOST : lanHost;
        final int portArg = viaUsb ? USB_PORT : lanPort;

        main.removeCallbacks(retry);
        stopping = false;
        setState(State.CONNECTING, manual
                ? "Connecting to " + hostArg + ":" + portArg
                : "Reconnecting to " + hostArg + "…");

        client.setFrameListener(fanout);
        client.setInputListener(RemoteControlService::dispatch);
        client.setKeymapListener(KeymapStore::acceptRemote);
        client.setTransportListener(this::onTransport);

        client.setListener(new LinkClient.Listener() {
            @Override
            public void onConnected(String hostName, String sharedRoot) {
                // LinkManager publishes the state after connect() returns.
            }

            @Override
            public void onDisconnected(String reason) {
                if (stopping) {
                    return;
                }
                setState(State.ERROR, "Link lost: " + reason);
                scheduleRetry();
            }
        });

        final String tokenArg = token;
        worker = new Thread(() -> {
            try {
                client.connect(hostArg, portArg, tokenArg, android.os.Build.MODEL, root);
                rootPath = client.rootPath();
                attempt = 0;
                if (context != null && !viaUsb) {
                    // Only a Wi-Fi connect teaches us where the PC lives. Saving the USB
                    // loopback here would overwrite the real address and leave the phone
                    // with nothing to fall back to once the cable is pulled.
                    Pairing.save(context, lanHost, lanPort, tokenArg, lanHost);
                }
                setState(State.CONNECTED, viaUsb
                        ? "Connected over USB"
                        : "Connected to " + hostArg);
                android.util.Log.i("ConnectIPN-usb", (viaUsb ? "connected over USB to " : "connected over Wi-Fi to ")
                        + hostArg + ":" + portArg);
                client.sendStatus(statusFlags());
                // If the phone was mirroring when the link dropped, tell the PC its
                // stream is still live; otherwise the viewer sits dark until the
                // user restarts mirroring by hand.
                ScreenCaptureService.announce();
            } catch (Exception e) {
                if (viaUsb) {
                    // Cable unplugged, USB debugging revoked, or the PC has not opened
                    // its tunnel yet. Drop the preference and let the normal retry take
                    // the Wi-Fi address instead of hammering a dead loopback port.
                    usbMode = false;
                    if (context != null) {
                        Pairing.setUsb(context, false);
                    }
                    setState(State.ERROR, "USB link unavailable, falling back to Wi-Fi");
                    if (autoReconnect) {
                        scheduleRetry();
                    }
                    return;
                }
                String detail = e.getMessage() != null ? e.getMessage() : e.toString();
                setState(State.ERROR, "Could not connect: " + detail);
                if (autoReconnect) {
                    scheduleRetry();
                }
            }
        }, "connectipn-connect");
        worker.setDaemon(true);
        worker.start();
    }

    /** A transport switch arrived from the PC while the link was up. */
    private void onTransport(int mode, int port) {
        android.util.Log.i("ConnectIPN-usb", "transport switch requested: mode=" + mode + " port=" + port);
        if (mode == Protocol.TRANSPORT_USB) {
            switchToUsb(port > 0 ? port : USB_PORT);
        }
    }

    /**
     * Moves an established link onto the USB cable.
     *
     * <p>Runs off the main thread because closing the old socket joins the reader
     * thread. {@code stopping} swallows the "link lost" that the close would otherwise
     * report, so the UI shows one switch instead of a disconnect followed by a retry.
     */
    public void switchToUsb(int port) {
        android.util.Log.i("ConnectIPN-usb", "moving the link to USB on port " + port);
        usbMode = true;
        if (context != null) {
            Pairing.setUsb(context, true);
        }
        Thread switcher = new Thread(() -> {
            stopping = true;
            client.disconnect();
            attempt = 0;
            autoReconnect = true;
            connectInternal(USB_HOST, port, context == null ? "" : Pairing.token(context),
                    storageRoot(), false);
        }, "connectipn-usb-switch");
        switcher.setDaemon(true);
        switcher.start();
    }

    /** Drops the USB tunnel and redials the saved Wi-Fi address. */
    public void switchToWifi() {
        usbMode = false;
        if (context != null) {
            Pairing.setUsb(context, false);
        }
        Thread switcher = new Thread(() -> {
            stopping = true;
            client.disconnect();
            attempt = 0;
            autoReconnect = true;
            if (context == null || !Pairing.has(context)) {
                setState(State.IDLE, "Not connected");
                return;
            }
            connectInternal(Pairing.host(context), Pairing.port(context),
                    Pairing.token(context), storageRoot(), false);
        }, "connectipn-wifi-switch");
        switcher.setDaemon(true);
        switcher.start();
    }

    private void scheduleRetry() {
        if (!autoReconnect || stopping || context == null || !Pairing.has(context)) {
            return;
        }
        long delay = BACKOFF[Math.min(attempt, BACKOFF.length - 1)];
        attempt++;
        main.removeCallbacks(retry);
        main.postDelayed(retry, delay);
    }

    private File storageRoot() {
        if (context == null) {
            return new File("/sdcard");
        }
        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.R
                && !android.os.Environment.isExternalStorageManager()) {
            return context.getExternalFilesDir(null);
        }
        return android.os.Environment.getExternalStorageDirectory();
    }

    /** Pointer input for the paired PC. Ignored while there is no link. */
    public void moveMouse(int dx, int dy) {
        client.move(dx, dy);
    }

    public void mouseButton(int mask, boolean down) {
        client.button(mask, down);
    }

    public void scrollMouse(int delta) {
        client.wheel(delta);
    }

    public void sendKey(int vk, boolean down) {
        client.key(vk, down);
    }

    public void padConnect(boolean connect) {
        client.padConnect(connect);
    }

    public void padAxis(int axis, int value) {
        client.padAxis(axis, value);
    }

    public void padButton(int mask, boolean down) {
        client.padButton(mask, down);
    }

    /** User asked to stop: no automatic reconnect until they pair again. */
    public void disconnect() {
        userStopped = true;
        stopping = true;
        autoReconnect = false;
        attempt = 0;
        main.removeCallbacks(retry);
        client.disconnect();
        rootPath = "";
        setState(State.IDLE, "Not connected");
    }

    /**
     * The foreground service went away (swiped away, or killed by the system). The
     * socket is closed but the pairing stays, so the next resume reconnects.
     */
    public void onServiceStopped() {
        stopping = true;
        attempt = 0;
        main.removeCallbacks(retry);
        client.disconnect();
        rootPath = "";
        setState(State.IDLE, "Not connected");
    }

    /** Forgets the saved PC as well. */
    public void forget() {
        disconnect();
        if (context != null) {
            Pairing.clear(context);
        }
    }

    private void setState(State next, String text) {
        state = next;
        message = text;
        main.post(() -> {
            Listener current = listener;
            if (current != null) {
                current.onLinkState(next, text);
            }
        });
    }
}
