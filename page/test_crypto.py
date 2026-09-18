#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
端到端加密验证：开**两个独立的浏览器实例**（各自独立 profile / localStorage），
模拟对话双方，跑完整流程：

    A 生成密钥 -> A 的公钥给 B
    B 生成密钥 -> B 的公钥给 A
    A 加密一条 -> 密文给 B -> B 解密 -> 比对明文
    篡改密文 -> 必须解密失败
    重放旧密文 -> 必须被拒绝

零依赖（stdlib）。需要 Edge。

用法:
    python test_crypto.py
    python test_crypto.py --keep   # 失败时保留浏览器不关，方便肉眼看
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

HERE = os.path.dirname(os.path.abspath(__file__))
PAGE = os.path.join(HERE, "chat.html")

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


def check(name, ok, detail=""):
    global passed, failed
    if ok:
        passed += 1
        print(f"  [PASS] {name}")
    else:
        failed += 1
        print(f"  [FAIL] {name}  {detail}")


# ---------------------------------------------------------------------------
# 极简 WebSocket / CDP 客户端（CDP 跑在 WebSocket 上）
# ---------------------------------------------------------------------------

class Ws:
    def __init__(self, url, timeout=15.0):
        rest = url[len("ws://"):]
        hostport, _, path = rest.partition("/")
        host, _, port = hostport.partition(":")
        self.sock = socket.create_connection((host, int(port or 80)), timeout=timeout)
        self.buf = bytearray()
        key = base64.b64encode(os.urandom(16)).decode()
        self.sock.sendall((
            f"GET /{path} HTTP/1.1\r\nHost: {hostport}\r\n"
            f"Upgrade: websocket\r\nConnection: Upgrade\r\n"
            f"Sec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n"
        ).encode())
        while b"\r\n\r\n" not in self.buf:
            c = self.sock.recv(4096)
            if not c:
                raise ConnectionError("CDP 握手失败")
            self.buf += c
        _, _, rest2 = bytes(self.buf).partition(b"\r\n\r\n")
        self.buf = bytearray(rest2)

    def _read(self, n):
        while len(self.buf) < n:
            c = self.sock.recv(max(4096, n - len(self.buf)))
            if not c:
                raise ConnectionError("CDP 关闭")
            self.buf += c
        out = bytes(self.buf[:n]); del self.buf[:n]
        return out

    def send(self, text):
        p = text.encode(); mask = os.urandom(4); n = len(p)
        h = bytearray([0x80 | OP_TEXT])
        if n < 126: h.append(0x80 | n)
        elif n < 65536: h.append(0x80 | 126); h += struct.pack("!H", n)
        else: h.append(0x80 | 127); h += struct.pack("!Q", n)
        h += mask
        self.sock.sendall(bytes(h) + bytes(b ^ mask[i % 4] for i, b in enumerate(p)))

    def recv(self):
        b0, b1 = self._read(2)
        op = b0 & 0x0F
        ln = b1 & 0x7F
        if ln == 126: ln = struct.unpack("!H", self._read(2))[0]
        elif ln == 127: ln = struct.unpack("!Q", self._read(8))[0]
        mask = self._read(4) if (b1 & 0x80) else b""
        pay = self._read(ln) if ln else b""
        if mask: pay = bytes(b ^ mask[i % 4] for i, b in enumerate(pay))
        if op == OP_CLOSE: raise ConnectionError("CDP 要求关闭")
        return pay.decode("utf-8", "replace")

    def close(self):
        try: self.sock.close()
        except OSError: pass


class Cdp:
    def __init__(self, ws_url):
        self.ws = Ws(ws_url); self.n = 0

    def call(self, method, **params):
        self.n += 1; mid = self.n
        self.ws.send(json.dumps({"id": mid, "method": method, "params": params}))
        end = time.time() + 20
        while time.time() < end:
            m = json.loads(self.ws.recv())
            if m.get("id") == mid:
                if "error" in m: raise RuntimeError(f"{method}: {m['error']}")
                return m.get("result", {})
        raise TimeoutError(method)

    def ev(self, expr):
        r = self.call("Runtime.evaluate", expression=expr,
                      returnByValue=True, awaitPromise=True)
        if r.get("exceptionDetails"):
            raise RuntimeError("JS 异常: " + json.dumps(r["exceptionDetails"], ensure_ascii=False)[:300])
        return r.get("result", {}).get("value")

    def close(self):
        self.ws.close()


