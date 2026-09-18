// bridge.cs -- WeChat clipboard bridge.
//
// Targets the C# compiler that ships with every Windows:
//     C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe   (C# 5 only!)
// so the user needs to install NOTHING. No SDK, no NuGet, no runtime pack.
//
// What it does:
//   * serves im.html and a WebSocket on 127.0.0.1
//   * SEND: page -> bridge -> clipboard -> focus WeChat -> Ctrl+V -> Enter
//   * RECV: you Ctrl+C in WeChat -> bridge notices clipboard change -> page
//
// It never touches the WeChat protocol, so it is version independent.
//
// C# 5 constraints (no string interpolation, no ?., no out var,
// no expression-bodied members, no auto-property initializers).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

internal static class Bridge
{
    private const string Host = "127.0.0.1";
    private const int Port = 8765;
    private const string WsMagic = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

    // WebSocket opcodes
    private const int OpCont = 0x0, OpText = 0x1, OpBin = 0x2;
    private const int OpClose = 0x8, OpPing = 0x9, OpPong = 0xA;

    // Win32
    private const int CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0042;
    private const int SwRestore = 9;
    private const ushort VkControl = 0x11, VkV = 0x56, VkReturn = 0x0D;
    private const uint KeyeventfKeyup = 0x0002;

    private static readonly object ClientsLock = new object();
    private static readonly List<TcpClient> Clients = new List<TcpClient>();

    private static string _pagePath;
    private static string _lastSentText = "";
    private static IntPtr _wechatHwnd = IntPtr.Zero;
    private static int _wechatPid = 0;

