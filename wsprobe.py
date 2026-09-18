#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""直接跟桥的 WebSocket 说话，用来判断桥到底是"卡住了"还是"只是发送失败"。

这是一个独立于页面的探针：页面里的 beacon 只能告诉我页面看到了什么，
这个脚本能直接问桥本人。

用法：
    python wsprobe.py --status                     # 安全探针，不碰微信
    python wsprobe.py --send "你好"                # 真发一条到微信当前对话
    python wsprobe.py '{"type":"status"}'          # 原始 JSON（小心引号，见下）

【重要】为什么推荐 --send / --status 而不是自己拼 JSON：
    Windows PowerShell 5.1 把参数交给原生 exe 时会把里面的双引号吃掉。
    于是 '{"type":"status"}' 到 Python 手里会变成 {type:status} ——
    非法 JSON，桥的 JavaScriptSerializer 直接报
      "无效的 JSON 基元: status" 然后丢弃这一帧，表现就是"桥不回话"。
    这个坑我踩过整整一轮：以为桥坏了，其实是探针发的东西不合法。
    所以 JSON 一律在 Python 里用 json.dumps 拼。

为什么要有 --status 这个安全探针：
    send 最终会调 SendToWeChat()，那会动剪贴板并给微信发 Ctrl+V / 回车。
    status 走完全独立的查询分支，一个键都不按。所以：
      status 有回执、send 超时  -> WS 线程活着，卡在 SendToWeChat 里面
      status 也没回执           -> WS 处理线程整个死了
"""

import argparse
import base64
import json
import os
import socket
import struct
import sys
import time

HOST = "127.0.0.1"
PORT = 8765


def handshake(sock):
    key = base64.b64encode(os.urandom(16)).decode()
    req = (
        "GET /ws HTTP/1.1\r\n"
        "Host: %s:%d\r\n"
        "Upgrade: websocket\r\n"
        "Connection: Upgrade\r\n"
        "Sec-WebSocket-Key: %s\r\n"
        "Sec-WebSocket-Version: 13\r\n"
        "\r\n" % (HOST, PORT, key)
    )
    sock.sendall(req.encode())

    buf = b""
    while b"\r\n\r\n" not in buf:
        chunk = sock.recv(4096)
        if not chunk:
            raise RuntimeError("握手时连接被关闭")
        buf += chunk
    head, _, rest = buf.partition(b"\r\n\r\n")
    status = head.split(b"\r\n")[0].decode(errors="replace")
    if "101" not in status:
        raise RuntimeError("握手失败: " + status)
    return rest


def send_text(sock, text):
    payload = text.encode("utf-8")
    n = len(payload)
    header = bytes([0x81])                      # FIN + opcode=text
    if n < 126:
        header += bytes([0x80 | n])             # 客户端必须掩码
    elif n < 65536:
        header += bytes([0x80 | 126]) + struct.pack(">H", n)
    else:
        header += bytes([0x80 | 127]) + struct.pack(">Q", n)
    mask = os.urandom(4)
    masked = bytes(payload[i] ^ mask[i % 4] for i in range(n))
    sock.sendall(header + mask + masked)


def recv_frame(sock, carry=b""):
    """读一帧。返回 (opcode, payload, 剩余缓冲)。"""
    buf = carry
    while len(buf) < 2:
        chunk = sock.recv(4096)
        if not chunk:
            return None, None, b""
        buf += chunk

    b0, b1 = buf[0], buf[1]
    opcode = b0 & 0x0F
    masked = b1 & 0x80
    length = b1 & 0x7F
    pos = 2

    if length == 126:
        while len(buf) < pos + 2:
            buf += sock.recv(4096)
        length = struct.unpack(">H", buf[pos:pos + 2])[0]
        pos += 2
    elif length == 127:
        while len(buf) < pos + 8:
            buf += sock.recv(4096)
        length = struct.unpack(">Q", buf[pos:pos + 8])[0]
        pos += 8

    mask = None
    if masked:
        while len(buf) < pos + 4:
            buf += sock.recv(4096)
        mask = buf[pos:pos + 4]
        pos += 4

    while len(buf) < pos + length:
        chunk = sock.recv(4096)
        if not chunk:
            return None, None, b""
        buf += chunk

    payload = buf[pos:pos + length]
    if mask:
        payload = bytes(payload[i] ^ mask[i % 4] for i in range(length))
    return opcode, payload, buf[pos + length:]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("message", nargs="?",
                    help="原始 JSON。注意 PowerShell 会吃掉双引号，优先用 --send/--status")
    ap.add_argument("--send", metavar="TEXT",
                    help="发一条 send 消息（JSON 在 Python 里拼，绕开 shell 引号问题）")
    ap.add_argument("--status", action="store_true",
                    help="发 type:status —— 安全，不碰微信、不按键")
    ap.add_argument("--timeout", type=float, default=10.0,
                    help="等回执的秒数；send 会走微信，建议给大一点")
    args = ap.parse_args()

    if args.send is not None:
        message = json.dumps({"type": "send", "text": args.send}, ensure_ascii=False)
        want = '"sent"'
    elif args.status:
        message = json.dumps({"type": "status"})
        want = '"status"'
    elif args.message:
        message = args.message
        want = '"sent"'
    else:
        message = json.dumps({"type": "status"})
        want = '"status"'

    print("连 ws://%s:%d/ws ..." % (HOST, PORT))
    try:
        sock = socket.create_connection((HOST, PORT), timeout=5)
    except OSError as e:
        print("[失败] 连不上桥: %s" % e)
        return 4
    try:
        carry = handshake(sock)
    except Exception as e:
        print("[失败] %s" % e)
        return 4
    print("握手 101 OK")
    print("发送 -> " + message)

    t0 = time.time()
    send_text(sock, message)

    got = 0
    while True:
        # 收到过回执之后就用短超时：没别的事就正常收工，
        # 而不是傻等满 --timeout 再报一句吓人的"超时"
        sock.settimeout(2.0 if got else args.timeout)
        try:
            opcode, payload, carry = recv_frame(sock, carry)
        except socket.timeout:
            print("")
            if got:
                print("[完成] 收到 %d 条回执，之后连接安静。" % got)
                sock.close()
                return 0
            print("[超时] %.1f 秒内没有任何回执。" % args.timeout)
            print("       桥收下了消息却没回话 —— 卡在处理里面了。")
            sock.close()
            return 2

        if opcode is None or opcode == 0x8:
            print("")
            print("[断开] 连接被桥关闭了。")
            sock.close()
            return 3
        if opcode in (0x1, 0x2):
            txt = payload.decode("utf-8", errors="replace")
            print("回执 (%.2fs) %s" % (time.time() - t0, txt))
            got += 1
            if want in txt:
                print("")
                print("[完成] 拿到期望的回执，收工。")
                sock.close()
                return 0


if __name__ == "__main__":
    sys.exit(main())
