#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
诊断「点桥药丸装不了桥」到底卡在哪一步。

用 CDP 监听下载事件 + 控制台错误，逐段验证：
    1. 药丸点击 -> 弹窗是否出现
    2. 下载按钮点击 -> 是否真的触发了下载
    3. 下载是否完成 / 被拦 / 被取消
    4. 控制台有没有报错

用法:
    python test_download.py
    python test_download.py --headed    # 有头模式（更接近用户实际）
"""

from __future__ import annotations

import base64
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import test_crypto as tc   # noqa: E402

PORT = 9361


class CdpEvents(tc.Cdp):
    """在 tc.Cdp 基础上收集事件（下载、控制台等）。"""

    def __init__(self, ws_url):
        tc.Cdp.__init__(self, ws_url)
        self.events = []
        self.console = []

    def pump(self, seconds):
        end = time.time() + seconds
        self.ws.sock.settimeout(0.4)
        while time.time() < end:
            try:
                raw = self.ws.recv()
            except Exception:
                continue
            try:
                m = json.loads(raw)
            except Exception:
                continue
            if "method" in m:
                self.events.append(m)
                if m["method"] == "Runtime.consoleAPICalled":
                    args = m.get("params", {}).get("args", [])
                    txt = " ".join(str(a.get("value", a.get("description", ""))) for a in args)
                    self.console.append(m["params"].get("type", "?") + ": " + txt)
                if m["method"] == "Log.entryAdded":
                    e = m.get("params", {}).get("entry", {})
                    self.console.append(e.get("level", "?") + ": " + str(e.get("text", "")))
        self.ws.sock.settimeout(15.0)


def main():
    for s in (sys.stdout, sys.stderr):
        try: s.reconfigure(encoding="utf-8", errors="replace")
        except Exception: pass

    headed = "--headed" in sys.argv
    browser = tc.find_browser()
    url = "file:///" + os.path.join(HERE, "chat.html").replace("\\", "/")
    dl = tempfile.mkdtemp(prefix="dl-test-")

    profile = tempfile.mkdtemp(prefix="dl-profile-")
    args = [browser, "--disable-gpu", "--no-first-run", "--no-default-browser-check",
            "--disable-extensions", "--disable-crash-reporter", "--disable-breakpad",
            f"--remote-debugging-port={PORT}", f"--user-data-dir={profile}"]
    if not headed:
        args.append("--headless=new")
    args.append(url)

    proc = subprocess.Popen(args, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    print(f"浏览器: {'有头' if headed else 'headless'}  Edge")
    print(f"下载目录: {dl}\n")

    cdp = None
    try:
        ws_url = tc.wait_target(PORT, url)
        cdp = CdpEvents(ws_url)
        cdp.call("Runtime.enable")
        cdp.call("Log.enable")
        cdp.call("Page.enable")

        # 允许下载并指定目录。
        # 有头模式**不设**，这样看到的才是用户真实遇到的默认行为。
        if headed:
            print("有头模式：不干预下载行为，观察浏览器默认表现")
            default_dl = os.path.join(os.path.expanduser("~"), "Downloads")
            print(f"默认下载目录: {default_dl}")
            before = set(os.listdir(default_dl)) if os.path.isdir(default_dl) else set()
        else:
            try:
                cdp.call("Browser.setDownloadBehavior",
                         behavior="allow", downloadPath=dl, eventsEnabled=True)
                print("Browser.setDownloadBehavior -> ok (headless)")
            except Exception as e:
                print(f"Browser.setDownloadBehavior 失败: {e}")

        for _ in range(40):
            if cdp.ev("(function(){try{return !!Crypto.myPubRaw()}catch(e){return false}})()"):
                break
            time.sleep(0.25)

        print("\n[1] 点「桥」药丸")
        cdp.ev("document.getElementById('pillBridge').click()")
        cdp.pump(1.0)
        shown = cdp.ev("document.getElementById('popBridge').classList.contains('show')")
        print(f"   弹窗显示: {shown}")
        print(f"   下载按钮存在: {cdp.ev('!!document.getElementById(\"btnDownload\")')}")
        print(f"   按钮可见: {cdp.ev('''(function(){
            var b=document.getElementById('btnDownload');
            var r=b.getBoundingClientRect();
            return r.width>0 && r.height>0;
        })()''')}")

        print("\n[2] 点「下载安装桥」")
        cdp.ev("document.getElementById('btnDownload').click()")
        cdp.pump(5.0)

        print("\n[3] 下载相关事件")
        found = False
        for m in cdp.events:
            if "download" in m["method"].lower():
                found = True
                print(f"   {m['method']}: {json.dumps(m.get('params', {}), ensure_ascii=False)[:300]}")
        if not found:
            print("   （没有任何下载事件 —— 下载根本没触发）")

        print("\n[4] 下载目录内容")
        if headed:
            after = set(os.listdir(default_dl))
            new = after - before
            if new:
                for f in new:
                    p = os.path.join(default_dl, f)
                    print(f"   [新文件] {f}  {os.path.getsize(p)} 字节")
            else:
                print("   （没有新文件落到 Downloads —— 被拦住了）")
            # CRT 里那种 .crdownload 说明下载卡住了
            stuck = [f for f in after if f.endswith(".crdownload")]
            if stuck:
                print(f"   [卡住] {stuck}  <- 正在下载或等待用户确认")
        else:
            files = os.listdir(dl)
            if files:
                for f in files:
                    p = os.path.join(dl, f)
                    print(f"   {f}  {os.path.getsize(p)} 字节")
            else:
                print("   （空 —— 文件没落盘）")

        print("\n[5] 控制台输出")
        if cdp.console:
            for c in cdp.console[-20:]:
                print(f"   {c}")
        else:
            print("   （无）")

        print("\n[6] 直接在页面里跑 blob 下载，看是不是 blob 的问题")
        cdp.ev("""
            (function(){
              var blob = new Blob(["hello-test"], {type:"application/octet-stream"});
              var url = URL.createObjectURL(blob);
              var a = document.createElement("a");
              a.href = url; a.download = "plain-test.txt";
              document.body.appendChild(a); a.click();
              return a.href;
            })()
        """)
        cdp.pump(3.0)
        print("   下载目录:", os.listdir(dl))
        for m in cdp.events:
            if "download" in m["method"].lower():
                print(f"   {m['method']}: {json.dumps(m.get('params', {}), ensure_ascii=False)[:200]}")

    except Exception as exc:
        print(f"\n[X] {type(exc).__name__}: {exc}")
    finally:
        if cdp:
            try: cdp.close()
            except Exception: pass
        proc.terminate()
        try: proc.wait(timeout=5)
        except subprocess.TimeoutExpired: proc.kill()
        shutil.rmtree(profile, ignore_errors=True)
        shutil.rmtree(dl, ignore_errors=True)


if __name__ == "__main__":
    raise SystemExit(main())
