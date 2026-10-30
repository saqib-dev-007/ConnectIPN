package com.connectipn;

import java.io.File;
import java.io.IOException;
import java.io.RandomAccessFile;
import java.nio.ByteBuffer;
import java.nio.channels.FileChannel;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.StandardCopyOption;
import java.nio.file.attribute.BasicFileAttributes;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.atomic.AtomicLong;

/**
 * Serves file operations for one shared root directory. Pure Java so it can be
 * exercised on a desktop JVM by tools/TestClientMain.java.
 */
public final class FileServer {
    private static final class Handle {
        final File file;
        final RandomAccessFile raf;
        final FileChannel channel;
        final boolean writable;
        final boolean directory;

        Handle(File file, RandomAccessFile raf, boolean writable, boolean directory) {
            this.file = file;
            this.raf = raf;
            this.channel = raf != null ? raf.getChannel() : null;
            this.writable = writable;
            this.directory = directory;
        }
    }

    private static final class Failure extends Exception {
        final int code;

        Failure(int code) {
            super("status " + code, null, false, false);
            this.code = code;
        }
    }

    private final File root;
    private final ConcurrentHashMap<Long, Handle> handles = new ConcurrentHashMap<>();
    private final AtomicLong nextHandle = new AtomicLong(1);

    public FileServer(File root) {
        this.root = root;
    }

    public String rootPath() {
        return root.getAbsolutePath();
    }

    public void closeAll() {
        for (Handle h : handles.values()) {
            closeQuietly(h);
        }
        handles.clear();
    }

    /**
     * Executes one request and returns the response payload (status first).
     */
    public byte[] execute(int op, byte[] body) {
        Protocol.Reader r = new Protocol.Reader(body);
        Protocol.Writer w = new Protocol.Writer();
        try {
            switch (op) {
                case Protocol.OP_HELLO:
                    return hello();
                case Protocol.OP_LIST:
                    return list(r);
                case Protocol.OP_STAT:
                    return stat(r);
                case Protocol.OP_OPEN:
                    return open(r);
                case Protocol.OP_READ:
                    return read(r);
                case Protocol.OP_WRITE:
                    return write(r);
                case Protocol.OP_CLOSE:
                    close(r);
                    return ok();
                case Protocol.OP_FLUSH:
                    return ok();
                case Protocol.OP_SETSIZE:
                    return setSize(r);
                case Protocol.OP_UNLINK:
                    return unlink(r);
                case Protocol.OP_MKDIR:
                    return mkdir(r);
                case Protocol.OP_RMDIR:
                    return rmdir(r);
                case Protocol.OP_RENAME:
                    return rename(r);
                case Protocol.OP_SETTIMES:
                    return setTimes(r);
                case Protocol.OP_SETATTRS:
                    return setAttrs(r);
                case Protocol.OP_STATFS:
                    return statfs();
                case Protocol.OP_PING:
                    return ok();
                default:
                    return err(Protocol.EOPNOTSUPP);
            }
        } catch (Failure f) {
            return err(f.code);
        } catch (IOException e) {
            System.err.println("op 0x" + Integer.toHexString(op) + " failed: " + e);
            return err(Protocol.EIO);
        } catch (RuntimeException e) {
            System.err.println("op 0x" + Integer.toHexString(op) + " failed: " + e);
            return err(Protocol.EIO);
        }
    }

    private byte[] hello() {
        // Token validation happens in LinkClient before any request is served.
        Protocol.Writer w = w0();
        w.str("ConnectIPN").str(root.getAbsolutePath());
        return w.toByteArray();
    }

    private Protocol.Writer w0() {
        Protocol.Writer w = new Protocol.Writer();
        w.u32(Protocol.OK);
        return w;
    }

    private byte[] list(Protocol.Reader r) throws Failure {
        File dir = resolve(r.str());
        if (!dir.exists()) {
            throw new Failure(Protocol.ENOENT);
        }
        if (!dir.isDirectory()) {
            throw new Failure(Protocol.ENOTDIR);
        }
        File[] children = dir.listFiles();
        if (children == null) {
            throw new Failure(Protocol.EACCES);
        }
        Protocol.Writer w = w0();
        w.u32(children.length);
        for (File c : children) {
            // One lstat per entry instead of isDirectory/length/lastModified (three syscalls).
            int flags = 0;
            long size = 0;
            long mtime = 0;
            try {
                BasicFileAttributes a = Files.readAttributes(
                        c.toPath(), BasicFileAttributes.class, LinkOption.NOFOLLOW_LINKS);
                if (a.isDirectory()) {
                    flags |= Protocol.ATTR_DIRECTORY;
                }
                size = a.size();
                mtime = a.lastModifiedTime().toMillis();
            } catch (IOException e) {
                if (c.isDirectory()) {
                    flags |= Protocol.ATTR_DIRECTORY;
                }
                size = c.length();
                mtime = c.lastModified();
            }
            w.str(c.getName());
            w.u32(flags);
            w.u64(flags != 0 ? 0 : size);
            w.i64(mtime);
            w.i64(mtime);
        }
        return w.toByteArray();
    }

