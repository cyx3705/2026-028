#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
极简 IM 桥 —— 零依赖（只用 Python 标准库，不需要 pip install 任何东西）。

它同时干两件事：

  1. 用  http://127.0.0.1:8765/  托管单文件 im.html
     —— 页面与接口**同源**，所以没有 CORS 问题；
        又因为是 local -> loopback，也不会触发浏览器的 Local Network Access 拦截。

  2. 提供  ws://127.0.0.1:8765/ws  双向通道
     —— 收发都走这一条长连接。

当前后端是 Mock：**你发什么，它就原样回给你一条「收到」。**
后续把 MockBackend 换成 WeChatFerry / Gewechat 的调用即可，前端一行都不用改。

用法：
    python bridge.py            # 启动并自动打开浏览器
    python bridge.py --no-open  # 只启动，不开浏览器
"""

from __future__ import annotations

import argparse
import base64
import hashlib
import http.server
import json
import os
import socket
import struct
import sys
import threading
import time
import uuid
import webbrowser
from urllib.parse import parse_qs, urlparse

# ---------------------------------------------------------------------------
# 配置
# ---------------------------------------------------------------------------

HOST = "127.0.0.1"          # 只监听本机，不暴露到局域网
PORT = 8765
ROOT = os.path.dirname(os.path.abspath(__file__))
URL = f"http://{HOST}:{PORT}/"

WS_MAGIC = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"

# WebSocket opcode
OP_CONT, OP_TEXT, OP_BIN = 0x0, 0x1, 0x2
OP_CLOSE, OP_PING, OP_PONG = 0x8, 0x9, 0xA


def _fix_console() -> None:
    """Windows 控制台默认不是 UTF-8，中文会炸。"""
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
        except Exception:
            pass


def log(msg: str) -> None:
    print(f"[{time.strftime('%H:%M:%S')}] {msg}", flush=True)


# ---------------------------------------------------------------------------
# WebSocket 帧编解码（服务端：收到的帧必有 mask，发出的帧必不带 mask）
# ---------------------------------------------------------------------------

class WsClosed(Exception):
    """对端正常关闭。"""


def _recv_exact(conn: socket.socket, n: int) -> bytes:
    buf = bytearray()
    while len(buf) < n:
        chunk = conn.recv(n - len(buf))
        if not chunk:
            raise WsClosed("连接已断开")
        buf += chunk
    return bytes(buf)


def ws_recv(conn: socket.socket) -> tuple[int, bytes]:
    """读一个帧，返回 (opcode, payload)。不支持分片续帧（够用）。"""
    b0, b1 = _recv_exact(conn, 2)
    opcode = b0 & 0x0F
    masked = bool(b1 & 0x80)
    length = b1 & 0x7F

    if length == 126:
        length = struct.unpack("!H", _recv_exact(conn, 2))[0]
    elif length == 127:
        length = struct.unpack("!Q", _recv_exact(conn, 8))[0]

    mask = _recv_exact(conn, 4) if masked else b""
    payload = _recv_exact(conn, length) if length else b""

    if masked:
        payload = bytes(b ^ mask[i % 4] for i, b in enumerate(payload))

    return opcode, payload


def ws_send(conn: socket.socket, payload: bytes | str, opcode: int = OP_TEXT) -> None:
    """发一个帧（服务端不掩码）。"""
    if isinstance(payload, str):
        payload = payload.encode("utf-8")

    n = len(payload)
    header = bytearray([0x80 | opcode])          # FIN=1
    if n < 126:
        header.append(n)
    elif n < 65536:
        header.append(126)
        header += struct.pack("!H", n)
    else:
        header.append(127)
        header += struct.pack("!Q", n)

    conn.sendall(bytes(header) + payload)


def ws_send_json(conn: socket.socket, obj: dict) -> None:
    ws_send(conn, json.dumps(obj, ensure_ascii=False))


# ---------------------------------------------------------------------------
# 后端：现在只有 Mock
# ---------------------------------------------------------------------------

# 「文件传输助手」是微信内置的特殊会话，wxid 固定就是 filehelper。
# 发给它 = 发给自己，不打扰任何人，是接后端后最理想的第一个测试目标。
FILEHELPER = {
    "id": "filehelper",
    "name": "文件传输助手",
    "kind": "contact",
    "note": "微信内置 · 发给自己",
    "builtin": True,
}


class MockBackend:
    """
    假后端。不碰微信，纯内存。

    行为：客户端发什么，就回显一条「来自对端」的消息。
    这样才能验证「发得出去、收得回来」这整条链路。
    """

    name = "mock"

    def list_conversations(self) -> list[dict]:
        """
        返回可选的会话列表。

        真实后端接进来后，这里换成「拉取通讯录 + 群列表」即可，
        前端一行都不用改。
        """
        return [
            dict(FILEHELPER),
            {"id": "wxid_alice", "name": "Alice", "kind": "contact"},
            {"id": "wxid_bob", "name": "Bob", "kind": "contact"},
            {"id": "12345678@chatroom", "name": "测试群", "kind": "group"},
        ]

    def handle_send(self, target: str, text: str) -> list[dict]:
        """返回要推给客户端的消息列表。"""
        now = time.time()
        return [
            {   # 1) 回执：你这条发出去了
                "type": "sent",
                "id": uuid.uuid4().hex[:12],
                "to": target,
                "text": text,
                "ts": now,
            },
            {   # 2) 模拟对端回了一条
                "type": "message",
                "id": uuid.uuid4().hex[:12],
                "from": target,
                "fromName": target,
                "text": text,
                "ts": now + 0.05,
                "echo": True,
            },
        ]


BACKEND = MockBackend()


# ---------------------------------------------------------------------------
# HTTP + WebSocket 处理器
# ---------------------------------------------------------------------------

class Handler(http.server.BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"
    server_version = "MiniIMBridge/1.0"

    # 关掉默认的逐请求日志，太吵
    def log_message(self, fmt: str, *args) -> None:  # noqa: A003
        pass

    def handle_one_request(self) -> None:
        """
        浏览器强关连接（比如刷新、或探针 kill 掉进程）时，Python 默认会把
        ConnectionResetError 的完整 traceback 打到 stderr，刷屏且没意义。
        这里吞掉它。
        """
        try:
            super().handle_one_request()
        except (ConnectionResetError, ConnectionAbortedError, BrokenPipeError):
            self.close_connection = True

    # -- 路由 ---------------------------------------------------------------

    def do_GET(self) -> None:  # noqa: N802
        path = self.path.split("?", 1)[0]

        if path == "/ws":
            self._handle_ws()
            return

        if path == "/health":
            self._send_json({"ok": True, "backend": BACKEND.name})
            return

        if path == "/report":
            self._handle_report()
            return

        self._serve_static("/" if path == "/" else path)

    def do_OPTIONS(self) -> None:  # noqa: N802
        # 以防有人真的用 file:// 打开页面再 fetch 过来
        self.send_response(204)
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Access-Control-Allow-Headers", "*")
        self.send_header("Access-Control-Allow-Methods", "GET, POST, OPTIONS")
        self.send_header("Content-Length", "0")
        self.end_headers()

    # -- 静态文件 -----------------------------------------------------------

    def _serve_static(self, path: str) -> None:
        name = "im.html" if path == "/" else os.path.basename(path)
        full = os.path.join(ROOT, name)

        if not os.path.isfile(full):
            self.send_error(404, "Not Found")
            return

        with open(full, "rb") as f:
            data = f.read()

        ctype = "text/html; charset=utf-8" if name.endswith(".html") else "application/octet-stream"

        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Access-Control-Allow-Origin", "*")   # 给 file:// 场景留条后路
        self.send_header("Cache-Control", "no-store")          # 改完刷新就生效，别缓存
        self.end_headers()
        self.wfile.write(data)

    def _handle_report(self) -> None:
        """
        页面把自身诊断结果回传到桥的日志里（用 1x1 图片信标，不受 CORS 限制）。

        这样即使页面是 file:// 打开的、WebSocket 连不上，我们也能在服务端
        看到「它到底卡在哪一步」——spike 阶段就靠这个拿证据。
        """
        q = parse_qs(urlparse(self.path).query)

        def g(k: str) -> str:
            return (q.get(k) or ["-"])[0]

        # 把整条 query 都打出来，诊断页面"点了没反应"时全靠这个
        parts = "  ".join(f"{k}={v[0]}" for k, v in q.items() if k != "r")
        log(f"页面探针  {parts}")

        pixel = base64.b64decode("R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw==")
        self.send_response(200)
        self.send_header("Content-Type", "image/gif")
        self.send_header("Content-Length", str(len(pixel)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(pixel)

    def _send_json(self, obj: dict) -> None:
        data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(data)

    # -- WebSocket ----------------------------------------------------------

    def _handle_ws(self) -> None:
        key = self.headers.get("Sec-WebSocket-Key")
        if not key:
            self.send_error(400, "Missing Sec-WebSocket-Key")
            return

        accept = base64.b64encode(
            hashlib.sha1((key + WS_MAGIC).encode("ascii")).digest()
        ).decode("ascii")

        self.send_response(101, "Switching Protocols")
        self.send_header("Upgrade", "websocket")
        self.send_header("Connection", "Upgrade")
        self.send_header("Sec-WebSocket-Accept", accept)
        self.end_headers()
        self.wfile.flush()

        # 从这里开始，这条连接不再说 HTTP，我们直接接管 socket
        self.close_connection = True
        conn = self.connection
        peer = self.client_address[0]
        log(f"WS 已连接  <- {peer}")

        try:
            ws_send_json(conn, {
                "type": "hello",
                "backend": BACKEND.name,
                "serverTime": time.time(),
            })
            # 连上就把会话列表推过去，前端好渲染左侧列表
            ws_send_json(conn, {
                "type": "convos",
                "list": BACKEND.list_conversations(),
            })

            while True:
                opcode, payload = ws_recv(conn)

                if opcode == OP_CLOSE:
                    log(f"WS 收到 close <- {peer}")
                    break
                if opcode == OP_PING:
                    ws_send(conn, payload, OP_PONG)
                    continue
                if opcode == OP_PONG:
                    continue
                if opcode != OP_TEXT:
                    continue

                try:
                    msg = json.loads(payload.decode("utf-8"))
                except (UnicodeDecodeError, json.JSONDecodeError) as exc:
                    log(f"WS 收到畸形数据: {exc}")
                    continue

                if msg.get("type") == "send":
                    text = str(msg.get("text", ""))
                    target = str(msg.get("to") or "mock-peer")
                    if not text.strip():
                        continue
                    log(f"发送 -> {target}: {text[:40]}")
                    for out in BACKEND.handle_send(target, text):
                        ws_send_json(conn, out)
                elif msg.get("type") == "convos":
                    # 前端主动要求刷新会话列表
                    ws_send_json(conn, {
                        "type": "convos",
                        "list": BACKEND.list_conversations(),
                    })

        except (WsClosed, ConnectionError, OSError) as exc:
            log(f"WS 断开   <- {peer}  ({exc})")
        except Exception as exc:  # noqa: BLE001
            log(f"WS 异常   <- {peer}  ({type(exc).__name__}: {exc})")
        finally:
            try:
                conn.close()
            except OSError:
                pass


class ThreadingServer(http.server.ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True


# ---------------------------------------------------------------------------
# 入口
# ---------------------------------------------------------------------------

def main() -> int:
    _fix_console()

    parser = argparse.ArgumentParser(description="极简 IM 桥")
    parser.add_argument("--no-open", action="store_true", help="不要自动打开浏览器")
    parser.add_argument("--port", type=int, default=PORT)
    args = parser.parse_args()

    port = args.port
    url = f"http://{HOST}:{port}/"

    httpd = ThreadingServer((HOST, port), Handler)

    log(f"桥已启动   {url}")
    log(f"WebSocket  ws://{HOST}:{port}/ws")
    log(f"后端       {BACKEND.name}（回显，不连微信）")
    log("按 Ctrl+C 退出")

    if not args.no_open:
        threading.Timer(0.8, lambda: webbrowser.open(url)).start()

    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        log("收到 Ctrl+C，正在退出")
    finally:
        httpd.server_close()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
