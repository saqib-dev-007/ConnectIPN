package com.connectipn;

import java.io.ByteArrayOutputStream;
import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;

/** Wire protocol constants and frame primitives. Mirrors windows/ConnectIPN.Desktop/Protocol/Protocol.cs. */
public final class Protocol {
    public static final int VERSION = 1;

    public static final int KIND_REQUEST = 1;
    public static final int KIND_RESPONSE = 2;

    public static final int OP_HELLO = 0x01;
    public static final int OP_LIST = 0x10;
    public static final int OP_STAT = 0x11;
    public static final int OP_OPEN = 0x12;
    public static final int OP_READ = 0x13;
    public static final int OP_WRITE = 0x14;
    public static final int OP_CLOSE = 0x15;
    public static final int OP_FLUSH = 0x16;
    public static final int OP_SETSIZE = 0x17;
    public static final int OP_UNLINK = 0x20;
    public static final int OP_MKDIR = 0x21;
    public static final int OP_RMDIR = 0x22;
    public static final int OP_RENAME = 0x23;
    public static final int OP_SETTIMES = 0x24;
    public static final int OP_SETATTRS = 0x25;
    public static final int OP_STATFS = 0x30;
    public static final int OP_PING = 0x7F;

    /**
     * Phone -> PC input. Sent as a request frame but never answered: the PC applies
     * the event and moves on, so a burst of moves costs one write each.
     * Body is always u8 action, i32 a, i32 b.
     */
    public static final int OP_INPUT = 0x40;

    // --- screen mirroring / remote control -------------------------------------
    // Fire-and-forget request frames (id 0, never answered), same as OP_INPUT.

    /** One encoded screen frame: u8 source, u8 flags, u16 w, u16 h, u32 seq, bytes jpeg. */
    public static final int OP_SCREEN = 0x50;

    /** u8 mode, u16 w, u16 h, u8 fps, u8 quality, u8 orientation, u8 aspect. */
    public static final int OP_SCREEN_CONFIG = 0x51;

    /** PC -> phone: u8 action, u8 flags, u16 x1, u16 y1, u16 x2, u16 y2,
     *  u16 duration, u16 key, u32 extra. */
    public static final int OP_PHONE_INPUT = 0x52;

    /** PC -> phone keymap layout, string json. */
    public static final int OP_KEYMAP = 0x53;

    /** Raw PCM audio for the mirrored screen: u8 source, u8 flags, u16 unused,
     *  u32 seq, u32 length, bytes (s16le, stereo, 48 kHz). */
    public static final int OP_AUDIO = 0x54;

    /** Phone -> PC capability report: u8 flags, u8 reserved, u16 reserved. */
    public static final int OP_STATUS = 0x55;

    /** PC -> phone: move the link onto the USB cable. Body: u8 mode, u16 port. */
    public static final int OP_TRANSPORT = 0x56;

    public static final int TRANSPORT_USB = 1;

    public static final int STATUS_CONTROL = 0x01;
    public static final int STATUS_OVERLAY = 0x02;
    public static final int STATUS_AUDIO_CAPABLE = 0x04;
    public static final int STATUS_AUDIO_RUNNING = 0x08;

    public static final int SCREEN_PHONE = 1;
    public static final int SCREEN_DESKTOP = 2;

    public static final int SCREEN_START_PHONE = 1;
    public static final int SCREEN_STOP_PHONE = 2;
    public static final int SCREEN_START_DESKTOP = 3;
    public static final int SCREEN_STOP_DESKTOP = 4;

    public static final int PHONE_TAP = 1;
    public static final int PHONE_SWIPE = 2;
    public static final int PHONE_LONG_PRESS = 3;
    public static final int PHONE_KEY = 4;

    public static final int ANDROID_KEY_BACK = 4;
    public static final int ANDROID_KEY_HOME = 3;
    public static final int ANDROID_KEY_RECENTS = 187;

    public static final int ORIENT_AUTO = 0;
    public static final int ORIENT_PORTRAIT = 1;
    public static final int ORIENT_LANDSCAPE = 2;

    public static final int ASPECT_NATIVE = 0;
    public static final int ASPECT_16_9 = 1;
    public static final int ASPECT_4_3 = 2;
    public static final int ASPECT_FILL = 3;

    public static final int INPUT_MOVE = 1;
    public static final int INPUT_BUTTON = 2;
    public static final int INPUT_WHEEL = 3;
    public static final int INPUT_KEY = 4;
    public static final int INPUT_PAD_AXIS = 5;
    public static final int INPUT_PAD_BUTTON = 6;
    public static final int INPUT_PAD_CONNECT = 7;

    /** Axis ids. Y is positive upwards, matching XInput. */
    public static final int PAD_LEFT_X = 0;
    public static final int PAD_LEFT_Y = 1;
    public static final int PAD_RIGHT_X = 2;
    public static final int PAD_RIGHT_Y = 3;
    public static final int PAD_LEFT_TRIGGER = 4;
    public static final int PAD_RIGHT_TRIGGER = 5;

    /** XInput button masks. */
    public static final int PAD_A = 0x1000;
    public static final int PAD_B = 0x2000;
    public static final int PAD_X = 0x4000;
    public static final int PAD_Y = 0x8000;
    public static final int PAD_LB = 0x0100;
    public static final int PAD_RB = 0x0200;
    public static final int PAD_BACK = 0x0020;
    public static final int PAD_START = 0x0010;
    public static final int PAD_UP = 0x0001;
    public static final int PAD_DOWN = 0x0002;
    public static final int PAD_LEFT = 0x0004;
    public static final int PAD_RIGHT = 0x0008;

    public static final int BUTTON_LEFT = 1;
    public static final int BUTTON_RIGHT = 2;
    public static final int BUTTON_MIDDLE = 4;