    private byte[] stat(Protocol.Reader r) throws Failure {
        File f = resolve(r.str());
        if (!f.exists()) {
            throw new Failure(Protocol.ENOENT);
        }
        return attrs(f);
    }

    private byte[] attrs(File f) {
        Protocol.Writer w = w0();
        writeAttrs(w, f);
        return w.toByteArray();
    }

    private void writeAttrs(Protocol.Writer w, File f) {
        boolean isDir = f.isDirectory();
        w.u32((isDir ? Protocol.ATTR_DIRECTORY : 0) | (f.canWrite() ? 0 : Protocol.ATTR_READONLY));
        w.u64(isDir ? 0 : f.length());
        long mtime = f.lastModified();
        w.i64(mtime);
        w.i64(mtime);
    }

    private byte[] open(Protocol.Reader r) throws Failure, IOException {
        File f = resolve(r.str());
        int access = r.u32();
        int disposition = r.u32();
        boolean writable = (access & Protocol.ACCESS_WRITE) != 0;

        boolean exists = f.exists();
        if (!exists) {
            if (disposition == Protocol.DISPOSITION_OPEN) {
                throw new Failure(Protocol.ENOENT);
            }
            File parent = f.getParentFile();
            if (parent == null || !parent.isDirectory()) {
                throw new Failure(Protocol.ENOENT);
            }
            if (!f.createNewFile()) {
                throw new Failure(Protocol.EEXIST);
            }
        } else if (disposition == Protocol.DISPOSITION_CREATE) {
            throw new Failure(Protocol.EEXIST);
        }

        boolean isDir = f.isDirectory();
        if (isDir && writable) {
            throw new Failure(Protocol.EISDIR);
        }

        RandomAccessFile raf = null;
        if (!isDir) {
            raf = new RandomAccessFile(f, writable ? "rw" : "r");
            if (disposition == Protocol.DISPOSITION_OVERWRITE) {
                raf.getChannel().truncate(0);
            }
        }
        long id = nextHandle.getAndIncrement();
        handles.put(id, new Handle(f, raf, writable, isDir));

        Protocol.Writer w = w0();
        w.u64(id);
        writeAttrs(w, f);
        return w.toByteArray();
    }

    private byte[] read(Protocol.Reader r) throws Failure, IOException {
        long id = r.u64();
        long offset = r.u64();
        int length = r.u32();
        Handle h = handle(id);
        if (h.directory) {
            throw new Failure(Protocol.EISDIR);
        }
        if (length < 0 || length > Protocol.MAX_FRAME - 16) {
            throw new Failure(Protocol.EIO);
        }
        byte[] out = new byte[8 + length];
        Protocol.putInt(out, 0, Protocol.OK);
        ByteBuffer dst = ByteBuffer.wrap(out, 8, length);
        long pos = offset;
        int total = 0;
        while (dst.hasRemaining()) {
            int n = h.channel.read(dst, pos);
            if (n <= 0) {
                break;
            }
            pos += n;
            total += n;
        }
        Protocol.putInt(out, 4, total);
        if (total != length) {
            byte[] trimmed = new byte[8 + total];
            System.arraycopy(out, 0, trimmed, 0, trimmed.length);
            return trimmed;
        }
        return out;
    }

    private byte[] write(Protocol.Reader r) throws Failure, IOException {
        long id = r.u64();
        long offset = r.u64();
        ByteBuffer data = r.byteBuffer();
        Handle h = handle(id);
        if (h.directory) {
            throw new Failure(Protocol.EISDIR);
        }
        if (!h.writable) {
            throw new Failure(Protocol.EACCES);
        }
        ByteBuffer src = data;
        long pos = offset;
        int total = 0;
        while (src.hasRemaining()) {
            int n = h.channel.write(src, pos);
            pos += n;
            total += n;
        }
        Protocol.Writer w = w0();
        w.u32(total);
        writeAttrs(w, h.file);
        return w.toByteArray();
    }

    private void close(Protocol.Reader r) throws Failure {
        Handle h = handles.remove(r.u64());
        if (h == null) {
            throw new Failure(Protocol.EIO);
        }
        closeQuietly(h);
    }