    // ------------------------------------------------------------------
    // Win32 interop
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    private const uint InputKeyboard = 1;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] private static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr hMem);

    // ------------------------------------------------------------------
    // Logging
    // ------------------------------------------------------------------

    private static void Log(string msg)
    {
        Console.WriteLine("[" + DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "] " + msg);
    }

    // ------------------------------------------------------------------
    // Clipboard
    // ------------------------------------------------------------------

    private static string GetClipboardText()
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!IsClipboardFormatAvailable(CfUnicodeText)) return null;
                if (!OpenClipboard(IntPtr.Zero)) { Thread.Sleep(30); continue; }
                try
                {
                    if (!IsClipboardFormatAvailable(CfUnicodeText)) return null;
                    IntPtr h = GetClipboardData(CfUnicodeText);
                    if (h == IntPtr.Zero) return null;
                    IntPtr p = GlobalLock(h);
                    if (p == IntPtr.Zero) return null;
                    try { return Marshal.PtrToStringUni(p); }
                    finally { GlobalUnlock(h); }
                }
                finally { CloseClipboard(); }
            }
            catch { Thread.Sleep(30); }
        }
        return null;
    }

    private static bool SetClipboardText(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!OpenClipboard(IntPtr.Zero)) { Thread.Sleep(30); continue; }
                try
                {
                    if (!EmptyClipboard()) return false;

                    byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                    IntPtr hMem = GlobalAlloc(GmemMoveable, new UIntPtr((uint)bytes.Length));
                    if (hMem == IntPtr.Zero) return false;

                    IntPtr target = GlobalLock(hMem);
                    if (target == IntPtr.Zero) { GlobalFree(hMem); return false; }
                    Marshal.Copy(bytes, 0, target, bytes.Length);
                    GlobalUnlock(hMem);

                    // After SetClipboardData succeeds the OS owns hMem.
                    if (SetClipboardData(CfUnicodeText, hMem) == IntPtr.Zero)
                    {
                        GlobalFree(hMem);
                        return false;
                    }
                    return true;
                }
                finally { CloseClipboard(); }
            }
            catch { Thread.Sleep(30); }
        }
        return false;
    }

    // ------------------------------------------------------------------
    // WeChat window
    // ------------------------------------------------------------------

    private static void FindWeChat()
    {
        Process[] procs = Process.GetProcessesByName("Weixin");
        if (procs.Length == 0) procs = Process.GetProcessesByName("WeChat");

        foreach (Process p in procs)
        {
            IntPtr h = p.MainWindowHandle;
            if (h != IntPtr.Zero)
            {
                _wechatHwnd = h;
                _wechatPid = p.Id;
                StringBuilder sb = new StringBuilder(256);
                GetClassName(h, sb, 256);
                Log("found WeChat: pid=" + p.Id + " class=" + sb.ToString());
                return;
            }
        }
        _wechatHwnd = IntPtr.Zero;
        _wechatPid = 0;
    }

    private static void EnsureWeChat()
    {
        if (_wechatHwnd != IntPtr.Zero && IsWindow(_wechatHwnd)) return;
        FindWeChat();
    }

    /// <summary>Class name of a window, or "" on failure.</summary>
    private static string ClassOf(IntPtr hwnd)
    {
        StringBuilder sb = new StringBuilder(256);
        GetClassName(hwnd, sb, 256);
        return sb.ToString();
    }

    private static int PidOf(IntPtr hwnd)
    {
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        return (int)pid;
    }

    /// <summary>
    /// THE SAFETY GUARD.
    /// Returns true only when the current foreground window really belongs to
    /// the WeChat process. Keystrokes are sent only when this passes, so a
    /// failed activation aborts instead of pasting into some random window.
    /// </summary>
    private static bool WeChatIsForeground()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;

        int fgPid = PidOf(fg);
        if (_wechatPid != 0 && fgPid != _wechatPid) return false;

        // If we have not pinned a window yet, accept any WeChat-process window
        // whose class looks like the Qt main window.
        if (_wechatPid == 0)
        {
            try
            {
                Process p = Process.GetProcessById(fgPid);
                if (!(p.ProcessName == "Weixin" || p.ProcessName == "WeChat")) return false;
            }
            catch { return false; }
        }

        string cls = ClassOf(fg);
        return cls.IndexOf("Qt", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private const int GwlStyle = -16;
    private const int WsThickframe = 0x00040000;

    /// <summary>
    /// 判断微信是不是停在"已登录主界面"，而不是"登录二维码界面"。
    ///
    /// 为什么需要这个：踩过一次 —— 微信被顶下线后弹着登录二维码，
    /// 桥的前台校验照样通过（同一个 pid、同一个 Qt 窗口类），
    /// 结果密文被粘进了二维码窗口，而桥还报告 ok。
    ///
    /// 两个信号（实测数据）：
    ///   登录界面   295x387，固定尺寸不可缩放
    ///   主界面  1017x690 / 1721x1041，可缩放（有 WS_THICKFRAME）
    /// </summary>
    private static bool WeChatLooksLoggedIn(IntPtr hwnd)
    {
        RECT r;
        if (!GetWindowRect(hwnd, out r)) return false;

        int w = r.Right - r.Left;
        int h = r.Bottom - r.Top;
        if (w < 480 || h < 380) return false;

        // 登录窗口没有可缩放边框
        int style = GetWindowLong(hwnd, GwlStyle);
        if ((style & WsThickframe) == 0) return false;

        return true;
    }

    private static string WindowSizeText(IntPtr hwnd)
    {
        RECT r;
        if (!GetWindowRect(hwnd, out r)) return "?";
        return (r.Right - r.Left) + "x" + (r.Bottom - r.Top);
    }

    /// <summary>ready / not-logged-in / not-found，给网页看的。</summary>
    private static string WeChatState()
    {
        if (_wechatHwnd == IntPtr.Zero) return "not-found";
        return WeChatLooksLoggedIn(_wechatHwnd) ? "ready" : "not-logged-in";
    }

    private static bool ActivateWeChat()
    {        EnsureWeChat();
        if (_wechatHwnd == IntPtr.Zero) return false;

        if (IsIconic(_wechatHwnd)) ShowWindow(_wechatHwnd, SwRestore);

        // SetForegroundWindow is unreliable from a background process; the
        // AttachThreadInput dance is the standard workaround.
        uint dummy;
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out dummy);
        uint ourThread = GetCurrentThreadId();
        bool attached = false;
        try
        {
            if (fgThread != ourThread) attached = AttachThreadInput(fgThread, ourThread, true);
            BringWindowToTop(_wechatHwnd);
            SetForegroundWindow(_wechatHwnd);
            SetFocus(_wechatHwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(fgThread, ourThread, false);
        }

        Thread.Sleep(120);
        return WeChatIsForeground();
    }

    // ------------------------------------------------------------------
    // Input
    // ------------------------------------------------------------------

    private static void SendKey(ushort vk, bool up)
    {
        INPUT[] seq = new INPUT[1];
        seq[0].type = InputKeyboard;
        seq[0].U.ki.wVk = vk;
        seq[0].U.ki.wScan = 0;
        seq[0].U.ki.dwFlags = up ? KeyeventfKeyup : 0;
        seq[0].U.ki.time = 0;
        seq[0].U.ki.dwExtraInfo = IntPtr.Zero;
        SendInput(1, seq, Marshal.SizeOf(typeof(INPUT)));
    }

    /// <summary>
    /// Paste the given text into the currently open WeChat conversation.
    /// Returns a status string that is safe to show to the user.
    /// </summary>
    private static string SendToWeChat(string text)
    {
        if (string.IsNullOrEmpty(text)) return "empty";

        if (!SetClipboardText(text))
        {
            return "error: 写剪贴板失败（可能被其它程序占用），已中止";
        }
        _lastSentText = text;

        if (!ActivateWeChat())
        {
            // Deliberately do NOT send keys here.
            return "error: 无法把微信窗口切到前台，已中止（按键未发送）";
        }

        // Re-check right before typing. This is the last line of defence.
        if (!WeChatIsForeground())
        {
            return "error: 前台校验未通过，已中止（按键未发送）";
        }

        // 守卫三：微信必须停在已登录主界面，别把密文粘进登录二维码窗口
        if (!WeChatLooksLoggedIn(_wechatHwnd))
        {
            return "error: 微信看起来停在登录界面（窗口 " + WindowSizeText(_wechatHwnd)
                 + "），已中止。请先在微信里登录并打开一个对话。";
        }

        Thread.Sleep(80);
        SendKey(VkControl, false);
        SendKey(VkV, false);
        SendKey(VkV, true);
        SendKey(VkControl, true);
        Thread.Sleep(60);
        SendKey(VkReturn, false);
        SendKey(VkReturn, true);

        return "ok";
    }

    // ------------------------------------------------------------------
    // Screenshot (debug aid: lets us verify a message really landed)
    // ------------------------------------------------------------------

    private static byte[] CaptureWeChat(bool fromScreen)
    {
        EnsureWeChat();
        if (_wechatHwnd == IntPtr.Zero) return null;

        RECT r;
        if (!GetWindowRect(_wechatHwnd, out r)) return null;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return null;

        using (Bitmap bmp = new Bitmap(w, h))
        {
            using (Graphics g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try
                {
                    if (fromScreen)
                    {
                        // PrintWindow often returns black for Qt/GPU windows.
                        IntPtr src = GetWindowDC(_wechatHwnd);
                        try
                        {
                            // BitBlt from the window DC
                            BitBlt(hdc, 0, 0, w, h, src, 0, 0, 0x00CC0020);
                        }
                        finally { ReleaseDC(_wechatHwnd, src); }
                    }
                    else
                    {
                        PrintWindow(_wechatHwnd, hdc, 2); // PW_RENDERFULLCONTENT
                    }
                }
                finally { g.ReleaseHdc(hdc); }
            }

            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int w, int h,
                                      IntPtr hdcSrc, int xSrc, int ySrc, int rop);

    // ------------------------------------------------------------------
    // WebSocket
    // ------------------------------------------------------------------

    private static bool ReadExact(NetworkStream s, byte[] buf, int offset, int count)
    {
        int done = 0;
        while (done < count)
        {
            int n = s.Read(buf, offset + done, count - done);
            if (n <= 0) return false;
            done += n;
        }
        return true;
    }

    private static byte[] WsReceive(NetworkStream s)
    {
        byte[] hdr = new byte[2];
        if (!ReadExact(s, hdr, 0, 2)) throw new IOException("closed");

        int opcode = hdr[0] & 0x0F;
        bool masked = (hdr[1] & 0x80) != 0;
        long len = hdr[1] & 0x7F;

        if (len == 126)
        {
            byte[] e = new byte[2];
            if (!ReadExact(s, e, 0, 2)) throw new IOException("closed");
            len = (e[0] << 8) | e[1];
        }
        else if (len == 127)
        {
            byte[] e = new byte[8];
            if (!ReadExact(s, e, 0, 8)) throw new IOException("closed");
            len = 0;
            for (int i = 0; i < 8; i++) len = (len << 8) | e[i];
        }

        byte[] mask = new byte[4];
        if (masked && !ReadExact(s, mask, 0, 4)) throw new IOException("closed");

        byte[] payload = new byte[len];
        if (len > 0 && !ReadExact(s, payload, 0, (int)len)) throw new IOException("closed");

        if (masked)
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(payload[i] ^ mask[i % 4]);

        // pack opcode into slot 0 so the caller gets one array back
        return Combine(opcode, payload);
    }

    private static byte[] Combine(int opcode, byte[] payload)
    {
        byte[] outBuf = new byte[payload.Length + 1];
        outBuf[0] = (byte)opcode;
        Buffer.BlockCopy(payload, 0, outBuf, 1, payload.Length);
        return outBuf;
    }

    private static void WsSend(TcpClient client, int opcode, byte[] payload)
    {
        NetworkStream s = client.GetStream();
        byte[] hdr;
        if (payload.Length < 126)
        {
            hdr = new byte[2];
            hdr[0] = (byte)(0x80 | opcode);
            hdr[1] = (byte)payload.Length;
        }
        else if (payload.Length < 65536)
        {
            hdr = new byte[4];
            hdr[0] = (byte)(0x80 | opcode);
            hdr[1] = 126;
            hdr[2] = (byte)(payload.Length >> 8);
            hdr[3] = (byte)(payload.Length & 0xFF);
        }
        else
        {
            hdr = new byte[10];
            hdr[0] = (byte)(0x80 | opcode);
            hdr[1] = 127;
            long l = payload.Length;
            for (int i = 9; i >= 2; i--) { hdr[i] = (byte)(l & 0xFF); l >>= 8; }
        }
        // 只锁这一个客户端，绝不用全局锁做网络写。
        //
        // 这里曾经是 lock (ClientsLock)。那是个致命错误：
        // 客户端连接一旦变成死连接（刷新/关闭标签页、半开连接），
        // s.Write 会一直阻塞到 TCP 重传超时，而 TcpClient 默认
        // SendTimeout = 0 表示**无限等**。攥着全局锁的那个线程不放手，
        // 于是 Clients.Add / Broadcast / RemoveClient 全部卡死：
        //   - 新客户端连得上（101 在拿锁之前就写了），但收不到 hello
        //   - 页面的 send / status 全部没有回执
        //   - HTTP /status 反而照常应答，因为它读 Clients.Count 时没加锁
        // 症状就是"桥看着还活着，WebSocket 彻底哑了"。
        lock (client)
        {
            s.Write(hdr, 0, hdr.Length);
            s.Write(payload, 0, payload.Length);
            s.Flush();
        }
    }

    private static void WsSendText(TcpClient client, string text)
    {
        WsSend(client, OpText, Encoding.UTF8.GetBytes(text));
    }

    private static void Broadcast(string json)
    {
        List<TcpClient> snapshot;
        lock (ClientsLock) snapshot = new List<TcpClient>(Clients);

        foreach (TcpClient c in snapshot)
        {
            try { WsSendText(c, json); }
            catch { RemoveClient(c); }
        }
    }

    private static void RemoveClient(TcpClient c)
    {
        lock (ClientsLock) Clients.Remove(c);
        try { c.Close(); } catch { }
    }

    // ------------------------------------------------------------------
    // Minimal JSON helpers (avoid depending on a JSON lib beyond
    // JavaScriptSerializer, which ships with .NET Framework)
    // ------------------------------------------------------------------

    private static string JsonEscape(string s)
    {
        StringBuilder sb = new StringBuilder();
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    private static string Msg(string type, string extraKey, string extraVal)
    {
        string s = "{\"type\":\"" + JsonEscape(type) + "\"";
        if (extraKey != null) s += ",\"" + JsonEscape(extraKey) + "\":\"" + JsonEscape(extraVal) + "\"";
        return s + "}";
    }

    // ------------------------------------------------------------------
    // Clipboard watcher thread
    // ------------------------------------------------------------------

    private static void ClipboardWatcher()
    {
        uint last = GetClipboardSequenceNumber();
        while (true)
        {
            try
            {
                Thread.Sleep(400);
                uint now = GetClipboardSequenceNumber();
                if (now == last) continue;
                last = now;

                string text = GetClipboardText();
                if (string.IsNullOrEmpty(text)) continue;
                if (text == _lastSentText) continue;   // do not echo our own paste

                Log("剪贴板变化 -> 推给网页 (" + text.Length + " 字符)");
                Broadcast("{\"type\":\"clipboard\",\"text\":\"" + JsonEscape(text) + "\"}");
            }
            catch (Exception ex)
            {
                Log("剪贴板监听异常: " + ex.Message);
            }
        }
    }

    // ------------------------------------------------------------------
    // HTTP / WS connection handling
    // ------------------------------------------------------------------

    private static void HandleClient(object state)
    {
        TcpClient client = (TcpClient)state;
        try
        {
            client.NoDelay = true;
            NetworkStream stream = client.GetStream();

            // --- read request headers ---
            MemoryStream head = new MemoryStream();
            byte[] one = new byte[1];
            int matched = 0;
            while (matched < 4)
            {
                int n = stream.Read(one, 0, 1);
                if (n <= 0) { client.Close(); return; }
                head.WriteByte(one[0]);
                if ((matched == 0 || matched == 2) && one[0] == (byte)'\r') matched++;
                else if ((matched == 1 || matched == 3) && one[0] == (byte)'\n') matched++;
                else matched = 0;
                if (head.Length > 65536) { client.Close(); return; }
            }

            string headerText = Encoding.UTF8.GetString(head.ToArray());
            string requestLine = headerText.Split('\n')[0].Trim();
            string path = "/";
            string[] parts = requestLine.Split(' ');
            if (parts.Length >= 2) path = parts[1];

            bool isWs = headerText.IndexOf("Upgrade: websocket", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isWs)
            {
                HandleWebSocket(client, stream, headerText);
                return;
            }

            if (path.StartsWith("/shot"))
            {
                bool fromScreen = path.IndexOf("screen=1") >= 0;
                byte[] png = CaptureWeChat(fromScreen);
                if (png == null)
                {
                    byte[] err = Encoding.UTF8.GetBytes("no wechat window");
                    WriteHttp(stream, "503 Service Unavailable", "text/plain", err);
                }
                else
                {
                    WriteHttp(stream, "200 OK", "image/png", png);
                }
                client.Close();
                return;
            }

            if (path.StartsWith("/wincheck"))
            {
                // 调试用：对任意窗口句柄跑一遍"已登录"启发式，方便验证护栏本身。
                // 例: /wincheck?hwnd=198042
                IntPtr target = _wechatHwnd;
                int q = path.IndexOf("hwnd=");
                if (q >= 0)
                {
                    string num = path.Substring(q + 5).Split('&')[0];
                    long v;
                    if (long.TryParse(num, out v)) target = new IntPtr(v);
                }

                RECT rr = new RECT();
                if (target != IntPtr.Zero) GetWindowRect(target, out rr);
                int w = rr.Right - rr.Left;
                int h = rr.Bottom - rr.Top;
                int st = target != IntPtr.Zero ? GetWindowLong(target, GwlStyle) : 0;
                bool thick = (st & WsThickframe) != 0;

                string jj = "{\"hwnd\":" + target.ToInt64()
                          + ",\"w\":" + w + ",\"h\":" + h
                          + ",\"thickframe\":" + (thick ? "true" : "false")
                          + ",\"loggedIn\":" + (target != IntPtr.Zero && WeChatLooksLoggedIn(target) ? "true" : "false")
                          + "}";
                WriteHttp(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(jj));
                client.Close();
                return;
            }

            if (path.StartsWith("/status"))
            {
                EnsureWeChat();
                bool have = _wechatHwnd != IntPtr.Zero;
                string j = "{\"wechat\":" + (have ? "true" : "false")
                         + ",\"pid\":" + _wechatPid
                         + ",\"loggedIn\":" + (have && WeChatLooksLoggedIn(_wechatHwnd) ? "true" : "false")
                         + ",\"size\":\"" + (have ? WindowSizeText(_wechatHwnd) : "-") + "\""
                         + ",\"foreground\":" + (WeChatIsForeground() ? "true" : "false")
                         + ",\"clients\":" + Clients.Count + "}";
                WriteHttp(stream, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(j));
                client.Close();
                return;
            }

            // static
            if (File.Exists(_pagePath))
            {
                byte[] html = File.ReadAllBytes(_pagePath);
                WriteHttp(stream, "200 OK", "text/html; charset=utf-8", html);
            }
            else
            {
                WriteHttp(stream, "404 Not Found", "text/plain",
                          Encoding.UTF8.GetBytes("im.html not found next to the exe"));
            }
            client.Close();
        }
        catch (Exception ex)
        {
            Log("连接异常: " + ex.Message);
            try { client.Close(); } catch { }
        }
    }

    private static void WriteHttp(NetworkStream s, string status, string contentType, byte[] body)
    {
        string head = "HTTP/1.1 " + status + "\r\n"
                    + "Content-Type: " + contentType + "\r\n"
                    + "Content-Length: " + body.Length + "\r\n"
                    + "Access-Control-Allow-Origin: *\r\n"
                    + "Cache-Control: no-store\r\n"
                    + "Connection: close\r\n\r\n";
        byte[] hb = Encoding.ASCII.GetBytes(head);
        s.Write(hb, 0, hb.Length);
        s.Write(body, 0, body.Length);
        s.Flush();
    }

    private static void HandleWebSocket(TcpClient client, NetworkStream stream, string headerText)
    {
        string key = null;
        string[] lines = headerText.Split('\n');
        foreach (string line in lines)
        {
            int idx = line.IndexOf(':');
            if (idx <= 0) continue;
            string name = line.Substring(0, idx).Trim();
            string val = line.Substring(idx + 1).Trim();
            if (string.Equals(name, "Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = val;
        }
        if (key == null) { client.Close(); return; }

        byte[] sha;
        using (SHA1 sha1 = SHA1.Create())
            sha = sha1.ComputeHash(Encoding.ASCII.GetBytes(key + WsMagic));
        string accept = Convert.ToBase64String(sha);

        string resp = "HTTP/1.1 101 Switching Protocols\r\n"
                    + "Upgrade: websocket\r\n"
                    + "Connection: Upgrade\r\n"
                    + "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
        byte[] rb = Encoding.ASCII.GetBytes(resp);
        stream.Write(rb, 0, rb.Length);
        stream.Flush();

        lock (ClientsLock) Clients.Add(client);
        Log("WS 已连接 (" + Clients.Count + " 个)");

        try
        {
            WsSendText(client, Msg("hello", "backend", "wechat-clipboard"));
            WsSendText(client, Msg("status", "wechat", WeChatState()));

            JavaScriptSerializer ser = new JavaScriptSerializer();
            while (true)
            {
                byte[] frame = WsReceive(stream);
                int opcode = frame[0];
                byte[] payload = new byte[frame.Length - 1];
                Buffer.BlockCopy(frame, 1, payload, 0, payload.Length);

                if (opcode == OpClose) break;
                if (opcode == OpPing) { WsSend(client, OpPong, payload); continue; }
                if (opcode != OpText) continue;

                string text = Encoding.UTF8.GetString(payload);
                Log("收到文本帧 " + payload.Length + " 字节 opcode=" + opcode + " : "
                    + (text.Length > 90 ? text.Substring(0, 90) + "..." : text));

                Dictionary<string, object> msg = null;
                try { msg = ser.Deserialize<Dictionary<string, object>>(text); }
                catch (Exception jex) { Log("JSON 解析失败: " + jex.Message); }

                if (msg == null) { Log("JSON 解析结果为空，丢弃这一帧"); continue; }
                object t;
                if (!msg.TryGetValue("type", out t)) continue;
                string type = Convert.ToString(t);

                if (type == "send")
                {
                    object bodyObj;
                    string body = msg.TryGetValue("text", out bodyObj) ? Convert.ToString(bodyObj) : "";

                    Log("发送 -> 微信 (" + (body == null ? 0 : body.Length) + " 字符)");
                    string result = SendToWeChat(body);
                    Log("结果: " + result);

                    WsSendText(client, "{\"type\":\"sent\",\"ok\":" + (result == "ok" ? "true" : "false")
                                     + ",\"detail\":\"" + JsonEscape(result) + "\"}");
                }
                else if (type == "status")
                {
                    EnsureWeChat();
                    WsSendText(client, Msg("status", "wechat", WeChatState()));
                }
            }
        }
        catch (Exception ex)
        {
            Log("WS 断开: " + ex.Message);
        }
        finally
        {
            RemoveClient(client);
            Log("WS 已断开 (" + Clients.Count + " 个)");
        }
    }

    // ------------------------------------------------------------------
    // Main
    // ------------------------------------------------------------------

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; }
        catch { }

        int port = Port;
        bool openBrowser = true;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--port" && i + 1 < args.Length) port = int.Parse(args[i + 1], CultureInfo.InvariantCulture);
            if (args[i] == "--no-open") openBrowser = false;
        }

        // 页面不是必须的：chat.html 用 file:// 直接打开也能连上这个 WebSocket。
        // 所以这里只是"有就顺便托管并自动开浏览器"，没有也照样工作。
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string[] candidates = new string[] { "chat.html", "im.html" };
        for (int i = 0; i < candidates.Length; i++)
        {
            string p = Path.Combine(baseDir, candidates[i]);
            if (File.Exists(p)) { _pagePath = p; break; }
        }
        if (_pagePath == null) _pagePath = Path.Combine(baseDir, "im.html");
        bool havePage = File.Exists(_pagePath);

        Log("WeChat 剪贴板桥  (.NET Framework / C# 5)");
        Log("WebSocket  ws://" + Host + ":" + port + "/ws");
        if (havePage)
            Log("页面   " + _pagePath);
        else
            Log("页面   无（不影响使用，直接双击 chat.html 即可）");

        FindWeChat();
        if (_wechatHwnd == IntPtr.Zero)
            Log("警告: 没找到微信窗口，请先把微信主窗口打开");
        else
            Log("微信窗口已就绪");

        Thread watcher = new Thread(ClipboardWatcher);
        watcher.IsBackground = true;
        watcher.Start();

        TcpListener listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Log("监听中...  Ctrl+C 退出");

        if (openBrowser)
        {
            if (havePage)
            {
                try { Process.Start("http://" + Host + ":" + port + "/"); }
                catch { }
            }
            else
            {
                Log("没有可托管的页面，不自动开浏览器。");
                Log("请直接双击你手上的 chat.html —— 用 file:// 打开一样能连上。");
            }
        }

        while (true)
        {
            TcpClient c = listener.AcceptTcpClient();

            // 必须有发送超时。默认 0 = 无限等，往死连接写会永远挂住，
            // 上面 WsSend 的注释里描述的那场事故就是这么来的。
            c.SendTimeout = 3000;
            c.NoDelay = true;

            Thread t = new Thread(HandleClient);
            t.IsBackground = true;
            t.Start(c);
        }
    }
}
