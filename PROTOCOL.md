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
| 0x40 | INPUT      | u8 action, i32 a, i32 b                       | none — never answered |
| 0x50 | SCREEN     | u8 source, u8 flags, u16 w, u16 h, u32 seq, bytes jpeg | none — never answered |
| 0x51 | SCREEN_CONFIG | u8 mode, u16 w, u16 h, u8 fps, u8 quality, u8 orientation, u8 aspect | none |
| 0x52 | PHONE_INPUT | u8 action, u8 flags, u16 x1,y1,x2,y2, u16 duration, u16 key, u32 extra | none |
| 0x53 | KEYMAP     | string json                                   | none |
| 0x54 | AUDIO      | u8 source, u8 flags, u16 unused, u32 seq, u32 length, bytes PCM | none — never answered |
| 0x55 | STATUS     | u8 flags, u8 reserved, u16 reserved           | none |
| 0x56 | TRANSPORT  | u8 mode, u16 port                             | none — phone redials |
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

## Phone-initiated input

The phone may send `INPUT` frames to drive the desktop pointer. They are
requests in the frame sense (kind 1) but they are **never answered**: the PC
applies the event and moves on, so a burst of movement costs one small write
per frame instead of a round trip. The request id is ignored and may be 0.

The body is always the same nine bytes, so both sides parse it the same way:

```
u8  action   1 = move, 2 = button, 3 = wheel, 4 = key
            5 = pad axis, 6 = pad button, 7 = pad connect
i32 a        move: dx · button: mask · wheel: delta · key: virtual-key code
            pad axis: axis id · pad button: button mask · pad connect: 0
i32 b        move: dy · button: 1 down / 0 up · wheel: 0 · key: 1 down / 0 up
            pad axis: value · pad button: 1 down / 0 up · pad connect: 1 open / 0 close
```

Button masks are `1` left, `2` right, `4` middle. Wheel deltas use the Win32
convention: `120` is one notch away from the user, `-120` towards them.

Keys use Windows virtual-key codes (`0x41`–`0x5A` for A–Z, `0x30`–`0x39` for
digits, `0xBA`–`0xC0` and `0xDB`–`0xDE` for the OEM punctuation keys). The
phone sends its own modifier keys — `0x10` Shift, `0x11` Ctrl, `0x12` Alt,
`0x5B` Win — as ordinary key events, so a chord is just a down/up pair wrapped
around the key and no modifier state has to be tracked on the PC.

Moves are relative and go through the desktop's normal pointer acceleration, so
`dx`/`dy` are in the same units as `WM_MOUSEMOVE`, not screen pixels.

### Virtual gamepad

`pad connect` plugs in or removes a virtual Xbox 360 controller on the PC (through
ViGEmBus), so games see a real XInput device. Sending it when the phone opens the
Controller app and again with `0` when it closes keeps the pad from lingering in
the device list.

Axis ids are `0` left X, `1` left Y, `2` right X, `3` right Y, `4` left trigger,
`5` right trigger. Sticks run `-32767`..`32767` with **Y positive upwards**, the
same convention XInput uses, so the PC can pass the value straight through.
Triggers are `0`..`255`.

Pad button ids are the XInput masks: `0x1000` A, `0x2000` B, `0x4000` X, `0x8000` Y,
`0x0100` LB, `0x0200` RB, `0x0020` Back, `0x0010` Start, `0x0001`/`0x0002`/`0x0004`/`0x0008`
for d-pad up, down, left and right.

A PC that does not recognise `0x40` must drain the body and ignore it; a phone
must tolerate the PC never answering. Because the frames are not answered, they
must not be mixed into the request-id space used by `OP_LIST` and friends —
which is why they carry id 0 and are dispatched before the pending-call table is
consulted.


## Screen mirroring and remote control (0x50–0x53)

These frames are **fire-and-forget**: kind 1, request id 0, never answered, and they
never enter the pending-call table used by file I/O. Either peer may send them.

### 0x50 SCREEN — one JPEG frame

```
u8  source    1 = phone screen, 2 = desktop screen
u8  flags     reserved, 0
u16 width     encoded frame width
u16 height    encoded frame height
u32 seq       sender's frame counter
bytes jpeg    u32 length + JPEG data
```

The receiver drops the frame if it is already decoding one, so a slow decoder
trades frame rate rather than adding latency.

### 0x51 SCREEN_CONFIG — start, stop or reshape a stream

```
u8  mode        1 start phone -> PC   2 stop phone -> PC
                3 start PC -> phone   4 stop PC -> phone
u16 width       requested capture width  (0 = native)
u16 height      requested capture height (0 = native)
u8  fps         1..60
u8  quality     10..95 (JPEG)
u8  orientation 0 auto, 1 portrait, 2 landscape
u8  aspect      0 native, 1 16:9, 2 4:3, 3 fill
```

The phone rebuilds its virtual display in place when it receives mode 1 while
already streaming, so the PC can change resolution without a second consent dialog.

### 0x52 PHONE_INPUT — PC drives the phone

```
u8  action    1 tap, 2 swipe, 3 long-press, 4 key
u8  flags     reserved
u16 x1, y1    start point, 0..65535 across the phone screen
u16 x2, y2    end point (swipe only)
u16 duration  gesture duration in ms
u16 key       4 back, 3 home, 187 recents
u32 extra     reserved
```

Android applies these through an AccessibilityService (`dispatchGesture`), which
the user enables once in Settings. Without it the frames are accepted and ignored.

### 0x53 KEYMAP — PC pushes a layout

```
string json   {"name":"...","controls":[{"type":"button|stick|look", ...}]}
```

The phone stores the layout and the keymap overlay reloads it immediately. The
overlay injects touches through the same AccessibilityService.

### 0x54 AUDIO — phone speaker mix

```
u8  source   1 = phone, 2 = PC (reserved)
u8  flags    0
u16 unused
u32 seq      frame counter
u32 length   byte count of the PCM that follows
bytes pcm    signed 16-bit little-endian, stereo, 48 000 Hz
```

The phone sends 20 ms chunks (3 840 bytes) while mirroring, so the PC can play the
sound with a small buffer. Capture uses Android's `AudioPlaybackCapture`, which only
includes apps that allow playback capture — the platform decides, not this app.

### 0x55 STATUS — what the phone can do

```
u8  flags     bit0 accessibility control ready
              bit1 overlay permission granted
              bit2 audio capture supported
              bit3 audio currently running
u8  reserved
u16 reserved
```

Sent after the handshake, whenever the accessibility service connects, and after
every stream start. The PC shows a "PC control is off" banner while bit0 is clear.

### 0x56 TRANSPORT — move the link onto the USB cable

```
u8  mode      1 = USB
u16 port      loopback port the phone should dial (47653)
```

Sent by the PC after it opens an adb reverse tunnel with
`adb reverse tcp:47653 tcp:<its listening port>`. The phone closes the current
socket and redials `127.0.0.1:<port>`, which the tunnel carries down the cable into
the same listener. The PC recognises the new connection by its loopback remote
address and reports the link as USB.

The phone remembers the choice and keeps preferring USB on later reconnects. If a
USB connect fails — cable pulled, adb server restarted — it clears that preference
and falls back to the saved Wi-Fi address on the next retry.

## Reconnecting

The pairing token is persisted on the PC, so it survives restarts. Android stores
host, port and token after the first successful handshake and reconnects on launch
with a short backoff (1.5 s → 15 s). Scanning the QR code is only needed the first
time, or after the phone forgets the PC.

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
