#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
验证 zip 安装包：下载 -> 是不是合法 zip -> 解出来的 .bat 能不能编译出桥。

这一步是因为 .bat 直接下载被浏览器拦截（落成 "未确认 xxx.crdownload"），
改成 zip 分发后必须确认 zip 本身是合法的、内容是对的。

用法:
    python test_zip.py
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
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import test_crypto as tc   # noqa: E402
from test_download import CdpEvents   # noqa: E402

PORT = 9371


def main():
    for s in (sys.stdout, sys.stderr):
        try: s.reconfigure(encoding="utf-8", errors="replace")
        except Exception: pass

    browser = tc.find_browser()
    url = "file:///" + os.path.join(HERE, "chat.html").replace("\\", "/")
    dl = tempfile.mkdtemp(prefix="zip-test-")
    profile = tempfile.mkdtemp(prefix="zip-profile-")

    proc = subprocess.Popen(
        [browser, "--headless=new", "--disable-gpu", "--no-first-run",
         "--no-default-browser-check", "--disable-extensions",
         "--disable-crash-reporter", "--disable-breakpad",
         f"--remote-debugging-port={PORT}", f"--user-data-dir={profile}", url],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)

    cdp = None
    ok = True
    try:
        cdp = CdpEvents(tc.wait_target(PORT, url))
        cdp.call("Runtime.enable")
        cdp.call("Page.enable")
        cdp.call("Browser.setDownloadBehavior", behavior="allow",
                 downloadPath=dl, eventsEnabled=True)
        for _ in range(40):
            if cdp.ev("(function(){try{return !!Crypto.myPubRaw()}catch(e){return false}})()"):
                break
            time.sleep(0.25)

        print("[1] 点桥药丸 -> 下载")
        cdp.ev("document.getElementById('pillBridge').click()")
        cdp.pump(0.6)
        cdp.ev("document.getElementById('btnDownload').click()")
        cdp.pump(4.0)

        files = os.listdir(dl)
        print(f"   下载目录: {files}")
        zipname = next((f for f in files if f.endswith(".zip")), None)
        tc.check("下载到了 zip（不是被挂起的 .crdownload）",
                 zipname is not None and not any(f.endswith(".crdownload") for f in files),
                 str(files))
        if not zipname:
            raise SystemExit(1)

        zpath = os.path.join(dl, zipname)
        print(f"   {zipname}  {os.path.getsize(zpath)} 字节")

        print("\n[2] 是不是合法 zip")
        tc.check("zipfile 能打开", zipfile.is_zipfile(zpath))
        with zipfile.ZipFile(zpath) as z:
            bad = z.testzip()
            tc.check("zip 完整性检查通过", bad is None, str(bad))
            names = z.namelist()
            print(f"   内容: {names}")
            tc.check("含 install-bridge.bat", "install-bridge.bat" in names, str(names))

            out = tempfile.mkdtemp(prefix="zip-out-")
            z.extractall(out)

        bat = os.path.join(out, "install-bridge.bat")
        data = open(bat, "rb").read()
        tc.check("解出来的 .bat 非空", len(data) > 5000, str(len(data)))
        tc.check("解出来的 .bat 是纯 ASCII", all(b < 128 for b in data), "含非 ASCII")

        print("\n[3] 解压出来的 .bat 能不能编译出可用桥")
        log = open(os.path.join(out, "log.txt"), "w", encoding="utf-8")
        bp = subprocess.Popen(["cmd", "/c", "install-bridge.bat"],
                              cwd=out, stdout=log, stderr=subprocess.STDOUT)
        time.sleep(9)
        exe = os.path.join(out, "bridge.exe")
        tc.check("编译出了 bridge.exe", os.path.isfile(exe),
                 "没生成" if not os.path.isfile(exe) else "")
        if os.path.isfile(exe):
            print(f"   bridge.exe: {os.path.getsize(exe)} 字节")

        bp.terminate()
        try: bp.wait(timeout=5)
        except subprocess.TimeoutExpired: bp.kill()
        subprocess.run(["powershell", "-NoProfile", "-Command",
                        "Get-Process bridge -ErrorAction SilentlyContinue | "
                        "Where-Object { $_.Path -like '*zip-out*' } | Stop-Process -Force"],
                       capture_output=True)
        shutil.rmtree(out, ignore_errors=True)

    except SystemExit:
        ok = False
    except Exception as exc:
        tc.check("zip 测试执行", False, f"{type(exc).__name__}: {exc}")
        ok = False
    finally:
        if cdp:
            try: cdp.close()
            except Exception: pass
        proc.terminate()
        try: proc.wait(timeout=5)
        except subprocess.TimeoutExpired: proc.kill()
        shutil.rmtree(profile, ignore_errors=True)
        shutil.rmtree(dl, ignore_errors=True)

    print(f"\n{'=' * 46}")
    print(f"通过 {tc.passed} / 失败 {tc.failed}")
    return 1 if (tc.failed or not ok) else 0


if __name__ == "__main__":
    raise SystemExit(main())
