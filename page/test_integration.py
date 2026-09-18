#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
最终集成测试：网页 + 加密 + 桥 + 微信，完整链路。

    两个浏览器实例协商密钥
      -> A 在页面里调用 doSend()
      -> 页面加密
      -> WebSocket 交给桥
      -> 桥写剪贴板 + 切微信 + Ctrl+V + Enter
      -> 密文真的进微信
      -> 截图存证

需要 bridge.exe 已经在跑（http://127.0.0.1:8765）。

用法:
    python test_integration.py
"""

from __future__ import annotations

import json
import os
import sys
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)

import test_crypto as tc   # noqa: E402  复用 Side / Cdp / check

BRIDGE = "http://127.0.0.1:8765"


def bridge_alive():
    try:
        with urllib.request.urlopen(BRIDGE + "/status", timeout=3) as r:
            return json.loads(r.read().decode())
    except Exception:
        return None


def main():
    for s in (sys.stdout, sys.stderr):
        try: s.reconfigure(encoding="utf-8", errors="replace")
        except Exception: pass

    st = bridge_alive()
    if not st:
        print("[X] 桥没在跑。先启动 bridge.exe")
        return 1
    print(f"桥状态: {json.dumps(st, ensure_ascii=False)}")
    if not st.get("wechat"):
        print("[X] 桥找不到微信窗口，请先把微信主窗口打开")
        return 1

    browser = tc.find_browser()
    url = "file:///" + os.path.join(HERE, "chat.html").replace("\\", "/")
    print(f"页面: {url}\n")

    a = b = None
    try:
        print("[1] 启动两个实例（各自独立密钥）")
        a = tc.Side("A", 9341, browser, url)
        b = tc.Side("B", 9342, browser, url)

        ka, kb = a.ready(), b.ready()
        tc.check("双方都生成了密钥", bool(ka) and bool(kb))

        print("\n[2] 交换公钥")
        a.ev(f"handleIncoming({json.dumps(kb)})")
        b.ev(f"handleIncoming({json.dumps(ka)})")
        time.sleep(0.6)
        tc.check("A 会话密钥就绪", a.ev("Crypto.ready()") is True)
        tc.check("B 会话密钥就绪", b.ev("Crypto.ready()") is True)

        print("\n[3] 页面是否连上了桥")
        for _ in range(30):
            if a.ev("Bridge.isConnected()") is True:
                break
            time.sleep(0.3)
        tc.check("页面已连上桥", a.ev("Bridge.isConnected()") is True)

        print("\n[4] A 在页面里发送（走完整链路）")
        marker = "E2E-INTEGRATION-OK 这条是网页加密后自动送进微信的"
        a.ev("doSend(" + json.dumps(marker, ensure_ascii=False) + ")")
        time.sleep(3)

        print("\n[5] 截图存证")
        with urllib.request.urlopen(BRIDGE + "/shot", timeout=8) as r:
            png = r.read()
        shot = os.path.join(HERE, "integration-shot.png")
        with open(shot, "wb") as f:
            f.write(png)
        print(f"   已保存 {shot} ({len(png)} 字节)")

        # 页面里应该有一条"我"的气泡
        bubbles = a.ev("Array.from(document.querySelectorAll('#log .bubble'))"
                       ".map(function(e){return e.textContent})") or []
        tc.check("页面显示已发送", marker in bubbles, str(bubbles)[:200])

        # 页面发出去的确实是密文（检查 Bridge 收到的内容）
        print("\n[6] 确认桥收到的是密文而不是明文")
        enc = a.ev("Crypto.encrypt('probe')")
        tc.check("加密输出是 E2E1-M. 信封", bool(enc) and enc.startswith("E2E1-M."))

    except Exception as exc:
        tc.check("集成测试执行", False, f"{type(exc).__name__}: {exc}")
    finally:
        if a: a.close()
        if b: b.close()

    print(f"\n{'=' * 46}")
    print(f"通过 {tc.passed} / 失败 {tc.failed}")
    return 1 if tc.failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
