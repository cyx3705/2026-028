#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
bridge.py 的自检脚本 —— 零依赖，纯标准库。

不依赖浏览器，确定性地验证：
  1. HTTP 侧：/health 正常、/ 能返回 im.html
  2. WebSocket 握手：Sec-WebSocket-Accept 计算正确
  3. 帧编解码：客户端发 masked 文本帧 -> 服务端能解 -> 服务端回 unmasked 帧 -> 客户端能解
  4. 业务回显：发一条 send，应该收到 sent + message 两条

需要 bridge.py 已经在跑：
    python bridge.py --no-open
    python selftest.py
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import socket
import struct
import sys
import urllib.request

HOST = "127.0.0.1"
PORT = int(os.environ.get("BRIDGE_PORT", "8765"))
WS_MAGIC = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

OP_TEXT, OP_CLOSE, OP_PING, OP_PONG = 0x1, 0x8, 0x9, 0xA

passed = 0
failed = 0


def check(name: str, ok: bool, detail: str = "") -> None:
    global passed, failed
    if ok:
        passed += 1
        print(f"  [PASS] {name}")
    else:
        failed += 1
        print(f"  [FAIL] {name}  {detail}")


# ---------------------------------------------------------------------------
# 最小 WebSocket 客户端（客户端发出的帧必须 masked）
# ---------------------------------------------------------------------------

class WsClient:
    def __init__(self, host: str, port: int, path: str = "/ws", timeout: float = 5.0):
        self.sock = socket.create_connection((host, port), timeout=timeout)
        self.buf = bytearray()

        key = base64.b64encode(os.urandom(16)).decode("ascii")
        req = (
            f"GET {path} HTTP/1.1\r\n"
            f"Host: {host}:{port}\r\n"
            f"Upgrade: websocket\r\n"
            f"Connection: Upgrade\r\n"
            f"Sec-WebSocket-Key: {key}\r\n"
            f"Sec-WebSocket-Version: 13\r\n"
            f"\r\n"
        )
        self.sock.sendall(req.encode("ascii"))

        # 读响应头
        while b"\r\n\r\n" not in self.buf:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise ConnectionError("握手期间连接被关闭")
            self.buf += chunk

        head, _, rest = bytes(self.buf).partition(b"\r\n\r\n")
        self.buf = bytearray(rest)

        lines = head.split(b"\r\n")
        self.status_line = lines[0].decode("latin-1")
        self.headers = {}
        for line in lines[1:]:
            if b":" in line:
                k, _, v = line.partition(b":")
                self.headers[k.strip().lower().decode("latin-1")] = v.strip().decode("latin-1")

        # 校验 accept
        expect = base64.b64encode(
            hashlib.sha1((key + WS_MAGIC).encode("ascii")).digest()
        ).decode("ascii")
        self.expected_accept = expect

    def _read(self, n: int) -> bytes:
        while len(self.buf) < n:
            chunk = self.sock.recv(max(4096, n - len(self.buf)))
            if not chunk:
                raise ConnectionError("连接已关闭")
            self.buf += chunk
        out = bytes(self.buf[:n])
        del self.buf[:n]
        return out

    def send_text(self, text: str) -> None:
        payload = text.encode("utf-8")
        mask = os.urandom(4)
        n = len(payload)
        header = bytearray([0x80 | OP_TEXT])
        if n < 126:
            header.append(0x80 | n)
        elif n < 65536:
            header.append(0x80 | 126)
            header += struct.pack("!H", n)
        else:
            header.append(0x80 | 127)
            header += struct.pack("!Q", n)
        header += mask
        masked = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
        self.sock.sendall(bytes(header) + masked)

    def recv_frame(self) -> tuple[int, bytes]:
        b0, b1 = self._read(2)
        opcode = b0 & 0x0F
        masked = bool(b1 & 0x80)
        length = b1 & 0x7F
        if length == 126:
            length = struct.unpack("!H", self._read(2))[0]
        elif length == 127:
            length = struct.unpack("!Q", self._read(8))[0]
        mask = self._read(4) if masked else b""
        payload = self._read(length) if length else b""
        if masked:
            payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))
        return opcode, payload

    def recv_json(self) -> dict:
        opcode, payload = self.recv_frame()
        if opcode != OP_TEXT:
            raise AssertionError(f"期望文本帧，实际 opcode={opcode}")
        return json.loads(payload.decode("utf-8"))

    def close(self) -> None:
        try:
            self.sock.close()
        except OSError:
            pass


# ---------------------------------------------------------------------------

