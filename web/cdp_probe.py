#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
端到端验证：用 CDP (Chrome DevTools Protocol) 真的驱动一遍 im.html。

跟 selftest.py 的区别：
  selftest.py  -> 只验证 bridge.py 的服务端协议
  cdp_probe.py -> 真的开一个浏览器、真的加载 im.html、真的在输入框打字点发送，
                  再从 DOM 里把气泡读回来。验证的是**页面 JS 有没有 bug**。

零依赖（stdlib + 手写 WebSocket 客户端，因为 CDP 本身跑在 WebSocket 上）。

用法:
    python cdp_probe.py <url> [--port 9222]
    python cdp_probe.py file:///C:/path/to/im.html
    python cdp_probe.py http://127.0.0.1:8765/
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import shutil
import socket
import struct
import subprocess
import sys
import tempfile
import time
import urllib.request

EDGE_CANDIDATES = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
    r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
]

WS_MAGIC = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"
OP_TEXT, OP_CLOSE = 0x1, 0x8

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
# 最小 WebSocket 客户端（CDP 与 bridge 都用它）
# ---------------------------------------------------------------------------

class Ws:
    def __init__(self, url: str, timeout: float = 10.0):
        assert url.startswith("ws://"), url
        rest = url[len("ws://"):]
        hostport, _, path = rest.partition("/")
        host, _, port = hostport.partition(":")
        port = int(port or 80)

        self.sock = socket.create_connection((host, port), timeout=timeout)
        self.buf = bytearray()

        key = base64.b64encode(os.urandom(16)).decode()
        self.sock.sendall((
            f"GET /{path} HTTP/1.1\r\nHost: {hostport}\r\n"
            f"Upgrade: websocket\r\nConnection: Upgrade\r\n"
            f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n"
        ).encode())

        while b"\r\n\r\n" not in self.buf:
            chunk = self.sock.recv(4096)
            if not chunk:
                raise ConnectionError("CDP 握手失败")
            self.buf += chunk
        _, _, rest_bytes = bytes(self.buf).partition(b"\r\n\r\n")
        self.buf = bytearray(rest_bytes)

    def _read(self, n: int) -> bytes:
        while len(self.buf) < n:
            chunk = self.sock.recv(max(4096, n - len(self.buf)))
            if not chunk:
                raise ConnectionError("CDP 连接关闭")
            self.buf += chunk
        out = bytes(self.buf[:n])
        del self.buf[:n]
        return out

    def send(self, text: str) -> None:
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

    def recv(self) -> str:
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
        if opcode == OP_CLOSE:
            raise ConnectionError("CDP 要求关闭")
        return payload.decode("utf-8", "replace")

    def close(self) -> None:
        try:
            self.sock.close()
        except OSError:
            pass


# ---------------------------------------------------------------------------
# CDP 驱动
# ---------------------------------------------------------------------------

class Cdp:
    def __init__(self, ws_url: str):
        self.ws = Ws(ws_url)
        self.n = 0

    def call(self, method: str, **params):
        self.n += 1
        mid = self.n
        self.ws.send(json.dumps({"id": mid, "method": method, "params": params}))
        deadline = time.time() + 15
        while time.time() < deadline:
            msg = json.loads(self.ws.recv())
            if msg.get("id") == mid:
                if "error" in msg:
                    raise RuntimeError(f"{method}: {msg['error']}")
                return msg.get("result", {})
        raise TimeoutError(method)

    def eval(self, expr: str):
        r = self.call("Runtime.evaluate",
                      expression=expr, returnByValue=True, awaitPromise=True)
        return r.get("result", {}).get("value")

    def close(self):
        self.ws.close()


def find_browser() -> str:
    for p in EDGE_CANDIDATES:
        if os.path.isfile(p):
            return p
    raise SystemExit("找不到 Edge/Chrome")