    private byte[] setSize(Protocol.Reader r) throws Failure, IOException {
        Handle h = handle(r.u64());
        long size = r.u64();
        if (!h.writable) {
            throw new Failure(Protocol.EACCES);
        }
        if (h.directory) {
            throw new Failure(Protocol.EISDIR);
        }
        h.raf.setLength(size);
        return attrs(h.file);
    }

    private byte[] unlink(Protocol.Reader r) throws Failure {
        File f = resolve(r.str());
        if (!f.exists()) {
            throw new Failure(Protocol.ENOENT);
        }
        if (f.isDirectory()) {
            throw new Failure(Protocol.EISDIR);
        }
        if (!f.delete()) {
            throw new Failure(Protocol.EACCES);
        }
        return ok();
    }

    private byte[] mkdir(Protocol.Reader r) throws Failure {
        File f = resolve(r.str());
        if (f.exists()) {
            throw new Failure(Protocol.EEXIST);
        }
        if (!f.mkdir()) {
            throw new Failure(Protocol.EIO);
        }
        return ok();
    }

    private byte[] rmdir(Protocol.Reader r) throws Failure {
        File f = resolve(r.str());
        if (!f.exists()) {
            throw new Failure(Protocol.ENOENT);
        }
        if (!f.isDirectory()) {
            throw new Failure(Protocol.ENOTDIR);
        }
        String[] children = f.list();
        if (children != null && children.length > 0) {
            throw new Failure(Protocol.ENOTEMPTY);
        }
        if (!f.delete()) {
            throw new Failure(Protocol.EACCES);
        }
        return ok();
    }

    private byte[] rename(Protocol.Reader r) throws Failure {
        File from = resolve(r.str());
        File to = resolve(r.str());
        boolean replace = r.u8() != 0;
        if (!from.exists()) {
            throw new Failure(Protocol.ENOENT);
        }
        if (to.exists()) {
            if (!replace) {
                throw new Failure(Protocol.EEXIST);
            }
            if (!to.delete()) {
                throw new Failure(Protocol.EACCES);
            }
        }
        try {
            Files.move(from.toPath(), to.toPath(), StandardCopyOption.REPLACE_EXISTING);
        } catch (IOException e) {
            System.err.println("rename failed: " + e);
            throw new Failure(Protocol.EIO);
        }
        return ok();
    }

    private byte[] setTimes(Protocol.Reader r) throws Failure {
        File f = resolve(r.str());
        r.i64(); // atime: not representable through java.io.File
        long mtime = r.i64();
        if (!f.exists()) {
            throw new Failure(Protocol.ENOENT);
        }
        if (mtime > 0 && !f.setLastModified(mtime)) {
            throw new Failure(Protocol.EACCES);
        }
        return ok();
    }

    private byte[] setAttrs(Protocol.Reader r) throws Failure {
        File f = resolve(r.str());
        int flags = r.u32();
        if (!f.exists()) {
            throw new Failure(Protocol.ENOENT);
        }
        boolean readOnly = (flags & Protocol.ATTR_READONLY) != 0;
        if (!f.setWritable(!readOnly, false)) {
            throw new Failure(Protocol.EACCES);
        }
        return ok();
    }

    private byte[] statfs() {
        Protocol.Writer w = w0();
        w.u64(root.getTotalSpace());
        w.u64(root.getUsableSpace());
        return w.toByteArray();
    }

    private Handle handle(long id) throws Failure {
        Handle h = handles.get(id);
        if (h == null) {
            throw new Failure(Protocol.EIO);
        }
        return h;
    }

    /** Lexical path resolution: rejects any ".." segment instead of canonicalising. */
    private File resolve(String path) throws Failure {
        if (path == null || path.isEmpty() || "/".equals(path)) {
            return root;
        }
        String p = path.replace('\\', '/');
        File f = root;
        for (String part : p.split("/")) {
            if (part.isEmpty() || ".".equals(part)) {
                continue;
            }
            if ("..".equals(part)) {
                throw new Failure(Protocol.EACCES);
            }
            f = new File(f, part);
        }
        return f;
    }

    private static byte[] ok() {
        byte[] b = new byte[4];
        return b;
    }

    private static byte[] err(int code) {
        byte[] b = new byte[4];
        Protocol.putInt(b, 0, code);
        return b;
    }

    private static void closeQuietly(Handle h) {
        try {
            if (h.raf != null) {
                h.raf.close();
            }
        } catch (IOException ignored) {
            // nothing useful to do
        }
    }
}
