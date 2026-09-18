#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
C# 桥的测试客户端。零依赖（stdlib）。

用法:
    python probe.py --status              # 查 /status
    python probe.py --shot out.png        # 把微信窗口截图存下来
    python probe.py --send "内容"          # 通过 WebSocket 发一条，看桥的回应
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import socket
import struct
import sys
import time
import urllib.request

HOST = "127.0.0.1"
PORT = 8765
WS_MAGIC = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
OP_TEXT, OP_CLOSE = 0x1, 0x8


class Ws:
    def __init__(self, host=HOST, port=PORT, path="/ws", timeout=10.0):
        self.sock = socket.create_connection((host, port), timeout=timeout)
        self.buf = bytearray()
        key = base64.b64encode(os.urandom(16)).decode()
        self.sock.sendall((
            "GET %s HTTP/1.1\r\nHost: %s:%d\r\n"
            "Upgrade: websocket\r\nConnection: Upgrade\r\n"
            "Sec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\n\r\n"
            % (path, host, port, key)
        ).encode())
        while b"\r\n\r\n" not in self.buf:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise ConnectionError("握手失败")
            self.buf += chunk
        head, _, rest = bytes(self.buf).partition(b"\r\n\r\n")
        if b"101" not in head.split(b"\r\n")[0]:
            raise ConnectionError("不是 101: " + head.split(b"\r\n")[0].decode("latin-1"))
        self.buf = bytearray(rest)

    def _read(self, n):
        while len(self.buf) < n:
            chunk = self.sock.recv(max(4096, n - len(self.buf)))
            if not chunk:
                raise ConnectionError("连接关闭")
            self.buf += chunk
        out = bytes(self.buf[:n]); del self.buf[:n]
        return out

    def send(self, text):
        payload = text.encode()
        mask = os.urandom(4)
        n = len(payload)
        hdr = bytearray([0x80 | OP_TEXT])
        if n < 126:
            hdr.append(0x80 | n)
        elif n < 65536:
            hdr.append(0x80 | 126); hdr += struct.pack("!H", n)
        else:
            hdr.append(0x80 | 127); hdr += struct.pack("!Q", n)
        hdr += mask
        self.sock.sendall(bytes(hdr) + bytes(b ^ mask[i % 4] for i, b in enumerate(payload)))

    def recv_frame(self):
        b0, b1 = self._read(2)
        opcode = b0 & 0x0F
        length = b1 & 0x7F
        if length == 126:
            length = struct.unpack("!H", self._read(2))[0]
        elif length == 127:
            length = struct.unpack("!Q", self._read(8))[0]
        mask = self._read(4) if (b1 & 0x80) else b""
        payload = self._read(length) if length else b""
        if mask:
            payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
        return opcode, payload

    def recv_json(self, timeout=None):
        if timeout is not None:
            self.sock.settimeout(timeout)
        op, payload = self.recv_frame()
        if op == OP_CLOSE:
            raise ConnectionError("对端关闭")
        return json.loads(payload.decode("utf-8"))

    def close(self):
        try: self.sock.close()
        except OSError: pass


def http_get(path):
    with urllib.request.urlopen("http://%s:%d%s" % (HOST, PORT, path), timeout=5) as r:
        return r.read(), r.headers.get("Content-Type", "")


def main():
    for s in (sys.stdout, sys.stderr):
        try: s.reconfigure(encoding="utf-8", errors="replace")
        except Exception: pass

    args = sys.argv[1:]
    if not args:
        print(__doc__); return 2

    if args[0] == "--status":
        body, _ = http_get("/status")
        print(json.dumps(json.loads(body.decode()), ensure_ascii=False, indent=2))
        return 0

    if args[0] == "--shot":
        out = args[1] if len(args) > 1 else "shot.png"
        path = "/shot" + ("?screen=1" if "--screen" in args else "")
        body, ctype = http_get(path)
        with open(out, "wb") as f:
            f.write(body)
        print("saved %s (%d bytes, %s)" % (out, len(body), ctype))
        return 0

    if args[0] == "--send":
        text = args[1] if len(args) > 1 else "probe"
        ws = Ws()
        try:
            # 桥在连上时会主动推 hello 和 status
            for _ in range(2):
                m = ws.recv_json(timeout=3)
                print("  <- %s" % json.dumps(m, ensure_ascii=False))

            print("  -> send: %r" % text)
            ws.send(json.dumps({"type": "send", "text": text}))

            deadline = time.time() + 10
            while time.time() < deadline:
                try:
                    m = ws.recv_json(timeout=5)
                except (socket.timeout, TimeoutError):
                    print("  (超时，没有更多回应)"); break
                print("  <- %s" % json.dumps(m, ensure_ascii=False))
                if m.get("type") == "sent":
                    break
        finally:
            ws.close()
        return 0

    if args[0] == "--watch":
        # 验证接收方向：先连上，再从外部改剪贴板，看桥有没有广播过来
        import subprocess
        text = args[1] if len(args) > 1 else "WATCH-TEST-1 来自剪贴板"
        ws = Ws()
        try:
            for _ in range(2):
                m = ws.recv_json(timeout=3)
                print("  <- %s" % json.dumps(m, ensure_ascii=False))

            print("  设置剪贴板 -> %r" % text)
            subprocess.run(
                ["powershell", "-NoProfile", "-Command",
                 "Set-Clipboard -Value " + json.dumps(text, ensure_ascii=False)],
                check=False, capture_output=True)

            print("  等待桥广播 clipboard 事件...")
            deadline = time.time() + 10
            got = False
            while time.time() < deadline:
                try:
                    m = ws.recv_json(timeout=5)
                except (socket.timeout, TimeoutError):
                    break
                print("  <- %s" % json.dumps(m, ensure_ascii=False))
                if m.get("type") == "clipboard":
                    if m.get("text") == text:
                        print("  [PASS] 剪贴板事件内容一致")
                    else:
                        print("  [FAIL] 内容不一致")
                    got = True
                    break
            if not got:
                print("  [FAIL] 没收到 clipboard 事件")
            return 0 if got else 1
        finally:
            ws.close()

    print(__doc__)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
