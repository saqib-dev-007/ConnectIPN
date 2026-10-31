package com.connectipn;

import android.os.Looper;

import java.io.BufferedInputStream;
import java.io.BufferedOutputStream;
import java.io.EOFException;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.util.ArrayList;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.LinkedBlockingQueue;
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

    /**
     * Screen mirroring callbacks. All methods run on the socket reader thread, so an
     * implementation that touches views must post to the main thread itself.
     */
    /** A keymap layout pushed by the PC. */
    public interface KeymapListener {
        void onKeymap(String json);
    }

    /** Touch/key events the PC sends to drive this phone. */
    public interface InputListener {
        void onPhoneInput(int action, int x1, int y1, int x2, int y2, int duration, int key);
    }

    /** The PC asked the link to move onto another transport (the USB cable). */
    public interface TransportListener {
        void onTransport(int mode, int port);
    }

    public interface FrameListener {
        default void onScreenFrame(byte[] jpeg, int width, int height, int source, int seq) {
        }

        default void onScreenConfig(int mode, int width, int height, int fps,
                                    int quality, int orientation, int aspect) {
        }

        default void onKeymap(String json) {
        }
    }

    private final ExecutorService workers = Executors.newFixedThreadPool(12, r -> {
        Thread t = new Thread(r, "connectipn-worker");
        t.setDaemon(true);
        return t;
    });
    private final AtomicBoolean running = new AtomicBoolean();
    private final Object writeLock = new Object();

    /** Input events are queued so the touchpad never writes to the socket on the UI thread. */
    private final LinkedBlockingQueue<int[]> inputQueue = new LinkedBlockingQueue<>(256);

    /**
     * Control frames (STATUS, SCREEN_CONFIG) that a UI callback asked for. They are
     * already fully framed, so the writer thread only has to push bytes out.
     */
    private final LinkedBlockingQueue<byte[]> controlQueue = new LinkedBlockingQueue<>(32);

    private Socket socket;
    private InputStream in;
    private OutputStream out;
    private FileServer files;
    private Thread reader;
    private volatile Thread inputThread;
    private volatile Thread controlThread;
    private Listener listener;
    private volatile FrameListener frameListener;
    private volatile InputListener inputListener;
    private volatile KeymapListener keymapListener;
    private volatile TransportListener transportListener;
    private volatile String rootPath = "";

    public String rootPath() {
        return rootPath;
    }

    public void setListener(Listener listener) {
        this.listener = listener;
    }

    public void setFrameListener(FrameListener listener) {
        this.frameListener = listener;
    }

    public void setInputListener(InputListener listener) {
        this.inputListener = listener;
    }

    public void setKeymapListener(KeymapListener listener) {
        this.keymapListener = listener;
    }

    public void setTransportListener(TransportListener listener) {
        this.transportListener = listener;
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

        inputThread = new Thread(this::inputLoop, "connectipn-input");
        inputThread.setDaemon(true);
        inputThread.start();

        controlThread = new Thread(this::controlLoop, "connectipn-control");
        controlThread.setDaemon(true);
        controlThread.start();

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

                // Screen traffic is high-rate and never answered, so it bypasses the
                // worker pool and the file server entirely.
                if (op == Protocol.OP_SCREEN) {
                    handleScreenFrame(body);
                    continue;
                }
                if (op == Protocol.OP_SCREEN_CONFIG) {
                    handleScreenConfig(body);
                    continue;
                }
                if (op == Protocol.OP_KEYMAP) {
                    handleKeymap(body);
                    continue;
                }
                if (op == Protocol.OP_PHONE_INPUT) {
                    handlePhoneInput(body);
                    continue;
                }
                if (op == Protocol.OP_AUDIO) {
                    // PC -> phone audio is not implemented; drain and ignore.
                    continue;
                }
                if (op == Protocol.OP_STATUS) {
                    continue;
                }
                if (op == Protocol.OP_TRANSPORT) {
                    handleTransport(body);
                    continue;
                }

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
            stopInput();
            stopControl();
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

    private void handleScreenFrame(byte[] body) {
        if (body.length < 14) {
            return;
        }
        Protocol.Reader r = new Protocol.Reader(body);
        int source = r.u8();
        r.u8(); // flags
        int width = r.u16();
        int height = r.u16();
        int seq = r.u32();
        byte[] jpeg = r.bytes();
        FrameListener l = frameListener;
        if (l != null) {
            l.onScreenFrame(jpeg, width, height, source, seq);
        }
    }

    private void handleScreenConfig(byte[] body) {
        if (body.length < 9) {
            return;
        }
        Protocol.Reader r = new Protocol.Reader(body);
        int mode = r.u8();
        int width = r.u16();
        int height = r.u16();
        int fps = r.u8();
        int quality = r.u8();
        int orientation = r.u8();
        int aspect = r.u8();
        FrameListener l = frameListener;
        if (l != null) {
            l.onScreenConfig(mode, width, height, fps, quality, orientation, aspect);
        }
    }

    private void handlePhoneInput(byte[] body) {
        if (body.length < 18) {
            return;
        }
        Protocol.Reader r = new Protocol.Reader(body);
        int action = r.u8();
        r.u8(); // flags
        int x1 = r.u16();
        int y1 = r.u16();
        int x2 = r.u16();
        int y2 = r.u16();
        int duration = r.u16();
        int key = r.u16();
        InputListener l = inputListener;
        if (l != null) {
            l.onPhoneInput(action, x1, y1, x2, y2, duration, key);
        }
    }

    /**
     * The PC set up an adb reverse tunnel and wants the link carried over USB.
     * Body: u8 mode, u16 port. Everything else is left to the listener, which owns
     * the reconnect: the socket this arrived on is about to be thrown away.
     */
    private void handleTransport(byte[] body) {
        if (body.length < 3) {
            return;
        }
        Protocol.Reader r = new Protocol.Reader(body);
        int mode = r.u8();
        int port = r.u16();
        TransportListener listener = transportListener;
        if (listener != null) {
            listener.onTransport(mode, port);
        }
    }

    private void handleKeymap(byte[] body) {
        Protocol.Reader r = new Protocol.Reader(body);
        String json = r.str();
        KeymapListener listener = keymapListener;
        if (listener != null) {
            listener.onKeymap(json);
        }
        FrameListener frames = frameListener;
        if (frames != null) {
            frames.onKeymap(json);
        }
    }

    /** Sends one JPEG frame. Fire-and-forget; id 0 is never answered. */
    public void sendScreenFrame(byte[] jpeg, int width, int height, int source, int seq) {
        if (!running.get()) {
            return;
        }
        byte[] header = new byte[24];
        Protocol.putInt(header, 0, 6 + 14 + jpeg.length);
        header[4] = (byte) Protocol.KIND_REQUEST;
        Protocol.putInt(header, 5, 0);
        header[9] = (byte) Protocol.OP_SCREEN;
        header[10] = (byte) source;
        header[11] = 0;
        header[12] = (byte) (width >>> 8);
        header[13] = (byte) width;
        header[14] = (byte) (height >>> 8);
        header[15] = (byte) height;
        Protocol.putInt(header, 16, seq);
        Protocol.putInt(header, 20, jpeg.length);
        try {
            synchronized (writeLock) {
                out.write(header);
                out.write(jpeg);
                out.flush();
            }
        } catch (IOException e) {
            running.set(false);
            closeQuietly();
        }
    }

    /** Sends one block of 16-bit stereo PCM. Fire-and-forget, id 0. */
    public void sendAudio(byte[] pcm, int length) {
        if (!running.get() || length <= 0) {
            return;
        }
        byte[] body = new byte[12 + length];
        body[0] = (byte) Protocol.SCREEN_PHONE;
        body[1] = 0;
        // bytes 2..3 reserved
        Protocol.putInt(body, 4, 0);            // sequence, unused by the player
        Protocol.putInt(body, 8, length);
        System.arraycopy(pcm, 0, body, 12, length);
        try {
            synchronized (writeLock) {
                Protocol.writeFrame(out, Protocol.KIND_REQUEST, 0, Protocol.OP_AUDIO, body);
                out.flush();
            }
        } catch (IOException e) {
            running.set(false);
            closeQuietly();
        }
    }

    /**
     * Sends a capability report so the PC can tell the user what is missing.
     *
     * <p>Called from {@code onResume} of most screens, which is the main thread. The
     * frame is handed to the control thread there: writing to the socket directly
     * throws {@link android.os.NetworkOnMainThreadException} and kills the process.
     */
    public void sendStatus(int flags) {
        if (!running.get()) {
            return;
        }
        byte[] body = new byte[4];
        body[0] = (byte) flags;
        writeControl(buildFrame(Protocol.OP_STATUS, body));
    }

    /** Start or stop a stream, and choose its shape. */
    public void sendScreenConfig(int mode, int width, int height, int fps,
                                 int quality, int orientation, int aspect) {
        if (!running.get()) {
            return;
        }
        byte[] body = new byte[9];
        body[0] = (byte) mode;
        body[1] = (byte) (width >>> 8);
        body[2] = (byte) width;
        body[3] = (byte) (height >>> 8);
        body[4] = (byte) height;
        body[5] = (byte) Math.max(1, Math.min(60, fps));
        body[6] = (byte) Math.max(10, Math.min(95, quality));
        body[7] = (byte) orientation;
        body[8] = (byte) aspect;
        writeControl(buildFrame(Protocol.OP_SCREEN_CONFIG, body));
    }

    /** Lays out a complete frame: 10 byte header plus the body. */
    private static byte[] buildFrame(int op, byte[] body) {
        byte[] frame = new byte[Protocol.HEADER_SIZE + body.length];
        Protocol.putInt(frame, 0, 6 + body.length);
        frame[4] = (byte) Protocol.KIND_REQUEST;
        Protocol.putInt(frame, 5, 0);
        frame[9] = (byte) op;
        System.arraycopy(body, 0, frame, Protocol.HEADER_SIZE, body.length);
        return frame;
    }

    private static boolean onMainThread() {
        Looper main = Looper.getMainLooper();
        return main != null && Looper.myLooper() == main;
    }

    /**
     * Writes a control frame without ever blocking the UI thread.
     *
     * <p>Callers that are already on a background thread keep the old behaviour and
     * write straight away, so a SCREEN_CONFIG sent from the capture thread still
     * lands before the first frame that follows it.
     */
    private void writeControl(byte[] frame) {
        if (frame == null || out == null) {
            return;
        }
        if (!onMainThread()) {
            writeNow(frame);
            return;
        }
        // Bounded on purpose: a stalled link must not let this grow without limit.
        if (!controlQueue.offer(frame)) {
            controlQueue.poll();
            controlQueue.offer(frame);
        }
    }

    private void writeNow(byte[] frame) {
        try {
            synchronized (writeLock) {
                out.write(frame);
                out.flush();
            }
        } catch (IOException e) {
            running.set(false);
            closeQuietly();
        }
    }

    private void controlLoop() {
        while (running.get()) {
            byte[] frame;
            try {
                frame = controlQueue.take();
            } catch (InterruptedException e) {
                return;
            }
            writeNow(frame);
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

    /** Relative pointer movement, in screen pixels on the PC. */
    public void move(int dx, int dy) {
        if (dx != 0 || dy != 0) {
            offerInput(Protocol.INPUT_MOVE, dx, dy);
        }
    }

    public void button(int mask, boolean down) {
        offerInput(Protocol.INPUT_BUTTON, mask, down ? 1 : 0);
    }

    /** One notch is 120; positive scrolls up. */
    public void wheel(int delta) {
        if (delta != 0) {
            offerInput(Protocol.INPUT_WHEEL, delta, 0);
        }
    }

    /** A Windows virtual-key code, pressed or released. */
    public void key(int vk, boolean down) {
        if (vk != 0) {
            offerInput(Protocol.INPUT_KEY, vk, down ? 1 : 0);
        }
    }

    /** Plug in or remove the virtual Xbox 360 pad on the PC. */
    public void padConnect(boolean connect) {
        offerInput(Protocol.INPUT_PAD_CONNECT, 0, connect ? 1 : 0);
    }

    public void padAxis(int axis, int value) {
        offerInput(Protocol.INPUT_PAD_AXIS, axis, value);
    }

    public void padButton(int mask, boolean down) {
        offerInput(Protocol.INPUT_PAD_BUTTON, mask, down ? 1 : 0);
    }

    private void offerInput(int action, int a, int b) {
        if (!running.get()) {
            return;
        }
        // When the link falls behind, dropping the newest event is better than
        // replaying stale finger movement a second later.
        inputQueue.offer(new int[] { action, a, b });
    }

    private void inputLoop() {
        List<int[]> batch = new ArrayList<>(64);
        while (running.get()) {
            int[] first;
            try {
                first = inputQueue.take();
            } catch (InterruptedException e) {
                return;
            }
            batch.clear();
            batch.add(first);
            inputQueue.drainTo(batch, 63);

            // Consecutive moves collapse into one frame so a fast swipe sends a
            // handful of writes instead of one per touch sample.
            int dx = 0;
            int dy = 0;
            for (int[] event : batch) {
                if (event[0] == Protocol.INPUT_MOVE) {
                    dx += event[1];
                    dy += event[2];
                    continue;
                }
                if (dx != 0 || dy != 0) {
                    writeInput(Protocol.INPUT_MOVE, dx, dy);
                    dx = 0;
                    dy = 0;
                }
                writeInput(event[0], event[1], event[2]);
            }
            if (dx != 0 || dy != 0) {
                writeInput(Protocol.INPUT_MOVE, dx, dy);
            }
        }
    }

    private void writeInput(int action, int a, int b) {
        Protocol.Writer writer = new Protocol.Writer();
        writer.u8(action).u32(a).u32(b);
        byte[] body = writer.toByteArray();
        try {
            synchronized (writeLock) {
                // Request id 0: the host never answers an input frame, so there is
                // nothing for it to echo back.
                Protocol.writeFrame(out, Protocol.KIND_REQUEST, 0, Protocol.OP_INPUT, body);
                out.flush();
            }
        } catch (IOException e) {
            running.set(false);
            closeQuietly();
        }
    }

    private void stopInput() {
        Thread thread = inputThread;
        inputThread = null;
        if (thread != null) {
            thread.interrupt();
        }
    }

    private void stopControl() {
        Thread thread = controlThread;
        controlThread = null;
        if (thread != null) {
            thread.interrupt();
        }
        controlQueue.clear();
    }

    public void disconnect() {
        running.set(false);
        stopInput();
        stopControl();
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
