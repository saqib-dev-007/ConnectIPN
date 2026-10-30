package com.connectipn;

import android.os.Handler;
import android.os.Looper;

import java.io.File;

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
    private Thread worker;

    private LinkManager() {
    }

    public static LinkManager get() {
        return INSTANCE;
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

    public void connect(String host, int port, String token, File root) {
        if (worker != null && worker.isAlive()) {
            return;
        }
        stopping = false;
        setState(State.CONNECTING, "Connecting to " + host + ":" + port);

        client.setListener(new LinkClient.Listener() {
            @Override
            public void onConnected(String hostName, String sharedRoot) {
                // LinkManager publishes the state after connect() returns.
            }

            @Override
            public void onDisconnected(String reason) {
                if (!stopping) {
                    setState(State.ERROR, "Link lost: " + reason);
                }
            }
        });

        worker = new Thread(() -> {
            try {
                client.connect(host, port, token, android.os.Build.MODEL, root);
                rootPath = client.rootPath();
                setState(State.CONNECTED, "Connected to " + host);
            } catch (Exception e) {
                String detail = e.getMessage() != null ? e.getMessage() : e.toString();
                setState(State.ERROR, "Could not connect: " + detail);
            }
        }, "connectipn-connect");
        worker.setDaemon(true);
        worker.start();
    }

    public void disconnect() {
        stopping = true;
        client.disconnect();
        rootPath = "";
        setState(State.IDLE, "Not connected");
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