    public static final int ATTR_DIRECTORY = 1;
    public static final int ATTR_READONLY = 2;

    public static final int ACCESS_READ = 1;
    public static final int ACCESS_WRITE = 2;
    public static final int ACCESS_DELETE = 4;

    public static final int DISPOSITION_OPEN = 0;
    public static final int DISPOSITION_CREATE = 1;
    public static final int DISPOSITION_OPEN_OR_CREATE = 2;
    public static final int DISPOSITION_OVERWRITE = 3;

    public static final int OK = 0;
    public static final int ENOENT = 2;
    public static final int EIO = 5;
    public static final int EACCES = 13;
    public static final int EEXIST = 17;
    public static final int ENOTDIR = 20;
    public static final int EISDIR = 21;
    public static final int ENOSPC = 28;
    public static final int ENOTEMPTY = 39;
    public static final int EOPNOTSUPP = 95;

    public static final int MAX_FRAME = 8 * 1024 * 1024;
    public static final int HEADER_SIZE = 10; // len(4) + kind(1) + id(4) + op(1)

    private Protocol() {
    }

    public static void writeFrame(OutputStream out, int kind, int id, int op, byte[] body)
            throws IOException {
        int len = 6 + body.length;
        byte[] header = new byte[HEADER_SIZE];
        putInt(header, 0, len);
        header[4] = (byte) kind;
        putInt(header, 5, id);
        header[9] = (byte) op;
        out.write(header);
        out.write(body);
    }

    public static void putInt(byte[] b, int off, int v) {
        b[off] = (byte) (v >>> 24);
        b[off + 1] = (byte) (v >>> 16);
        b[off + 2] = (byte) (v >>> 8);
        b[off + 3] = (byte) v;
    }

    /** Reads one full frame body (everything after the 10 byte header). */
    public static byte[] readFrame(InputStream in, int[] meta) throws IOException {
        byte[] header = new byte[HEADER_SIZE];
        readFully(in, header, 0, HEADER_SIZE);
        int len = getInt(header, 0);
        if (len < 6 || len > MAX_FRAME) {
            throw new IOException("bad frame length " + len);
        }
        meta[0] = header[4] & 0xFF;
        meta[1] = getInt(header, 5);
        meta[2] = header[9] & 0xFF;
        byte[] body = new byte[len - 6];
        readFully(in, body, 0, body.length);
        return body;
    }

    public static int getInt(byte[] b, int off) {
        return ((b[off] & 0xFF) << 24) | ((b[off + 1] & 0xFF) << 16)
                | ((b[off + 2] & 0xFF) << 8) | (b[off + 3] & 0xFF);
    }

    private static void readFully(InputStream in, byte[] b, int off, int len) throws IOException {
        int done = 0;
        while (done < len) {
            int n = in.read(b, off + done, len - done);
            if (n < 0) {
                throw new EOFException("link closed");
            }
            done += n;
        }
    }

    /** Big-endian payload reader. */
    public static final class Reader {
        private final ByteBuffer buf;

        public Reader(byte[] data) {
            this(data, 0, data.length);
        }

        public Reader(byte[] data, int off, int len) {
            buf = ByteBuffer.wrap(data, off, len);
        }

        public int u8() {
            return buf.get() & 0xFF;
        }

        public int u32() {
            return buf.getInt();
        }

        public int u16() {
            return buf.getShort() & 0xFFFF;
        }

        public long u64() {
            return buf.getLong();
        }

        public long i64() {
            return buf.getLong();
        }

        public String str() {
            int len = buf.getShort() & 0xFFFF;
            byte[] b = new byte[len];
            buf.get(b);
            return new String(b, StandardCharsets.UTF_8);
        }

        /** Zero-copy view of a byte block (no array copy). */
        public java.nio.ByteBuffer byteBuffer() {
            int len = buf.getInt();
            if (len < 0 || len > buf.remaining()) {
                throw new IllegalArgumentException("bad byte length " + len);
            }
            java.nio.ByteBuffer slice = buf.slice();
            slice.limit(len);
            buf.position(buf.position() + len);
            return slice;
        }

        public byte[] bytes() {
            int len = buf.getInt();
            if (len < 0 || len > buf.remaining()) {
                throw new IllegalArgumentException("bad byte length " + len);
            }
            byte[] b = new byte[len];
            buf.get(b);
            return b;
        }

        public int remaining() {
            return buf.remaining();
        }
    }

    /** Big-endian payload writer. */
    public static final class Writer {
        private final ByteArrayOutputStream out = new ByteArrayOutputStream(256);

        public Writer u8(int v) {
            out.write(v & 0xFF);
            return this;
        }

        public Writer u32(int v) {
            out.write(v >>> 24);
            out.write(v >>> 16);
            out.write(v >>> 8);
            out.write(v);
            return this;
        }

        public Writer u64(long v) {
            return i64(v);
        }

        public Writer i64(long v) {
            out.write((int) (v >>> 56));
            out.write((int) (v >>> 48));
            out.write((int) (v >>> 40));
            out.write((int) (v >>> 32));
            out.write((int) (v >>> 24));
            out.write((int) (v >>> 16));
            out.write((int) (v >>> 8));
            out.write((int) v);
            return this;
        }

        public Writer str(String s) {
            byte[] b = s.getBytes(StandardCharsets.UTF_8);
            if (b.length > 0xFFFF) {
                throw new IllegalArgumentException("string too long");
            }
            out.write(b.length >>> 8);
            out.write(b.length);
            out.write(b, 0, b.length);
            return this;
        }

        public Writer raw(byte[] b) {
            out.write(b, 0, b.length);
            return this;
        }

        public byte[] toByteArray() {
            return out.toByteArray();
        }
    }
}
