# ConnectIPN Wire Protocol (v1)

Binary, big-endian, length-prefixed. One TCP connection carries every request.
Android always dials the Windows host; Windows is the TCP listener and the
initiator of file operations.

## Frame

```
+--------+------+--------+------+---------------------------+
| u32 len| u8   | u32 id | u8   | body (len - 6 bytes)      |
|        | kind |        | op   |                           |
+--------+------+--------+------+---------------------------+
```

* `len` counts every byte after the length field (kind + id + op + body).
* `kind`: 1 = request (Android -> Windows), 2 = response (Windows -> Android).
  Requests are only sent by Windows after the handshake (the app protocol is
  symmetric: either peer may issue them).
* `id`: request id chosen by the sender; responses echo it. Many requests may
  be in flight at once (pipelining) — the receiver matches by id.
* `op`: operation code, see below.

A response body always starts with `u32 status` (0 = success, otherwise an
errno-style code). On failure no further payload follows.

## Scalar encoding

| type   | encoding                                    |
|--------|---------------------------------------------|
| u8     | 1 byte                                      |
| u16    | 2 bytes big-endian                          |
| u32    | 4 bytes big-endian                          |
| u64    | 8 bytes big-endian                          |
| i64    | 8 bytes big-endian, two's complement        |
| string | u16 length + UTF-8 bytes (no terminator)    |
| bytes  | u32 length + raw bytes                      |

## Attributes

```
u32 flags      bit0 directory, bit1 read-only
u64 size       bytes
i64 mtime      unix milliseconds
i64 ctime      unix milliseconds
```

## Operations

| op   | name       | request payload                              | success payload |
|------|------------|----------------------------------------------|-----------------|
| 0x01 | HELLO      | string token, string clientName, u32 version, string rootPath | string serverName, string rootPath |
| 0x10 | LIST       | string path                                  | u32 count, count * (string name, attrs) |
| 0x11 | STAT       | string path                                  | attrs |
| 0x12 | OPEN       | string path, u32 access, u32 disposition      | u64 handle, attrs |
| 0x13 | READ       | u64 handle, u64 offset, u32 length            | bytes |
| 0x14 | WRITE      | u64 handle, u64 offset, bytes data            | u32 written, attrs |
| 0x15 | CLOSE      | u64 handle                                    | - |
| 0x16 | FLUSH      | u64 handle                                    | - |
| 0x17 | SETSIZE    | u64 handle, u64 size                          | attrs |
| 0x20 | UNLINK     | string path                                   | - |
| 0x21 | MKDIR      | string path                                   | - |
| 0x22 | RMDIR      | string path                                   | - |
| 0x23 | RENAME     | string path, string newPath, u8 replace       | - |
| 0x24 | SETTIMES   | string path, i64 atime, i64 mtime             | - (0 means "leave unchanged") |
| 0x25 | SETATTRS   | string path, u32 flags (only bit1 used)       | - |
| 0x30 | STATFS     | string path                                   | u64 totalBytes, u64 freeBytes |
| 0x7F | PING       | -                                             | - |

### OPEN access flags
`1` read, `2` write, `4` delete. A handle opened without write access must
reject WRITE/SETSIZE with `EACCES`.

### OPEN disposition
`0` open existing (ENOENT if missing), `1` create new (EEXIST if present),
`2` open or create, `3` open existing and truncate to zero, create if missing.

### Status codes
`0` OK, `2` ENOENT, `5` EIO, `13` EACCES, `17` EEXIST, `20` ENOTDIR,
`21` EISDIR, `28` ENOSPC, `39` ENOTEMPTY, `95` EOPNOTSUPP.

## Handshake

1. Windows listens on `0.0.0.0:<port>` and renders
   `connectipn://pair?h=<ip>&p=<port>&t=<token>&v=1` as a QR code.
2. Android scans it, opens a TCP connection, sends HELLO with the token,
   its device name and protocol version.
3. Windows replies with status 0 (and its name + the shared root path) or
   closes the socket on a bad token / version.
4. From then on Windows issues file operations; Android serves them from a
   worker pool (8 threads) so slow reads do not block metadata calls.

## Performance notes

* `TCP_NODELAY` on both ends, 1 MiB socket buffers.
* Requests are pipelined; WinFsp issues reads from several threads at once and
  they share the socket, so round-trip latency does not serialise a chunk stream.
* Payloads are copied at most once per direction (no intermediate buffering).
* Frames above 8 MiB are rejected as a protocol error.
