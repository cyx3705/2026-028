#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""回归测试：桥不能把"用户事后复制的内容"误当成回声吞掉。

踩过的坑
--------
ClipboardWatcher 里原来是这么写的：

    if (text == _lastSentText) continue;   // do not echo our own paste

只要剪贴板内容跟"桥最后一次粘出去的"相同就**永久**忽略。于是用户主动
Ctrl+C 复制同一条消息时，桥一声不响、页面永远收不到 —— 单机自测
（测试模式会把对方的回复也发到微信）时必现。

修法：按剪贴板序号精确识别"这一次变化是我们自己造成的"，文本兜底只吞一次。

这个脚本就是那条修复的回归测试：
    1. 通过 WS 让桥发一条独特内容到微信（桥会把它粘进剪贴板）
    2. 等桥粘完
    3. 用 Set-Clipboard 把**同样的内容**重新写进剪贴板，模拟用户 Ctrl+C
    4. 断言桥把它推给了网页

跑法（需要桥在跑；会真的往微信当前对话发一条测试文本）：
    python test_clipboard_echo.py
"""

import json
import socket
import subprocess
import sys
import time

import wsprobe as W


def connect():
    sock = socket.create_connection((W.HOST, W.PORT), timeout=5)
    carry = W.handshake(sock)
    return sock, carry


def wait_for(sock, carry, predicate, timeout):
    """读到一条满足 predicate 的文本帧就返回它。"""
    deadline = time.time() + timeout
    seen = []
    while time.time() < deadline:
        sock.settimeout(max(0.2, deadline - time.time()))
        try:
            op, payload, carry = W.recv_frame(sock, carry)
        except socket.timeout:
            break
        if op is None:
            break
        if op in (0x1, 0x2):
            txt = payload.decode("utf-8", errors="replace")
            seen.append(txt)
            if predicate(txt):
                return txt, seen, carry
    return None, seen, carry


def set_clipboard(text):
    """模拟用户按 Ctrl+C —— 用 PowerShell 直接写剪贴板，效果等价。"""
    r = subprocess.run(
        ["powershell", "-NoProfile", "-NonInteractive", "-Command",
         "Set-Clipboard -Value ([Console]::In.ReadToEnd())"],
        input=text, capture_output=True, text=True,
    )
    return r.returncode == 0


def main():
    marker = "ECHO-REGRESSION-%d" % int(time.time())
    print("标记文本: " + marker)
    print("")

    print("[1] 连桥 ...")
    try:
        sock, carry = connect()
    except OSError as e:
        print("    连不上桥: %s" % e)
        return 2
    print("    握手 101 OK")

    print("[2] 让桥把标记发到微信（这一步会真的往当前对话粘一条）...")
    W.send_text(sock, json.dumps({"type": "send", "text": marker}))
    got, seen, carry = wait_for(sock, carry, lambda t: '"sent"' in t, 25)
    if not got:
        print("    没等到 sent 回执，收到: %r" % seen)
        sock.close()
        return 2
    print("    " + got)
    if '"ok":true' not in got:
        print("    桥没能发出去（微信没打开对话？）—— 测试无法继续")
        sock.close()
        return 2

    # 给剪贴板监听线程一点时间，让它把"我们自己粘的那一次"消化掉
    print("[3] 等监听线程消化掉回声 ...")
    time.sleep(2.0)

    print("[4] 把**同样的内容**重新写进剪贴板，模拟用户 Ctrl+C ...")
    if not set_clipboard(marker):
        print("    写剪贴板失败")
        sock.close()
        return 2

    print("[5] 等桥推给网页 ...")
    got2, seen2, carry = wait_for(
        sock, carry,
        lambda t: '"clipboard"' in t and marker in t, 15)

    sock.close()
    print("")
    if got2:
        print("[通过] 桥把用户复制的同一条内容推过来了 —— 没有被误吞。")
        print("       " + got2[:120])
        return 0
    print("[失败] 15 秒内没有收到 clipboard 推送。")
    print("       收到过的帧: %r" % seen2)
    print("       这就是那个 bug：内容跟 _lastSentText 相同被永久忽略了。")
    return 1


if __name__ == "__main__":
    sys.exit(main())