def wait_for_target(port: int, want: str = "", timeout: float = 20.0) -> str:
    """
    等 CDP 的 page target。

    注意：浏览器启动瞬间会有个 about:blank 的空标签页，如果直接取第一个 page
    target，就会抓到它，导致读到的 location 是 about:blank。所以这里必须
    按 URL 匹配，匹配不上时至少也要躲开 about:blank。
    """
    url = f"http://127.0.0.1:{port}/json/list"
    deadline = time.time() + timeout
    started = time.time()
    fallback = ""
    last = ""

    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=2) as r:
                targets = json.loads(r.read().decode())
            pages = [t for t in targets
                     if t.get("type") == "page" and t.get("webSocketDebuggerUrl")]
            for t in pages:
                t_url = t.get("url", "")
                if t_url in ("", "about:blank"):
                    continue
                # 前缀互相包含即可，容忍浏览器对 URL 的归一化
                if want and (want[:60] in t_url or t_url[:60] in want):
                    return t["webSocketDebuggerUrl"]
                fallback = fallback or t["webSocketDebuggerUrl"]
            # 兜底：等够 5 秒还没精确匹配，就用第一个非空白页
            if fallback and time.time() - started > 5:
                return fallback
        except Exception as exc:  # noqa: BLE001
            last = str(exc)
        time.sleep(0.3)

    if fallback:
        return fallback
    raise TimeoutError(f"等不到 CDP page target（{last}）")


