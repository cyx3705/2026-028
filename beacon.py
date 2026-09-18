#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""页面探针接收端：把 chat.html 里埋的 beacon 收下来打屏。

页面每次关键操作都会往 http://127.0.0.1:8766/report 发一个 1x1 图片
（no-cors 子资源，file:// 页面也能发）。这个脚本就是那个端点。

为什么端口是 8766 而不是桥的 8765：
    诊断接收端必须能一直开着。它一旦占了 8765，就会把真正的桥挤掉 ——
    更糟的是页面会连上它、以为桥已经装好了。分端口之后互不干扰。

为什么用 1x1 图片而不是 fetch：
    no-cors 的图片子资源不受 CORS 限制，file:// 页面也能发，
    发不出去也只是静默失败，绝不影响页面主流程。

用法：
    python beacon.py              # 前台跑，Ctrl+C 退出
    python beacon.py --port 8766
"""

import argparse
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import urlparse, parse_qs, unquote

# 1x1 透明 GIF
PIXEL = bytes.fromhex(
    "47494638396101000100800000ffffff00000021f90401000000002c00000000"
    "010001000002024401003b"
)

PORT = 8766


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        u = urlparse(self.path)

        if u.path == "/report":
            q = parse_qs(u.query)
            tag = q.get("t", ["?"])[0]
            build = q.get("b", [""])[0]
            proto = q.get("p", [""])[0]
            extra = q.get("x", [""])[0]

            print("[%s] %-22s b=%s  p=%s  %s" % (
                self.log_date_time_string(), tag, build, proto, extra))
            sys.stdout.flush()

            self.send_response(200)
            self.send_header("Content-Type", "image/gif")
            self.send_header("Content-Length", str(len(PIXEL)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("Access-Control-Allow-Origin", "*")
            self.end_headers()
            self.wfile.write(PIXEL)
            return

        # 顺带提供一个健康检查，方便确认接收端真的在跑
        if u.path in ("/", "/health"):
            body = b"beacon receiver alive\n"
            self.send_response(200)
            self.send_header("Content-Type", "text/plain; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return

        self.send_response(404)
        self.send_header("Content-Length", "0")
        self.end_headers()

    def do_POST(self):
        self.do_GET()

    def log_message(self, *a):
        pass    # 只打印探针，不打 HTTP 访问日志


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=PORT)
    args = ap.parse_args()

    srv = ThreadingHTTPServer(("127.0.0.1", args.port), Handler)
    print("探针接收端已启动  http://127.0.0.1:%d/report" % args.port)
    print("页面里的 beacon 会打到这上面。Ctrl+C 退出。")
    print("")
    sys.stdout.flush()
    try:
        srv.serve_forever()
    except KeyboardInterrupt:
        print("\n已停止。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
