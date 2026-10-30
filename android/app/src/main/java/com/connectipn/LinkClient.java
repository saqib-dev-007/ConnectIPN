package com.connectipn;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.EOFException;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;

/**
 * Dials the Windows host, performs the HELLO handshake and then serves file
 * operations on that socket. One reader thread + a small worker pool keeps
 * metadata calls responsive while a large read is in flight.
 */
public final class LinkClient {
    public interface Listener {
        void onConnected(String host, String rootPath);

        void onDisconnected(String reason);
    }

    private final ExecutorService workers = Executors.newFixedThreadPool(12, r -> {
        Thread t = new Thread(r, "connectipn-worker");
        t.setDaemon(true);
        return t;
    });
    private final AtomicBoolean running = new AtomicBoolean();
    private final Object writeLock = new Object();

    private Socket socket;
    private InputStream in;
    private OutputStream out;
    private FileServer files;
    private Thread reader;
    private Listener listener;
    private volatile String rootPath = "";

    public String rootPath() {
        return rootPath;
    }

    public void setListener(Listener listener) {
        this.listener = listener;
    }

    public boolean isConnected() {
        return running.get();
    }

    public void connect(String host, int port, String token, String deviceName, File root)
            throws IOException {
        if (running.get()) {
            throw new IOException("already connected");
        }
        Socket s = new Socket();
        s.setTcpNoDelay(true);
        s.setReceiveBufferSize(1 << 20);
        s.setSendBufferSize(1 << 20);
        s.setKeepAlive(true);
        s.connect(new InetSocketAddress(host, port), 6000);
        socket = s;
        in = new BufferedInputStream(s.getInputStream(), 1 << 16);
        out = new BufferedOutputStream(s.getOutputStream(), 1 << 16);
        files = new FileServer(root);

        Protocol.Writer hello = new Protocol.Writer();
        hello.str(token).str(deviceName).u32(Protocol.VERSION).str(root.getAbsolutePath());
        byte[] body = hello.toByteArray();
        synchronized (writeLock) {
            Protocol.writeFrame(out, Protocol.KIND_REQUEST, 1, Protocol.OP_HELLO, body);
            out.flush();
        }

        int[] meta = new int[3];
        byte[] reply = Protocol.readFrame(in, meta);
        if (meta[0] != Protocol.KIND_RESPONSE) {
            closeQuietly();
            throw new IOException("unexpected handshake frame");
        }
        Protocol.Reader r = new Protocol.Reader(reply);
        int status = r.u32();
        if (status != Protocol.OK) {
            closeQuietly();
            throw new IOException(status == Protocol.EACCES
                    ? "pairing token rejected" : "handshake failed (" + status + ")");
        }
        r.str(); // server name
        rootPath = r.str();

        running.set(true);
        reader = new Thread(this::readLoop, "connectipn-reader");
        reader.setDaemon(true);
        reader.start();

        Listener l = listener;
        if (l != null) {
            l.onConnected(host, rootPath);
        }
    }

    private void readLoop() {
        int[] meta = new int[3];
        String reason = "link closed";
        try {
            while (running.get()) {
                byte[] body = Protocol.readFrame(in, meta);
                if (meta[0] != Protocol.KIND_REQUEST) {
                    continue;
                }
                final int id = meta[1];
                final int op = meta[2];
                workers.execute(() -> serve(id, op, body));
            }
        } catch (EOFException e) {
            reason = "host disconnected";
        } catch (IOException e) {
            reason = e.getMessage() != null ? e.getMessage() : "socket error";
        } catch (RuntimeException e) {
            reason = "protocol error: " + e;
        } finally {
            boolean wasRunning = running.getAndSet(false);
            closeQuietly();
            if (files != null) {
                files.closeAll();
            }
            Listener l = listener;
            if (l != null && wasRunning) {
                l.onDisconnected(reason);
            }
        }
    }

    private void serve(int id, int op, byte[] body) {
        if (!running.get()) {
            return;
        }
        byte[] payload;
        try {
            payload = files.execute(op, body);
        } catch (RuntimeException e) {
            payload = new byte[4];
            Protocol.putInt(payload, 0, Protocol.EIO);
        }
        try {
            synchronized (writeLock) {
                Protocol.writeFrame(out, Protocol.KIND_RESPONSE, id, op, payload);
                out.flush();
            }
        } catch (IOException e) {
            running.set(false);
            closeQuietly();
        }
    }

    public void disconnect() {
        running.set(false);
        closeQuietly();
        if (reader != null) {
            try {
                reader.join(1000);
            } catch (InterruptedException e) {
                Thread.currentThread().interrupt();
            }
        }
        if (files != null) {
            files.closeAll();
        }
    }

    private void closeQuietly() {
        try {
            if (socket != null) {
                socket.close();
            }
        } catch (IOException ignored) {
            // already closed
        }
    }
}