def main() -> int:
    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
        except Exception:
            pass

    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    target_url = sys.argv[1]
    port = 9222
    if "--port" in sys.argv:
        port = int(sys.argv[sys.argv.index("--port") + 1])
    shot = None
    if "--shot" in sys.argv:
        shot = sys.argv[sys.argv.index("--shot") + 1]

    browser = find_browser()
    profile = tempfile.mkdtemp(prefix="cdp-probe-")
    print(f"浏览器: {os.path.basename(browser)}")
    print(f"目标  : {target_url}\n")

    proc = subprocess.Popen(
        [browser, "--headless=new", "--disable-gpu", "--no-first-run",
         "--no-default-browser-check", "--disable-extensions",
         "--disable-crash-reporter", "--disable-breakpad",
         f"--remote-debugging-port={port}", f"--user-data-dir={profile}",
         target_url],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )

    cdp = None
    try:
        ws_url = wait_for_target(port, target_url)
        cdp = Cdp(ws_url)
        cdp.call("Runtime.enable")

        # --- 1. 页面诊断面板 ---------------------------------------------
        print("[1] 页面环境")
        # 等到真的落在目标页面上再读数，别读到 about:blank
        for _ in range(40):
            proto = cdp.eval("location.protocol")
            if proto and proto != "about:":
                break
            time.sleep(0.25)
        origin = cdp.eval("location.origin")
        secure = cdp.eval("window.isSecureContext")
        print(f"  protocol={proto}  origin={origin}  isSecureContext={secure}")
        want_scheme = "file:" if target_url.startswith("file:") else "http:"
        check(f"落在目标页面 ({want_scheme})", proto == want_scheme, f"实际 {proto}")

        # --- 2. 会话列表 / 选择 ------------------------------------------
        print("\n[2] 会话列表 / 选择")
        convos = None
        for _ in range(40):
            convos = cdp.eval(
                "Array.from(document.querySelectorAll('#convoList .convo .nm'))"
                ".map(function(e){return e.textContent})"
            )
            if convos:
                break
            time.sleep(0.25)

        print(f"  会话列表: {convos}")
        check("会话列表已渲染", bool(convos), str(convos))
        check("包含「文件传输助手」", "文件传输助手" in (convos or []), str(convos))
        check("「文件传输助手」排第一", (convos or [""])[0] == "文件传输助手", str(convos))

        active = cdp.eval("document.getElementById('ctName').textContent")
        check("默认自动选中第一个会话", active == "文件传输助手", str(active))

        tag = cdp.eval("document.getElementById('backendTag').textContent")
        check("顶栏显示后端名", "mock" in (tag or ""), str(tag))

        # 显式点一下「文件传输助手」，验证选择交互
        clicked = cdp.eval(
            "(function(){"
            " var rows=document.querySelectorAll('#convoList .convo');"
            " for (var i=0;i<rows.length;i++){"
            "   if (rows[i].querySelector('.nm').textContent==='文件传输助手'){"
            "     rows[i].click(); return true; } }"
            " return false;"
            "})()"
        )
        time.sleep(0.4)
        check("能点击选中会话", clicked is True, str(clicked))
        check("标题切到「文件传输助手」",
              cdp.eval("document.getElementById('ctName').textContent") == "文件传输助手")
        check("目标 id 显示为 filehelper",
              cdp.eval("document.getElementById('ctId').textContent") == "filehelper")
        check("选中后输入框可用",
              cdp.eval("document.getElementById('input').disabled") is False)

        # 切到别的会话再切回来，验证列表切换
        cdp.eval(
            "(function(){"
            " var rows=document.querySelectorAll('#convoList .convo');"
            " for (var i=0;i<rows.length;i++){"
            "   var n=rows[i].querySelector('.nm').textContent;"
            "   if (n!=='文件传输助手'){ rows[i].click(); return n; } }"
            " return null;"
            "})()"
        )
        time.sleep(0.4)
        other = cdp.eval("document.getElementById('ctName').textContent")
        check("能切换到其它会话", other != "文件传输助手" and other != "未选择会话", str(other))

        # 切回文件传输助手，后面的收发测试都在它上面做
        cdp.eval(
            "(function(){"
            " var rows=document.querySelectorAll('#convoList .convo');"
            " for (var i=0;i<rows.length;i++){"
            "   if (rows[i].querySelector('.nm').textContent==='文件传输助手'){"
            "     rows[i].click(); return true; } }"
            " return false;"
            "})()"
        )
        time.sleep(0.4)

        # --- 3. 真的打字 + 点发送（目标是文件传输助手）--------------------
        print("\n[3] 端到端收发（目标是 文件传输助手）")
        text = "CDP 自动测试：你好 🌍"
        cdp.eval(
            "(function(){"
            "  var t=document.getElementById('input');"
            f" t.value={json.dumps(text, ensure_ascii=False)};"
            "  document.getElementById('send').click();"
            "  return true;"
            "})()"
        )
        time.sleep(1.5)

        bubbles = cdp.eval(
            "Array.from(document.querySelectorAll('#log .bubble'))"
            ".map(function(e){return e.textContent})"
        ) or []

        check("出现 2 条气泡（发出 + 收到）", len(bubbles) == 2, f"实际 {len(bubbles)}: {bubbles}")
        check("发出的气泡内容正确",
              len(bubbles) >= 1 and text in bubbles[0], str(bubbles[:1]))
        check("收到的回显内容正确",
              len(bubbles) >= 2 and text in bubbles[1], str(bubbles[1:2]))

        # --- 4. 输入框已清空 ---------------------------------------------
        left = cdp.eval("document.getElementById('input').value")
        check("发送后输入框已清空", left == "", repr(left))

        # --- 5. 长消息（>125 字节，走扩展长度分支）------------------------
        print("\n[4] 长消息 / 边界")
        long_text = "长" * 300
        cdp.eval(
            "(function(){"
            "  var t=document.getElementById('input');"
            f" t.value={json.dumps(long_text, ensure_ascii=False)};"
            "  document.getElementById('send').click();"
            "  return true;"
            "})()"
        )
        time.sleep(1.5)
        bubbles2 = cdp.eval(
            "Array.from(document.querySelectorAll('#log .bubble'))"
            ".map(function(e){return e.textContent})"
        ) or []
        check("长消息往返完整",
              len(bubbles2) >= 4 and bubbles2[3] == long_text,
              f"len={len(bubbles2)} last_len={len(bubbles2[-1]) if bubbles2 else 0}")

        # --- 5. 可选：截图，用来肉眼检查布局/CSS -------------------------
        if shot:
            print("\n[5] 截图")
            cdp.call("Page.enable")
            r = cdp.call("Page.captureScreenshot", format="png")
            with open(shot, "wb") as f:
                f.write(base64.b64decode(r["data"]))
            print(f"  已保存: {shot}")

    except Exception as exc:  # noqa: BLE001
        check("CDP 驱动", False, f"{type(exc).__name__}: {exc}")
    finally:
        if cdp:
            cdp.close()
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
        shutil.rmtree(profile, ignore_errors=True)

    print(f"\n{'=' * 40}")
    print(f"通过 {passed} / 失败 {failed}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
