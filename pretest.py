#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""测试前清场：关闭桥，确认端口干净。

规矩（用户定的）：每次测试前都必须先把桥关掉。

这个脚本就是那条规矩的执行者。跑一次，下面几件事全做完：

  1. 杀掉所有 bridge.exe（C# 桥本体）
  2. 杀掉所有命令行含 bridge.py 的 python（探针接收端 / 原型桥）
  3. **按 PID 杀掉占用 8765 的任何进程**（兜底，最可靠的一条）
  4. 用真实 TCP 连接确认 8765 拒绝连接

为什么第 3 条必须有：
  实测 tasklist 的输出捕获在这台机器上会失败（返回空），
  于是脚本会误报"没有正在运行的桥"，而端口其实被占着。
  按端口找 PID 再杀，绕开所有输出解析问题。

为什么不用 Get-NetTCPConnection 判断端口：
  实测那个 cmdlet 在这台机器上谎报过"空闲"（服务明明在应答 HTTP 200）。
  一律以真实连接为准；找 PID 用 netstat -ano。

用法：
    python pretest.py            # 清场并检查
    python pretest.py --quiet    # 只输出结论行
"""

import socket
import subprocess
import sys
import time

BRIDGE_PORT = 8765      # C# 桥的端口，必须空着
BEACON_PORT = 8766      # 探针接收端，诊断用，不参与主流程


def _run(cmd):
    return subprocess.run(cmd, shell=True, capture_output=True, text=True)


def _stop_pid(pid):
    """用 PowerShell 的 Stop-Process 杀。taskkill 在这台机器上会报 Access denied。"""
    r = subprocess.run(
        ["powershell", "-NoProfile", "-NonInteractive", "-Command",
         "Stop-Process -Id %s -Force -ErrorAction Stop; 'OK'" % pid],
        capture_output=True, text=True,
    )
    return "OK" in (r.stdout or "")


def kill_by_image():
    killed = []
    out = _run('tasklist /FI "IMAGENAME eq bridge.exe" /FO CSV /NH').stdout or ""
    if "bridge.exe" not in out:
        return killed
    r = _run("taskkill /F /IM bridge.exe")
    if r.returncode == 0:
        killed.append("bridge.exe (按映像名)")
    return killed


def kill_bridge_py():
    ps = (
        "Get-CimInstance Win32_Process -Filter \"name='python.exe'\" | "
        "Where-Object { $_.CommandLine -like '*bridge.py*' } | "
        "ForEach-Object { $_.ProcessId }"
    )
    r = subprocess.run(
        ["powershell", "-NoProfile", "-NonInteractive", "-Command", ps],
        capture_output=True, text=True,
    )
    killed = []
    for pid in [t for t in (r.stdout or "").split() if t.isdigit()]:
        _stop_pid(pid)
        killed.append("python(bridge.py) pid=" + pid)
    return killed


def port_owner_pids(port):
    """从 netstat -ano 里找出正在 LISTEN 这个端口的 PID。"""
    out = _run("netstat -ano").stdout or ""
    pids = set()
    needle = ":" + str(port)
    for line in out.splitlines():
        parts = line.split()
        if len(parts) < 5:
            continue
        if parts[0].upper() != "TCP":
            continue
        if not parts[1].endswith(needle):
            continue
        if parts[3].upper() != "LISTENING":
            continue
        pids.add(parts[4])
    return sorted(pids)


def port_open(port, timeout=0.7):
    """真的去连一下。连得上 = 有人占着。"""
    s = socket.socket()
    s.settimeout(timeout)
    try:
        s.connect(("127.0.0.1", port))
        return True
    except OSError:
        return False
    finally:
        s.close()


def main():
    quiet = "--quiet" in sys.argv

    def say(msg):
        if not quiet:
            print(msg)

    say("清场：关闭桥 ...")
    killed = kill_by_image() + kill_bridge_py()

    # 兜底：谁占着 8765 就杀谁
    for pid in port_owner_pids(BRIDGE_PORT):
        if _stop_pid(pid):
            killed.append("占用 %d 的进程 pid=%s" % (BRIDGE_PORT, pid))

    if killed:
        for k in killed:
            say("  已终止  " + k)
    else:
        say("  没有正在运行的桥")

    time.sleep(0.8)

    bridge_busy = port_open(BRIDGE_PORT)
    beacon_busy = port_open(BEACON_PORT)

    if not quiet:
        print("")
        print("端口检查（以真实连接为准）:")
        print("  %d (桥)        %s" % (BRIDGE_PORT, "被占用 <<< 不能测！" if bridge_busy else "空闲 OK"))
        print("  %d (探针)      %s" % (BEACON_PORT, "有接收端" if beacon_busy else "空闲"))

    if bridge_busy:
        print("")
        print("[失败] %d 仍被占用，桥没关干净。" % BRIDGE_PORT)
        for pid in port_owner_pids(BRIDGE_PORT):
            print("       还占着的 pid = " + pid)
        return 1

    if not quiet:
        print("")
        print("[就绪] 桥已关闭，%d 空闲。可以开始测试。" % BRIDGE_PORT)
    return 0


if __name__ == "__main__":
    sys.exit(main())
