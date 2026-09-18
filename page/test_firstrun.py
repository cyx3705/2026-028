#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
首次运行测试：**从零程序状态开始**，走完整安装流程。

    1. 桥没装、没跑 —— 页面应该显示「桥 未安装」
    2. 点「密钥」药丸 -> 无桥时应把公钥复制到剪贴板
    3. 点「桥」药丸 -> 弹出配置面板
    4. 页面生成 install-bridge.bat
    5. 运行它 -> 编译并启动 bridge.exe
    6. 页面应该**自动重连**，药丸变绿
    7. 截图存证

用法:
    python test_firstrun.py
"""

from __future__ import annotations

import base64
import json
import os
import subprocess
import sys
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import test_crypto as tc   # noqa: E402

PORT = 9351


def port_free():
    try:
        with urllib.request.urlopen("http://127.0.0.1:8765/status", timeout=2):
            return False
    except Exception:
        return True


def main():
    for s in (sys.stdout, sys.stderr):
        try: s.reconfigure(encoding="utf-8", errors="replace")
        except Exception: pass

    if not port_free():
        print("[X] 8765 上还有东西在跑。请先关掉桥，本测试要从零开始。")
        return 1
    print("起始状态: 桥未运行，8765 空闲\n")

    browser = tc.find_browser()
    url = "file:///" + os.path.join(HERE, "chat.html").replace("\\", "/")

    side = None
    bridge_proc = None
    tmp = os.path.join(HERE, "_firstrun")
    try:
        print("[1] 打开页面（此时没有任何桥）")
        side = tc.Side("first", PORT, browser, url)
        time.sleep(1.5)
        side.ready()

        pb = side.ev("document.getElementById('pillBridge').textContent")
        pk = side.ev("document.getElementById('pillKey').textContent")
        print(f"   桥药丸: {pb}")
        print(f"   密钥药丸: {pk}")
        tc.check("桥显示「未安装」", "未安装" in (pb or ""), str(pb))
        tc.check("密钥显示未就绪", "未就绪" in (pk or ""), str(pk))
        tc.check("输入框被禁用", side.ev("document.getElementById('input').disabled") is True)
        tc.check("桥连接状态为 false", side.ev("Bridge.isConnected()") is False)

        # 大横幅：不依赖弹窗，未装桥时必须可见且在视口内。
        # 这条是针对"弹窗被扔到屏幕外"那个坑加的独立防线。
        banner = side.ev("""(function(){
            var b = document.getElementById('banner');
            var r = b.getBoundingClientRect();
            return {shown:b.classList.contains('show'), top:Math.round(r.top),
                    h:Math.round(r.height), vh:window.innerHeight};
        })()""")
        print(f"   大横幅: {banner}")
        tc.check("未装桥时大横幅可见且在视口内",
                 bool(banner) and banner["shown"] and banner["h"] > 0
                 and banner["top"] >= 0 and banner["top"] < banner["vh"],
                 str(banner))
        tc.check("大横幅里有安装按钮",
                 side.ev("!!document.getElementById('btnInstallBig')") is True)
        tc.check("浏览器支持 showSaveFilePicker（可绕开下载拦截）",
                 side.ev("typeof window.showSaveFilePicker === 'function'") is True)

        print("\n[2] 点「密钥」药丸（无桥 -> 应该复制到剪贴板）")
        side.ev("document.getElementById('pillKey').click()")
        time.sleep(1.0)
        tc.check("密钥面板已弹出",
                 side.ev("document.getElementById('popKey').classList.contains('show')") is True)

        clip = subprocess.run(
            ["powershell", "-NoProfile", "-Command", "Get-Clipboard -Raw"],
            capture_output=True, text=True)
        got = (clip.stdout or "").strip()
        print(f"   剪贴板: {got[:56]}…")
        tc.check("剪贴板里是我的公钥（E2E1-K. 开头）", got.startswith("E2E1-K."), got[:40])

        print("\n[3] 点「桥」药丸 -> 展开配置面板")
        side.ev("document.getElementById('pillBridge').click()")
        time.sleep(0.6)
        tc.check("桥面板已弹出",
                 side.ev("document.getElementById('popBridge').classList.contains('show')") is True)
        tc.check("面板里有下载按钮",
                 side.ev("!!document.getElementById('btnDownload')") is True)

        # 关键：光有 class 和尺寸不够，必须确认它**真的在视口里**。
        # 踩过：.pop 曾是 header 的兄弟，top:calc(100%+6px) 参照整个视口，
        # 弹窗被扔到 100vh+6px 的屏幕外 —— 点了完全没反应，
        # 而只检查 classList / width 的测试全都通过。
        rect = side.ev("""(function(){
            var r = document.getElementById('popBridge').getBoundingClientRect();
            return {top:Math.round(r.top), left:Math.round(r.left),
                    w:Math.round(r.width), h:Math.round(r.height),
                    vw:window.innerWidth, vh:window.innerHeight};
        })()""")
        print(f"   面板 rect: {rect}")
        in_view = bool(rect) and rect["w"] > 0 and rect["h"] > 0 \
            and rect["top"] >= 0 and rect["top"] < rect["vh"] \
            and rect["left"] >= 0 and rect["left"] + rect["w"] <= rect["vw"] + 1
        tc.check("面板真的落在视口内（不是被扔到屏幕外）", in_view, str(rect))

        # 顺手截一张"弹窗开着"的图，肉眼可查
        side.cdp.call("Page.enable")
        shot0 = side.cdp.call("Page.captureScreenshot", format="png")
        with open(os.path.join(HERE, "popover-shot.png"), "wb") as f:
            f.write(base64.b64decode(shot0["data"]))

        print("\n[4] 页面生成安装包")
        bat = side.ev("buildInstaller()")
        tc.check("安装包内容非空", bool(bat) and len(bat) > 5000, str(len(bat)))
        tc.check("安装包是纯 ASCII", all(ord(c) < 128 for c in (bat or "")), "含非 ASCII")

        os.makedirs(tmp, exist_ok=True)
        bat_path = os.path.join(tmp, "install-bridge.bat")
        with open(bat_path, "w", encoding="ascii", newline="\r\n") as f:
            f.write(bat)
        print(f"   已写出 {bat_path} ({len(bat)} 字符)")

        print("\n[5] 运行安装包（模拟用户双击）")
        log = open(os.path.join(tmp, "log.txt"), "w", encoding="utf-8")
        bridge_proc = subprocess.Popen(["cmd", "/c", "install-bridge.bat"],
                                       cwd=tmp, stdout=log, stderr=subprocess.STDOUT)
        time.sleep(9)
        exe = os.path.join(tmp, "bridge.exe")
        tc.check("编译出了 bridge.exe", os.path.isfile(exe),
                 "没生成" if not os.path.isfile(exe) else "")
        if os.path.isfile(exe):
            print(f"   bridge.exe 大小: {os.path.getsize(exe)} 字节")

        print("\n[6] 页面应该自动重连（不需要刷新）")
        ok = False
        for _ in range(30):
            if side.ev("Bridge.isConnected()") is True:
                ok = True
                break
            time.sleep(0.5)
        tc.check("页面自动连上了新装好的桥", ok)

        pb2 = side.ev("document.getElementById('pillBridge').textContent")
        print(f"   桥药丸现在是: {pb2}")
        tc.check("桥药丸变绿", "已连接" in (pb2 or ""), str(pb2))

        print("\n[7] 截图存证")
        side.cdp.call("Page.enable")
        shot = side.cdp.call("Page.captureScreenshot", format="png")
        out = os.path.join(HERE, "firstrun-shot.png")
        with open(out, "wb") as f:
            f.write(base64.b64decode(shot["data"]))
        print(f"   已保存 {out}")

    except Exception as exc:
        tc.check("首次运行测试执行", False, f"{type(exc).__name__}: {exc}")
    finally:
        if side: side.close()
        if bridge_proc:
            bridge_proc.terminate()
            try: bridge_proc.wait(timeout=5)
            except subprocess.TimeoutExpired: bridge_proc.kill()
        # 安装包自己启动的 bridge.exe
        subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Get-Process bridge -ErrorAction SilentlyContinue | "
                        "Where-Object { $_.Path -like '*_firstrun*' } | Stop-Process -Force"],
                       capture_output=True)

    print(f"\n{'=' * 46}")
    print(f"通过 {tc.passed} / 失败 {tc.failed}")
    return 1 if tc.failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