# ---------------------------------------------------------------------------

def find_browser():
    for p in EDGE_CANDIDATES:
        if os.path.isfile(p):
            return p
    raise SystemExit("找不到 Edge/Chrome")


def wait_target(port, want, timeout=25.0):
    end = time.time() + timeout
    fallback = ""
    while time.time() < end:
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/json/list", timeout=2) as r:
                targets = json.loads(r.read().decode())
            for t in targets:
                if t.get("type") != "page" or not t.get("webSocketDebuggerUrl"):
                    continue
                u = t.get("url", "")
                if u in ("", "about:blank"):
                    continue
                if want[:50] in u or u[:50] in want:
                    return t["webSocketDebuggerUrl"]
                fallback = fallback or t["webSocketDebuggerUrl"]
        except Exception:
            pass
        time.sleep(0.3)
    if fallback:
        return fallback
    raise TimeoutError("等不到 CDP target")


class Side:
    """一个"人"：独立浏览器实例 + 独立 localStorage。"""

    def __init__(self, name, port, browser, url):
        self.name = name
        self.profile = tempfile.mkdtemp(prefix=f"e2e-{name}-")
        self.proc = subprocess.Popen(
            [browser, "--headless=new", "--disable-gpu", "--no-first-run",
             "--no-default-browser-check", "--disable-extensions",
             "--disable-crash-reporter", "--disable-breakpad",
             f"--remote-debugging-port={port}", f"--user-data-dir={self.profile}",
             url],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        ws_url = wait_target(port, url)
        self.cdp = Cdp(ws_url)
        self.cdp.call("Runtime.enable")

    def ev(self, expr):
        return self.cdp.ev(expr)

    def ready(self):
        """等页面里 Crypto 初始化完。"""
        for _ in range(60):
            v = self.ev("(function(){ try { return Crypto.myKeyEnvelope(); } catch(e){ return null; } })()")
            if v:
                return v
            time.sleep(0.25)
        return None

    def close(self):
        try: self.cdp.close()
        except Exception: pass
        self.proc.terminate()
        try: self.proc.wait(timeout=5)
        except subprocess.TimeoutExpired: self.proc.kill()
        shutil.rmtree(self.profile, ignore_errors=True)


def main():
    for s in (sys.stdout, sys.stderr):
        try: s.reconfigure(encoding="utf-8", errors="replace")
        except Exception: pass

    if not os.path.isfile(PAGE):
        print(f"[X] 先跑 build_page.py 生成 {PAGE}")
        return 1

    browser = find_browser()
    url = "file:///" + PAGE.replace("\\", "/")
    print(f"页面: {url}\n")

    a = b = None
    keep = "--keep" in sys.argv
    try:
        print("[1] 启动两个独立浏览器实例")
        a = Side("A", 9331, browser, url)
        b = Side("B", 9332, browser, url)
        check("A 实例启动", True)
        check("B 实例启动", True)

        ka = a.ready(); kb = b.ready()
        check("A 生成了密钥", bool(ka and ka.startswith("E2E1-K.")), str(ka)[:40])
        check("B 生成了密钥", bool(kb and kb.startswith("E2E1-K.")), str(kb)[:40])
        check("双方公钥不同", ka != kb)
        print(f"   A 指纹: {a.ev('Crypto.myFingerprint()')}")
        print(f"   B 指纹: {b.ev('Crypto.myFingerprint()')}")

        print("\n[2] 交换公钥")
        a.ev(f"handleIncoming({json.dumps(kb)})")
        b.ev(f"handleIncoming({json.dumps(ka)})")
        time.sleep(0.5)
        check("A 协商出会话密钥", a.ev("Crypto.ready()") is True)
        check("B 协商出会话密钥", b.ev("Crypto.ready()") is True)

        print("\n[3] A 加密 -> B 解密")
        plain = "你好，这是端到端测试 🌍 中文+emoji"
        env = a.ev("Crypto.encrypt(" + json.dumps(plain) + ")")
        check("密文带正确前缀", bool(env and env.startswith("E2E1-M.")), str(env)[:40])
        check("密文里看不到明文", plain not in (env or ""))
        print(f"   密文长度: {len(env)} 字符")

        b.ev(f"handleIncoming({json.dumps(env)})")
        time.sleep(0.6)
        bubbles = b.ev("Array.from(document.querySelectorAll('#log .bubble')).map(function(e){return e.textContent})") or []
        check("B 解出明文", plain in bubbles, str(bubbles)[:200])

        print("\n[4] B 回一条 -> A 解密（反向密钥）")
        reply = "收到！反向也通了 ✅"
        env2 = b.ev("Crypto.encrypt(" + json.dumps(reply) + ")")
        a.ev(f"handleIncoming({json.dumps(env2)})")
        time.sleep(0.6)
        ab = a.ev("Array.from(document.querySelectorAll('#log .bubble')).map(function(e){return e.textContent})") or []
        check("A 解出回复", reply in ab, str(ab)[:200])

        print("\n[5] 篡改必须失败")
        # 把密文最后一字节改掉（auth tag 会被破坏）
        tampered = env[:-2] + ("AA" if not env.endswith("AA") else "BB")
        b.ev(f"handleIncoming({json.dumps(tampered)})")
        time.sleep(0.5)
        sysmsgs = b.ev("Array.from(document.querySelectorAll('#log .sys')).map(function(e){return e.textContent})") or []
        check("篡改的密文被拒绝", any("解密失败" in s for s in sysmsgs), str(sysmsgs)[-300:])

        print("\n[6] 重放必须失败")
        # env 是 A 发的第 1 条；让 B 再收一次同样的
        b.ev(f"handleIncoming({json.dumps(env)})")
        time.sleep(0.5)
        sysmsgs2 = b.ev("Array.from(document.querySelectorAll('#log .sys')).map(function(e){return e.textContent})") or []
        check("重放被拒绝", any(("重放" in s) or ("解密失败" in s) for s in sysmsgs2), str(sysmsgs2)[-300:])

        print("\n[7] 计数器/nonce 不重用")
        # 直接看密文前 12 字节的 iv 更实在
        e1 = a.ev('Crypto.encrypt("n1")')
        e2 = a.ev('Crypto.encrypt("n2")')
        e3 = a.ev('Crypto.encrypt("n3")')

        def iv_of(envelope):
            body = envelope[len("E2E1-M."):]
            raw = base64.urlsafe_b64decode(body + "=" * (-len(body) % 4))
            return raw[:12].hex()

        iv1, iv2, iv3 = iv_of(e1), iv_of(e2), iv_of(e3)
        print(f"   iv1={iv1}")
        print(f"   iv2={iv2}")
        print(f"   iv3={iv3}")
        check("连续消息的 nonce 互不相同", len({iv1, iv2, iv3}) == 3)
        check("nonce 单调递增", iv1 < iv2 < iv3)

        print("\n[8] 桥安装包生成")
        bat = a.ev("buildInstaller()")
        check("installer 非空", bool(bat) and len(bat) > 1000, str(len(bat)))
        check("installer 是 ASCII（.bat 中文会炸）", all(ord(c) < 128 for c in (bat or "")),
              "含有非 ASCII 字符")
        check("installer 含 csc.exe 路径", "csc.exe" in (bat or ""))
        check("installer 含 base64 分块", (bat or "").count("bridge.cs.b64 echo") > 5)
        with open(os.path.join(HERE, "_installer_test.bat"), "w", encoding="ascii", newline="\r\n") as f:
            f.write(bat)
        print(f"   已写出 _installer_test.bat ({len(bat)} 字符)")

    except Exception as exc:
        check("测试执行", False, f"{type(exc).__name__}: {exc}")
    finally:
        if not keep:
            if a: a.close()
            if b: b.close()

    print(f"\n{'=' * 46}")
    print(f"通过 {passed} / 失败 {failed}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