def main() -> int:
    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
        except Exception:
            pass

    base = f"http://{HOST}:{PORT}"
    print(f"自检目标: {base}\n")

    # --- 1. HTTP 侧 -------------------------------------------------------
    print("[1] HTTP 侧")

    try:
        with urllib.request.urlopen(f"{base}/health", timeout=5) as r:
            body = r.read().decode("utf-8")
            health = json.loads(body)
        check("/health 返回 200 + JSON", health.get("ok") is True, body)
    except Exception as exc:  # noqa: BLE001
        check("/health 可访问", False, f"{type(exc).__name__}: {exc}")
        print("\n桥没在跑？先执行:  python bridge.py --no-open")
        return 1

    try:
        with urllib.request.urlopen(f"{base}/", timeout=5) as r:
            html = r.read().decode("utf-8")
            ctype = r.headers.get("Content-Type", "")
        check("/ 返回 HTML", "<!DOCTYPE html>" in html and "text/html" in ctype,
              f"ctype={ctype} len={len(html)}")
        check("CORS 头存在", "*" in (r.headers.get("Access-Control-Allow-Origin") or ""))
        check("禁用缓存", "no-store" in (r.headers.get("Cache-Control") or ""))
    except Exception as exc:  # noqa: BLE001
        check("/ 可访问", False, f"{type(exc).__name__}: {exc}")

    # --- 2. 握手 ----------------------------------------------------------
    print("\n[2] WebSocket 握手")

    ws = None
    try:
        ws = WsClient(HOST, PORT)
        check("状态行是 101", "101" in ws.status_line, ws.status_line)
        check("Upgrade: websocket",
              ws.headers.get("upgrade", "").lower() == "websocket",
              str(ws.headers.get("upgrade")))
        check("Sec-WebSocket-Accept 计算正确",
              ws.headers.get("sec-websocket-accept") == ws.expected_accept,
              f"got={ws.headers.get('sec-websocket-accept')} want={ws.expected_accept}")
    except Exception as exc:  # noqa: BLE001
        check("握手成功", False, f"{type(exc).__name__}: {exc}")
        if ws:
            ws.close()
        print(f"\n通过 {passed} / 失败 {failed}")
        return 1

    # --- 3. 帧与业务 ------------------------------------------------------
    print("\n[3] 帧编解码 + 业务回显")

    try:
        # 服务端主动推的 hello
        hello = ws.recv_json()
        check("收到 hello", hello.get("type") == "hello", str(hello))
        check("hello 带 backend 字段", "backend" in hello, str(hello))

        # 紧接着推会话列表
        convos = ws.recv_json()
        check("收到 convos 会话列表", convos.get("type") == "convos", str(convos))
        lst = convos.get("list") or []
        check("会话列表非空", len(lst) > 0, str(lst))

        helper = next((c for c in lst if c.get("id") == "filehelper"), None)
        check("列表含 filehelper", helper is not None, str(lst))
        check("filehelper 中文名正确",
              helper is not None and helper.get("name") == "文件传输助手", str(helper))
        check("filehelper 标记为内置",
              helper is not None and helper.get("builtin") is True, str(helper))
        check("filehelper 排第一",
              bool(lst) and lst[0].get("id") == "filehelper", str(lst and lst[0]))

        # 客户端主动要求刷新列表
        ws.send_text(json.dumps({"type": "convos"}))
        refreshed = ws.recv_json()
        check("能主动刷新会话列表",
              refreshed.get("type") == "convos" and len(refreshed.get("list") or []) == len(lst),
              str(refreshed)[:120])

        # 发中文（顺便验证 UTF-8 与多字节长度）
        text = "你好，世界 🌍"
        ws.send_text(json.dumps({"type": "send", "to": "selftest", "text": text},
                                ensure_ascii=False))

        sent = ws.recv_json()
        check("收到 sent 回执", sent.get("type") == "sent", str(sent))
        check("sent 内容往返一致", sent.get("text") == text, repr(sent.get("text")))
        check("sent.to 正确", sent.get("to") == "selftest", str(sent.get("to")))

        msg = ws.recv_json()
        check("收到 message 回显", msg.get("type") == "message", str(msg))
        check("message 内容往返一致", msg.get("text") == text, repr(msg.get("text")))
        check("message 标记 echo", msg.get("echo") is True, str(msg))

        # 发一条长消息，验证 126 分支的扩展长度编码
        long_text = "长" * 300          # UTF-8 下 900 字节 > 125
        ws.send_text(json.dumps({"type": "send", "to": "selftest", "text": long_text},
                                ensure_ascii=False))
        ws.recv_json()                  # sent
        long_echo = ws.recv_json()      # message
        check("长消息往返一致（>125 字节分支）", long_echo.get("text") == long_text,
              f"len={len(long_echo.get('text') or '')}")

        # 空消息应被忽略
        ws.send_text(json.dumps({"type": "send", "to": "selftest", "text": "   "}))
        ws.sock.settimeout(0.6)
        ignored = True
        try:
            ws.recv_frame()
            ignored = False
        except (socket.timeout, TimeoutError):
            pass
        check("空白消息被忽略", ignored)

    except Exception as exc:  # noqa: BLE001
        check("帧/业务测试", False, f"{type(exc).__name__}: {exc}")
    finally:
        if ws:
            ws.close()

    print(f"\n{'=' * 40}")
    print(f"通过 {passed} / 失败 {failed}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
