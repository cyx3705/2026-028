#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
把 cs/bridge.cs 以 base64 内嵌进 template.html，产出单文件 chat.html。

为什么用 base64 而不是原文内嵌：
  C# 源码里有 < > & | " \\ 这些字符，直接塞进 JS 字符串需要大量转义，
  改一次 bridge.cs 就得重转一次，迟早出错。base64 一次编码，永久安全。

用法:
    python build_page.py
"""

from __future__ import annotations

import base64
import datetime
import hashlib
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
BRIDGE_CS = os.path.join(HERE, "..", "cs", "bridge.cs")
WXDB_CS = os.path.join(HERE, "..", "cs", "wxdb.cs")
TEMPLATE = os.path.join(HERE, "template.html")
OUTPUT = os.path.join(HERE, "chat.html")
PLACEHOLDER = "__BRIDGE_B64__"
WXDB_PLACEHOLDER = "__WXDB_B64__"
BUILD_PLACEHOLDER = "__BUILD__"


def main() -> int:
    for s in (sys.stdout, sys.stderr):
        try:
            s.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
        except Exception:
            pass

    for p in (BRIDGE_CS, WXDB_CS, TEMPLATE):
        if not os.path.isfile(p):
            print(f"[X] 找不到 {p}")
            return 1

    with open(BRIDGE_CS, "rb") as f:
        raw = f.read()
    b64 = base64.b64encode(raw).decode("ascii")

    # bridge.cs 已经 using LocalChat 了，页面里那个自解压 bat 要一起编译
    # wxdb.cs，否则对面双击安装时一半是 CS0246 编不过。
    with open(WXDB_CS, "rb") as f:
        wraw = f.read()
    wb64 = base64.b64encode(wraw).decode("ascii")

    with open(TEMPLATE, "r", encoding="utf-8") as f:
        tpl = f.read()

    if PLACEHOLDER not in tpl:
        print(f"[X] 模板里没有占位符 {PLACEHOLDER}")
        return 1
    if WXDB_PLACEHOLDER not in tpl:
        print(f"[X] 模板里没有占位符 {WXDB_PLACEHOLDER}")
        return 1

    # 构建戳：网页上会显示出来，这样"浏览器里跑的是哪一版"一目了然。
    # （踩过：磁盘上 chat.html 已更新，但用户标签页还是旧的，白排查半天。）
    stamp = datetime.datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    src_hash = hashlib.sha256(raw + wraw).hexdigest()[:8]

    out = tpl.replace(PLACEHOLDER, b64)
    out = out.replace(WXDB_PLACEHOLDER, wb64)
    out = out.replace(BUILD_PLACEHOLDER, f"{stamp} · cs:{src_hash}")

    with open(OUTPUT, "w", encoding="utf-8", newline="\n") as f:
        f.write(out)

    print(f"  bridge.cs   {len(raw):>8,} 字节")
    print(f"  wxdb.cs     {len(wraw):>8,} 字节  (sha256:{src_hash})")
    print(f"  base64      {len(b64) + len(wb64):>8,} 字符")
    print(f"  chat.html   {len(out):>8,} 字符  ({len(out.encode('utf-8')):,} 字节)")
    print(f"  构建戳      {stamp}")
    print(f"  -> {OUTPUT}")

    # 自检
    if PLACEHOLDER in out or WXDB_PLACEHOLDER in out or BUILD_PLACEHOLDER in out:
        print("[X] 还有残留占位符"); return 1
    if base64.b64decode(b64) != raw or base64.b64decode(wb64) != wraw:
        print("[X] base64 往返不一致"); return 1
    print("  [OK] 占位符已替换，base64 往返校验通过")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
