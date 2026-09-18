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
using LocalChat;          // wxdb.cs：从微信进程内存里读解密后的 SQLite 页

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
    private const int SwShow = 5;
    private const ushort VkControl = 0x11, VkV = 0x56, VkReturn = 0x0D;
    private const ushort VkC = 0x43, VkEscape = 0x1B;
    private const uint KeyeventfKeyup = 0x0002;

    private static readonly object ClientsLock = new object();
    private static readonly List<TcpClient> Clients = new List<TcpClient>();

    private static string _pagePath;
    private static string _lastSentText = "";

    // 我们最后一次往剪贴板写内容之后的剪贴板序号。
    // 用它精确识别"这一次剪贴板变化是桥自己造成的"，见 ClipboardWatcher 的注释。
    private static uint _lastSentSeq = 0;
    private static IntPtr _wechatHwnd = IntPtr.Zero;
    private static int _wechatPid = 0;

    // 自动拉取：微信因为收到新消息把自己切到前台时，自动拉一次。
    private static volatile bool _autoPull = true;
    private static int _autoPullPending = 0;
    private static DateTime _lastAutoPull = DateTime.MinValue;
    private static DateTime _autoPullQuietUntil = DateTime.MinValue;
    private static string _lastPullNote = "从未拉取";
    private static DateTime _suppressClipboardUntil = DateTime.MinValue;

    // ------------------------------------------------------------------
    // 读取方式
    //
    // mem  = 从微信进程内存里读 SQLCipher 解密后的 SQLite 页（默认）
    // clip = 旧的"拖长方形 + Ctrl+C"路径，留作退路
    // ------------------------------------------------------------------
    private static string _readMode = "mem";

    // 锁定的会话；空 = 自动（任何有动静的消息表都读）。
    // 两种绑定方式：
    //   _sessionId = 对方的 wxid / 群号，表名 = "Msg_" + md5(会话id)
    //   _msgTable  = 直接指定 Msg_<md5> 表名（页面上的会话列表用这个）
    private static string _sessionId = "";
    private static string _msgTable = "";

    // 每个消息表已处理到的最大 local_id，用来只挑真正新增的行。
    private static readonly Dictionary<string, long> _msgWatermark =
        new Dictionary<string, long>();

    // 桥启动时刻（Unix 秒）。只有"启动之后到达"的消息才算新消息 ——
    // 这样启动时不会把页缓存里的旧消息当成新消息刷出来，
    // 也不需要预先给一百多个会话各打一次水位。
    private static long _startedAt = 0;

    // 内存模式下"微信是否可用"的判据：能不能读到会话库。
    // 不能再用窗口尺寸判断 —— 微信收进托盘或最小化时窗口是 237x39，
    // 那时候 WeChatLooksLoggedIn 会误报未登录，而内存读取根本不需要窗口。
    private static bool _lastReadSawSession = false;

    // 会话库 WAL 的指纹。它一变就说明有写入，才值得去抓一次内存快照
    // （抓一次 253MB 约 0.4 秒，不能无脑轮询）。
    private static string _walPath = null;
    private static long _walLen = -1;
    private static DateTime _walTime = DateTime.MinValue;

    // 公众号等"永远在动"的会话会把"最近会话"顶掉，读取时排除。
    private static readonly string[] _noiseSessions = {
        "brandsessionholder", "brandservicesessionholder",
        "@placeholder_foldgroup", "filehelper_placeholder"
    };

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
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint f, uint dx, uint dy, uint d, IntPtr e);
    private const uint MeLeftdown = 0x0002, MeLeftup = 0x0004;
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
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
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG m, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc cb,
                                                 uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public POINT pt;
    }

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd,
                                       int idObject, int idChild, uint thread, uint time);

    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutofcontext = 0x0000;
    private const uint PmRemove = 0x0001;
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

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

    // 慢路径扫描用的状态（EnumWindows 的回调没法带闭包，只能放静态字段）
    private static uint _scanPid;
    private static IntPtr _scanBest;
    private static long _scanBestArea;
    private static bool _scanLoggedInOnly;

    private static bool ScanWindow(IntPtr h, IntPtr l)
    {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if (pid != _scanPid) return true;

        if (ClassOf(h).IndexOf("Qt", StringComparison.Ordinal) < 0) return true;

        RECT r;
        if (!GetWindowRect(h, out r)) return true;
        int w = r.Right - r.Left, hh = r.Bottom - r.Top;
        long area = (long)w * hh;

        bool ok = WeChatLooksLoggedIn(h);
        Log("  候选窗口 hwnd=" + h.ToInt64() + " 尺寸=" + w + "x" + hh
            + " 已登录=" + (ok ? "是" : "否"));

        if (_scanLoggedInOnly && !ok) return true;
        if (area > _scanBestArea) { _scanBestArea = area; _scanBest = h; }
        return true;
    }

    private static void FindWeChat()
    {
        Process[] procs = Process.GetProcessesByName("Weixin");
        if (procs.Length == 0) procs = Process.GetProcessesByName("WeChat");

        // 快路径：窗口正常可见时 MainWindowHandle 就是它
        foreach (Process p in procs)
        {
            IntPtr h = p.MainWindowHandle;
            if (h != IntPtr.Zero)
            {
                _wechatHwnd = h;
                _wechatPid = p.Id;
                Log("found WeChat: pid=" + p.Id + " class=" + ClassOf(h));
                return;
            }
        }

        // 慢路径：窗口被收进托盘时 MainWindowHandle 会是 0，
        // 快路径整个失效（真踩过：这样会导致"找不到微信"，发送和拉取全废）。
        // 枚举这些进程的所有顶层窗口，挑面积最大的那个 Qt 窗口 ——
        // 主窗口很大，那些消息辅助窗口都是 0 尺寸。
        foreach (Process p in procs)
        {
            // 先只挑"看起来已登录"的窗口（尺寸够大 + 可缩放边框）。
            // 微信还有别的 Qt 顶层窗口（实测有个 1920x1026 的辅助窗口），
            // 光按面积挑会挑错，于是被判成"停在登录界面"。
            _scanPid = (uint)p.Id;
            _scanBest = IntPtr.Zero;
            _scanBestArea = 0;
            _scanLoggedInOnly = true;
            EnumWindows(ScanWindow, IntPtr.Zero);

            if (_scanBest == IntPtr.Zero)
            {
                // 都不过关就退回"面积最大"，至少别找不到窗口
                Log("  没有看起来已登录的 Qt 窗口，退回按面积挑");
                _scanBestArea = 0;
                _scanLoggedInOnly = false;
                EnumWindows(ScanWindow, IntPtr.Zero);
            }

            if (_scanBest != IntPtr.Zero)
            {
                _wechatHwnd = _scanBest;
                _wechatPid = p.Id;
                RECT r;
                GetWindowRect(_scanBest, out r);
                Log("found WeChat（窗口被隐藏，走慢路径）: pid=" + p.Id
                    + " class=" + ClassOf(_scanBest)
                    + " 尺寸=" + (r.Right - r.Left) + "x" + (r.Bottom - r.Top));
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
        // 内存模式下"能不能用"和窗口无关：微信收进托盘、最小化、甚至
        // 窗口尺寸只报 237x39，都照样能读库。窗口尺寸那道判断是给
        // 剪贴板模式用的（它必须切窗口、必须窗口够大才不是登录界面）。
        if (_readMode == "mem") return _wechatPid != 0 ? "ready" : "not-found";
        return WeChatLooksLoggedIn(_wechatHwnd) ? "ready" : "not-logged-in";
    }

    private static bool ActivateWeChat()
    {        EnsureWeChat();
        if (_wechatHwnd == IntPtr.Zero) return false;

        if (IsIconic(_wechatHwnd)) ShowWindow(_wechatHwnd, SwRestore);

        // 还要处理"被收进托盘隐藏"这种情况 —— 那时 IsIconic 是 false，
        // 光靠上面那句救不回来。真踩过：窗口句柄还在但 vis=False，
        // 于是切前台失败，发送和拉取全部失效。
        if (!IsWindowVisible(_wechatHwnd)) ShowWindow(_wechatHwnd, SwShow);

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

    // 拖选几何（见 PullMessages 里 2026-09-18 的修正说明）
    private const int PullXOuter = 30;      // 起点：距右边缘，落在头像右缘
    private const int PullXInner = 70;      // 终点：距右边缘，落在气泡右缘
    private const int PullHeight = 300;     // 从最下面往上拉多高（越小越不打扰）
    private const int PullBottomInset = 110;// 距下边缘：输入框正上方
    private const int PullTopMargin = 120;  // 离顶至少留这么多 —— 碰到顶微信会把列表翻上去

    /// <summary>
    /// 拉取当前对话里可见消息的纯文本（拿不到返回 null）。
    ///
    /// 原理：微信 4.x 的消息区是一整块自绘画布，UIA 完全读不到
    /// （连开了无障碍桥也只有 1 个元素）。但**鼠标在消息区拖一下
    /// 会形成一次文字选择**，再按 Ctrl+C，微信就把选中的消息以纯文本
    /// 放进剪贴板：
    ///     发送者\n2026年09月18日 17:05\n内容\n\n（下一条…）
    /// 这是唯一走得通的自动读取路径 —— 键盘那条（Ctrl+A 全选）微信压根没有，
    /// 实测 8 种挪焦点的办法全部失败。
    ///
    /// 坐标**不按窗口比例算**：左侧图标栏和会话列表是固定像素宽的
    /// （约 390px），按比例在窄窗口下会掉进会话列表里（窗口宽 800 时
    /// x=0.40 只有 320px，已经在会话列表上了）。改成从右边缘/下边缘算
    /// 固定内缩，就跟窗口宽度无关了。窗口**移动**本来就无所谓 ——
    /// 每次都重新 GetWindowRect，SetCursorPos 用的是绝对屏幕坐标。
    ///
    /// 三道闸跟发送时完全一样：任一不过，一个键都不按。
    ///
    /// 【2026-09-18 修正】拖的必须是**细长方形**，不能是一条竖线。
    /// 实测（临时探针 probe.exe，每种拖法都比对剪贴板序号）：
    ///     零宽度的竖线 right-51 ...... 剪贴板序号根本没变 —— 什么都没选中，
    ///                                  读到的是上一次留在剪贴板里的旧内容
    ///     细长方形 right-30->right-70  每次都更新（FRESH），可重复
    /// 也就是说在这之前"自动拉取成功"有一部分是在读旧剪贴板。
    ///
    /// x 取 30..70：实测这个窗口里自己的头像列就在距右边缘 31..66 px，
    /// 气泡文字的右边缘在 79 px 处。取 30..70 正好盖住头像列、压不到文字，
    /// 跟"沿右侧头像处拖"这个要求一致（窗口 1115 宽时量的）。
    /// </summary>
    private static string PullMessages(string why, bool keepClipboard)
    {
        // 先把用户当前的前台窗口和剪贴板留一份底，拉完要还回去。
        IntPtr prevFg = GetForegroundWindow();
        bool wechatWasFg = prevFg == _wechatHwnd;
        string prevClip = keepClipboard ? GetClipboardText() : null;

        if (!ActivateWeChat()) { Log("拉取失败: 无法把微信切到前台，已中止"); return null; }
        if (!WeChatIsForeground()) { Log("拉取失败: 前台校验未通过，已中止"); return null; }
        if (!WeChatLooksLoggedIn(_wechatHwnd)) { Log("拉取失败: 微信看起来停在登录界面，已中止"); return null; }

        // 从**最下面**往上拉一小段，绝不碰到顶部。
        //
        // 踩过：原来是从上边缘 +70 一直拉到下边缘 -170，也就是顶着标题栏拖。
        // 那样微信会**自动把会话列表往上翻**，翻到很久以前 —— 于是每次拉到的
        // 都是不同的一屏（实测同一套代码一会儿 866 字符一会儿 1320 字符），
        // 而且把用户正在看的对话位置也带跑了。
        //
        // 现在只拉底部 PullHeight 这么高，并且离顶至少 PullTopMargin。
        // 实测（探针 probe.exe bot，连拉两次不重置，看第一条是不是同一个）：
        //     高度 300 -> 4 条，两次第一条完全一样（没翻）
        //     高度 420 -> 6 条，两次一样
        //     高度 560 -> 7 条，两次一样
        // 取最小的 300：反正页面只处理最近 5 条，信息越少越不容易出事。
        string pulled = null;
        RECT wr;
        int[] botTries = { PullBottomInset, PullBottomInset + 130, PullBottomInset + 280 };
        foreach (int bt in botTries)
        {
            EnsureWeChat();
            if (_wechatHwnd == IntPtr.Zero) break;
            GetWindowRect(_wechatHwnd, out wr);

            int yBot = wr.Bottom - bt;                  // 最下面：输入框正上方
            int yTop = yBot - PullHeight;               // 只往上拉这么一点
            int floor = wr.Top + PullTopMargin;         // 绝不到顶
            if (yTop < floor) yTop = floor;

            if (yBot <= yTop + 40) continue;            // 消息区太矮，别拖了
            pulled = DragAndCopy(wr.Right - PullXOuter, yBot, wr.Right - PullXInner, yTop);
            if (LooksLikeDump(pulled)) break;
        }

        string preview = pulled == null ? "" : pulled.Replace("\r", " ").Replace("\n", " ");
        if (preview.Length > 70) preview = preview.Substring(0, 70);
        _lastPullNote = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + why + "拉取"
                      + (pulled == null ? "没读到内容" : "拿到 " + pulled.Length + " 字符")
                      + (preview.Length == 0 ? "" : " | " + preview);
        Log("拉取消息(" + why + "): " + (pulled == null ? "剪贴板没变化（消息区是空的？）"
                                                        : pulled.Length + " 字符"));

        // 自动拉取不抢用户的剪贴板：把原来的内容写回去。
        // 只在剪贴板里还是我们刚放进去的东西时才还原，避免踩掉用户此刻的复制。
        if (keepClipboard && prevClip != null && GetClipboardText() == pulled)
        {
            if (SetClipboardText(prevClip))
            {
                // 还原本身也会改剪贴板序号，得认成"自己造成的"，
                // 否则剪贴板监听线程会把还原的内容当成用户复制推给页面。
                _lastSentSeq = GetClipboardSequenceNumber();
                _lastSentText = prevClip;
            }
        }

        // 自动拉取不抢焦点：把用户原来的前台窗口还回去。
        if (!wechatWasFg) RestoreForeground(prevFg);

        return pulled;
    }

    /// <summary>把前台还给用户原来的窗口（拉取结束后用）。</summary>
    private static void RestoreForeground(IntPtr target)
    {
        if (target == IntPtr.Zero || target == _wechatHwnd) return;
        if (!IsWindow(target)) return;

        uint dummy;
        uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out dummy);
        uint ourThread = GetCurrentThreadId();
        bool attached = false;
        try
        {
            if (fgThread != ourThread) attached = AttachThreadInput(fgThread, ourThread, true);
            SetForegroundWindow(target);
        }
        catch { }
        finally { if (attached) AttachThreadInput(fgThread, ourThread, false); }
    }

    /// <summary>
    /// 那个像素真的属于微信吗？
    ///
    /// 第四道闸。踩过：微信不是最前面那个窗口时，光标底下其实是 Solid Edge，
    /// 一拖就在别人的模型上拉了个框。鼠标拖拽是按屏幕坐标打到**最上面**那个
    /// 窗口上的，所以拖之前必须先问一句 WindowFromPoint。
    /// </summary>
    private static bool PixelBelongsToWeChat(int x, int y)
    {
        POINT p; p.X = x; p.Y = y;
        IntPtr h = WindowFromPoint(p);
        if (h == IntPtr.Zero) return false;

        uint pid;
        GetWindowThreadProcessId(h, out pid);
        if (_wechatPid != 0 && pid == (uint)_wechatPid) return true;

        Log("拉取中止: 像素(" + x + "," + y + ")属于 hwnd=" + h + " pid=" + pid
            + " class=" + ClassOf(h) + "，不是微信");
        return false;
    }

    // ------------------------------------------------------------------
    // 自动拉取：等微信自己把窗口切到前台，再去读
    //
    // 实测（临时探针 probe.exe events，用户从手机发消息）：
    //   18:05:12 HOOK ev=0x0003 pid=19576 hwnd=263490 class=Qt51514QWindowIcon title=微信
    //   18:05:13 FG wechat foreground=True
    // 微信 4.x 收到消息**不走 Windows 通知中心**，全程没有新建任何 toast 窗口，
    // 而是直接把自己的主窗口激活到前台。所以能挂钩子的信号就是
    // EVENT_SYSTEM_FOREGROUND 打在微信自己的窗口上。
    //
    // 这样一来"收到消息后自动拉取"并不额外抢焦点 —— 微信已经先抢了。
    // 我们只做三件不打扰的事：拉完把前台还给用户原来的窗口、把光标放回原处、
    // 把剪贴板写回原来的内容。
    //
    // 另外还挂了一道自锁：拉取本身会把微信切到前台，那也会触发这个事件，
    // 所以拉取前后各安静一段时间，不然会自己触发自己。
    // ------------------------------------------------------------------

    // 这个静态字段是必须的，不是洁癖。
    //
    // SetWinEventHook 只拿走一个函数指针，托管侧那个隐式生成的委托
    // 没有任何东西引用它 —— 一次 GC 之后就被回收，钩子从此再也不回调，
    // 而且不报任何错。实测就是这样：启动后触发过一次，之后彻底安静。
    // 放在静态字段里让 GC 一直看得见它。
    private static readonly WinEventProc _foregroundHookProc = WeChatForegroundHook;

    private static void WeChatForegroundHook(IntPtr hook, uint ev, IntPtr hwnd,
                                            int idObject, int idChild, uint thread, uint time)
    {
        if (!_autoPull) return;
        if (ev != EventSystemForeground) return;
        if (DateTime.Now < _autoPullQuietUntil) return;

        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        if (_wechatPid == 0 || pid != (uint)_wechatPid) return;

        Interlocked.Exchange(ref _autoPullPending, 1);
        Log("自动拉取: 微信切到前台，已排队");
    }

    // ------------------------------------------------------------------
    // 从微信进程内存里读消息
    //
    // 微信 4.x 的库是 SQLCipher 加密的，但 SQLCipher 每解一页就把明文放进
    // SQLite 的页缓存，所以明文本来就在进程堆上。WxSnapshot 顺着 pcache1 的
    // PgHdr1 把页缓存枚举出来就能当普通 SQLite 读 —— 不需要密钥，也就不用
    // 去暴力破解（PBKDF2 256000 轮是 170ms 一次，根本不现实）。
    //
    // 代价：只能看到页缓存里还有的页。所以它的正确用法就是"消息一到就去读"，
    // 刚写入的页必然还在缓存里。
    // ------------------------------------------------------------------

    /// <summary>
    /// 所有库 WAL 的位置（用 | 连接），用来判断"有没有新写入"。
    /// 现在读的是消息表，所以消息库的 WAL 是关键触发源。
    /// </summary>
    private static string FindSessionWal()
    {
        try
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string root = Path.Combine(docs, "xwechat_files");
            if (!Directory.Exists(root)) return null;
            List<string> found = new List<string>();
            foreach (string d in Directory.GetDirectories(root))
            {
                string ds = Path.Combine(d, "db_storage");
                if (!Directory.Exists(ds)) continue;
                foreach (string sub in Directory.GetDirectories(ds))
                {
                    foreach (string f in Directory.GetFiles(sub, "*.db-wal"))
                        found.Add(f);
                }
            }
            if (found.Count == 0) return null;
            return string.Join("|", found.ToArray());
        }
        catch { return null; }
    }

    /// <summary>会话库 WAL 变了没有。抓一次快照要 0.4 秒，不能无脑轮询。</summary>
    private static bool WalChanged()
    {
        if (_walPath == null) _walPath = FindSessionWal();
        if (_walPath == null) return true;      // 找不到就每次都读
        try
        {
            long len = 0;
            DateTime t = DateTime.MinValue;
            string[] parts = _walPath.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length == 0) continue;
                FileInfo fi = new FileInfo(parts[i]);
                if (!fi.Exists) continue;
                len += fi.Length;
                if (fi.LastWriteTime > t) t = fi.LastWriteTime;
            }
            if (len != _walLen || t != _walTime)
            {
                _walLen = len;
                _walTime = t;
                return true;
            }
        }
        catch { return true; }
        return false;
    }

    /// <summary>公众号之类"永远在动"的会话会把"最近会话"顶掉，排除掉。</summary>
    private static bool IsNoiseSession(string u)
    {
        if (u == null) return true;
        if (u.StartsWith("gh_", StringComparison.Ordinal)) return true;
        if (u.EndsWith("@openim", StringComparison.Ordinal)) return true;
        for (int i = 0; i < _noiseSessions.Length; i++)
            if (u == _noiseSessions[i]) return true;
        return false;
    }

    private static string Stamp()
    {
        return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>非文本消息给个占位，免得正文是空的让页面以为解析失败。</summary>
    private static string TypePlaceholder(long t)
    {
        // 有些消息的 local_type 是合成的：高位放子类型、低 32 位才是基本类型。
        // 实测 244813135921 = (57&lt;&lt;32)|49、21474836529 = (5&lt;&lt;32)|49，都是 49。
        if (t > 10000) t = t & 0xFFFFFFFFL;
        if (t == 3) return "[图片]";
        if (t == 34) return "[语音]";
        if (t == 43) return "[视频]";
        if (t == 47) return "[表情]";
        if (t == 48) return "[位置]";
        if (t == 49) return "[链接或卡片]";
        if (t == 50) return "[通话]";
        if (t == 10000) return "[系统消息]";
        return "[类型 " + t + "]";
    }

    /// <summary>
    /// 读一次消息，产出和剪贴板路径**完全一样**的纯文本，页面端不用改：
    ///     发送者
    ///     2026年09月18日 17:17
    ///     正文
    /// 空行分隔。没有新消息返回 null。
    /// </summary>
    /// <param name="catchUp">手动拉取：忽略"启动之后才算新"的限制，直接给最近几条。</param>
    private static string MemPull(string why, bool catchUp, out int total, out int fresh)
    {
        total = 0;
        fresh = 0;
        EnsureWeChat();
        if (_wechatPid == 0)
        {
            _lastPullNote = Stamp() + " " + why + "拉取失败：没找到微信进程";
            return null;
        }

        WxSnapshot snap;
        try { snap = WxSnapshot.CaptureAuto(_wechatPid); }
        catch (Exception ex)
        {
            _lastPullNote = Stamp() + " " + why + "拉取失败：" + ex.Message;
            return null;
        }

        // 以"消息表"为准，不再用会话表：会话表只缓存得到一部分
        // （实测 153 个会话里只有 53 个读得到），拿它判断"哪个会话有动静"
        // 根本不可靠 —— 这就是"朋友的对话总是识别不到"的直接原因。
        //
        // 消息库的 sqlite_master 里有 80 多张 Msg_<md5> 表，每张表只看
        // b-tree 最右叶子就能拿到它最新的几行，代价是 O(树深) 而不是全表。
        List<string> tables = new List<string>();
        if (_msgTable.Length > 0) tables.Add(_msgTable);
        else if (_sessionId.Length > 0) tables.Add("Msg_" + WxSnapshot.Md5Hex(_sessionId));
        else tables = snap.MessageTables();
        if (tables.Count == 0)
        {
            _lastPullNote = Stamp() + " " + why + "：页缓存里没有消息表";
            return null;
        }

        List<PullItem> take = new List<PullItem>();
        List<string> hits = new List<string>();
        int readable = 0;
        for (int ti = 0; ti < tables.Count; ti++)
        {
            string tbl = tables[ti];
            string note;
            List<WxMessage> tail = snap.TableTail(tbl, 6, out note);
            if (tail.Count == 0 && tables.Count == 1 && _sessionId.Length > 0)
            {
                // 根页不在页缓存里：拿会话行的 last_msg_locald_id 当锚点，去内存
                // 页映像里认那一页，再顺着 rowid 往前接几页 —— 这条路不需要根页。
                long anchor = 0;
                foreach (WxSession s in snap.Sessions())
                    if (s.UserName == _sessionId) { anchor = s.LastMsgLocalId; break; }
                if (anchor > 0)
                {
                    string how2;
                    tail = snap.LeafChain(tbl, anchor, 3, 1, out how2);
                    note = how2;
                }
            }
            if (tail.Count == 0) continue;
            readable++;
            total += tail.Count;

            long mark;
            _msgWatermark.TryGetValue(tbl, out mark);
            long maxId = mark;
            int got = 0;
            for (int i = 0; i < tail.Count; i++)      // 行序 = rowid 升序
            {
                WxMessage m = tail[i];
                if (m.LocalId > maxId) maxId = m.LocalId;
                if (!catchUp)
                {
                    if (m.LocalId <= mark) continue;
                    if (m.CreateTime < _startedAt) continue;
                }
                PullItem it = new PullItem();
                it.Msg = m;
                it.Who = tbl.Substring(4, 8);
                take.Add(it);
                got++;
            }
            _msgWatermark[tbl] = maxId;
            if (got > 0) hits.Add(tbl.Substring(4, 8) + "×" + got);
        }
        _lastReadSawSession = readable > 0;
        fresh = take.Count;
        if (fresh == 0)
        {
            _lastPullNote = Stamp() + " " + why + "：没有新消息（可读消息表 "
                          + readable + "/" + tables.Count + "）";
            return null;
        }

        take.Sort(delegate(PullItem a, PullItem b)
        {
            return a.Msg.CreateTime.CompareTo(b.Msg.CreateTime);
        });
        if (take.Count > 5) take.RemoveRange(0, take.Count - 5);   // 只留最新 5 条

        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < take.Count; i++)
        {
            PullItem it = take[i];
            WxMessage m = it.Msg;
            string body = string.IsNullOrEmpty(m.Text) ? TypePlaceholder(m.LocalType) : m.Text;
            // 群消息的正文里本来就带"发送者wxid:"前缀（微信自己写的）。
            // 这里再补一行发送者，纯粹为了和剪贴板路径的输出格式一致 ——
            // 页面那边的 parseWeChatDump 会把这一行连同日期行一起丢掉。
            sb.Append(it.Who);
            sb.Append('\n');
            sb.Append(new DateTime(1970, 1, 1).AddSeconds(m.CreateTime).ToLocalTime()
                      .ToString("yyyy年MM月dd日 HH:mm", CultureInfo.InvariantCulture));
            sb.Append('\n');
            sb.Append(body);
            sb.Append('\n');
            sb.Append('\n');
        }
        _lastPullNote = Stamp() + " " + why + "拉取：拿到 " + take.Count + " 条"
                      + "（" + string.Join("、", hits.ToArray()) + "）";
        return sb.ToString();
    }

    /// <summary>一条待输出的消息，连同它的会话标签。</summary>
    private sealed class PullItem
    {
        public WxMessage Msg;
        public string Who;
    }

    private sealed class TableEntry
    {
        public long Time;
        public string Json;
    }

    /// <summary>
    /// 会话选择列表：**以最新消息为准**，只列最近有动静的对话（3–5 个）。
    ///
    /// 两条路并用，缺一不可：
    ///   1. 会话行（SessionTable）给出每个会话的 last_msg_locald_id —— 表名
    ///      可以由 username 直接算出来（Msg_ + md5(username)），所以会话行里
    ///      有的对话都能列出来，不必先找到它的表根页。
    ///   2. 消息表能走通就走表（最准）；走不通就按 last_msg_locald_id 去内存
    ///      页映像里认那一页，把最新那条读出来；再不行才回落成会话摘要。
    /// </summary>
    private const int SessionsMin = 3;
    private const int SessionsMax = 5;

    private static string TablesJson()
    {
        try
        {
            EnsureWeChat();
            if (_wechatPid == 0) return "[]";
            WxSnapshot snap = WxSnapshot.CaptureAuto(_wechatPid);
            List<TableEntry> list = new List<TableEntry>();
            HashSet<string> seen = new HashSet<string>();
            HashSet<string> noiseMd5 = new HashSet<string>();
            List<WxSession> sessions = snap.Sessions();
            foreach (WxSession s in sessions)
                if (IsNoiseSession(s.UserName))
                    noiseMd5.Add(WxSnapshot.Md5Hex(s.UserName));

            foreach (WxSession s in sessions)
            {
                // 公众号 / openim / brandsessionholder 这类"永远在动"的会话会把
                // 最近 3–5 个名额全占了，选择列表里不列它们。
                if (IsNoiseSession(s.UserName)) continue;
                string tbl = "Msg_" + WxSnapshot.Md5Hex(s.UserName);
                string how;
                WxMessage m = snap.NewestFor(tbl, s.LastMsgLocalId, s.LastTimestamp, out how);
                long tm = m != null ? m.CreateTime : s.LastTimestamp;
                long ty = m != null ? m.LocalType : s.LastMsgType;
                string prev = m != null ? m.Text : s.Summary;
                if (string.IsNullOrEmpty(prev))
                {
                    // 最新那条是卡片/图片这类没正文的：拿它前面最近一条有正文的
                    // 当提示。列表是给人认的，"最近在说什么"比"是什么类型"有用。
                    string note2;
                    List<WxMessage> t2 = snap.MessageTail(tbl, 8, out note2);
                    for (int i = t2.Count - 1; i >= 0; i--)
                        if (!string.IsNullOrEmpty(t2[i].Text)) { prev = t2[i].Text; break; }
                }
                if (string.IsNullOrEmpty(prev)) prev = TypePlaceholder(ty);
                list.Add(Entry(tbl, s.UserName, tm, ty, prev, how));
                seen.Add(tbl);
            }

            // 会话行里没有、但消息表读得出来的，也补上（比如刚建的会话）
            foreach (string tbl in snap.MessageTables())
            {
                if (seen.Contains(tbl)) continue;
                if (noiseMd5.Contains(tbl.Substring(4))) continue;
                string note;
                List<WxMessage> tail = snap.TableTail(tbl, 2, out note);
                if (tail.Count == 0) continue;
                WxMessage last = tail[tail.Count - 1];
                string prev = last.Text;
                if (string.IsNullOrEmpty(prev)) prev = TypePlaceholder(last.LocalType);
                list.Add(Entry(tbl, "", last.CreateTime, last.LocalType, prev, "表"));
                seen.Add(tbl);
            }

            list.Sort(delegate(TableEntry a, TableEntry b)
            {
                return b.Time.CompareTo(a.Time);
            });
            if (list.Count > SessionsMax) list.RemoveRange(SessionsMax, list.Count - SessionsMax);

            List<string> js = new List<string>();
            for (int i = 0; i < list.Count; i++) js.Add(list[i].Json);
            _lastListNote = list.Count + " 个（要 3–5 个）";
            return "[" + string.Join(",", js.ToArray()) + "]";
        }
        catch (Exception ex)
        {
            Log("会话列表失败: " + ex.Message);
            return "[]";
        }
    }

    private static string _lastListNote = "";

    private static TableEntry Entry(string table, string who, long time, long type,
                                    string preview, string how)
    {
        if (preview != null && preview.Length > 60) preview = preview.Substring(0, 60);
        TableEntry e = new TableEntry();
        e.Time = time;
        e.Json = "{\"table\":\"" + table + "\",\"md5\":\"" + table.Substring(4)
               + "\",\"who\":\"" + JsonEscape(who == null ? "" : who)
               + "\",\"time\":" + time + ",\"type\":" + type
               + ",\"how\":\"" + JsonEscape(how == null ? "" : how)
               + "\",\"preview\":\"" + JsonEscape(preview == null ? "" : preview)
               + "\"}";
        return e;
    }

    /// <summary>手动拉取：两种读取方式共用入口。</summary>
    private static string ManualPull()
    {
        if (_readMode == "clip") return PullMessages("手动", false);
        int total, fresh;
        return MemPull("手动", true, out total, out fresh);
    }

    private static void AutoPullLoop()
    {
        IntPtr hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero,
                                      _foregroundHookProc, 0, 0, WineventOutofcontext);
        if (hook == IntPtr.Zero) Log("自动拉取: 前台钩子没挂上（不影响轮询）");
        Log("自动拉取: 已开启（内存读取；会话库有写入就去读一次）");

        MSG msg;
        DateTime lastCheck = DateTime.MinValue;
        while (true)
        {
            // out-of-context 的钩子是把事件当消息投到这个线程的队列里的，
            // 不抽消息回调永远不会被调用。
            while (PeekMessage(out msg, IntPtr.Zero, 0, 0, PmRemove))
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            bool forced = Interlocked.Exchange(ref _autoPullPending, 0) == 1;
            bool due = (DateTime.Now - lastCheck).TotalMilliseconds >= 1500;

            if (_autoPull && (forced || (due && WalChanged())))
            {
                lastCheck = DateTime.Now;
                if (DateTime.Now - _lastAutoPull >= TimeSpan.FromMilliseconds(700))
                {
                    _lastAutoPull = DateTime.Now;
                    _autoPullQuietUntil = DateTime.Now.AddSeconds(4);

                    int total, fresh;
                    string text = _readMode == "clip"
                        ? PullMessages("自动", true)
                        : MemPull("自动", false, out total, out fresh);
                    Log(_lastPullNote);

                    if (text != null && (_readMode == "clip" ? LooksLikeDump(text) : text.Length > 0))
                    {
                        Broadcast("{\"type\":\"pulled\",\"ok\":true,\"auto\":true,\"len\":"
                                  + text.Length + ",\"text\":\"" + JsonEscape(text) + "\"}");
                    }
                }
            }
            Thread.Sleep(120);
        }
    }

    /// <summary>
    /// 在微信消息区从 (ax,ay) 拖到 (bx,by) 形成一次文字选择，然后 Ctrl+C。
    /// 返回剪贴板里拿到的内容；没拿到返回 null。
    ///
    /// 为什么用拖拽而不是点击：拖动过程中不会触发消息里的链接，
    /// 所以即使起点正好落在某条消息上也安全。
    /// </summary>
    private static string DragAndCopy(int ax, int ay, int bx, int by)
    {
        // 第四道闸：起终点像素都必须真的属于微信，否则一个键都不按。
        if (!PixelBelongsToWeChat(ax, ay)) return null;
        if (!PixelBelongsToWeChat(bx, by)) return null;

        // 这次 Ctrl+C 包括事后还原剪贴板，全程别让监听线程往外推。
        _suppressClipboardUntil = DateTime.Now.AddSeconds(5);
        // 拖选必须把微信切到前台，那也会触发自动拉取的钩子 —— 安静一会儿，别自己触发自己。
        _autoPullQuietUntil = DateTime.Now.AddSeconds(8);

        uint before = GetClipboardSequenceNumber();

        POINT saved;
        GetCursorPos(out saved);

        try
        {
            SetCursorPos(ax, ay);
            Thread.Sleep(140);
            mouse_event(MeLeftdown, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(140);
            for (int i = 1; i <= 12; i++)      // 分步移动：一步跳过去控件不认
            {
                SetCursorPos(ax + (bx - ax) * i / 12, ay + (by - ay) * i / 12);
                Thread.Sleep(45);
            }
            Thread.Sleep(180);
            mouse_event(MeLeftup, 0, 0, 0, IntPtr.Zero);
            Thread.Sleep(450);

            SendKey(VkControl, false); SendKey(VkC, false);
            SendKey(VkC, true); SendKey(VkControl, true);
            Thread.Sleep(550);

            // 把这次 Ctrl+C 标记成"我们自己按的"，免得剪贴板监听线程
            // 把它当成用户操作又推一遍给页面。
            //
            // 这里**故意不发 Esc**：早先为了清残留选择状态加过一句 Esc，
            // 之后微信窗口就被收进了托盘（vis=False），后续全部失效。
            // 实测拖选完界面上本来就看不到任何残留，不需要清。
            _lastSentSeq = GetClipboardSequenceNumber();
            _lastSentText = "";
        }
        finally
        {
            // 把用户的光标放回原处。拖选期间它必须真的移过去（mouse_event
            // 是按屏幕坐标打真实鼠标输入的），所以只能在结束后还原。
            SetCursorPos(saved.X, saved.Y);
        }

        if (GetClipboardSequenceNumber() == before) return null;
        string t = GetClipboardText();
        return (t != null && t.Length > 0) ? t : null;
    }

    /// <summary>
    /// 微信把选中的消息导成纯文本时，每条都带一行"2026年09月18日 17:05"。
    /// 用这个特征区分"拖到了消息区"和"拖进了输入框、把草稿选中了"。
    /// </summary>
    private static bool LooksLikeDump(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        return System.Text.RegularExpressions.Regex.IsMatch(s, @"\d{4}年\d{1,2}月\d{1,2}日");
    }

    /// <summary>按一下组合键（modifier 传 0 就是单键）。</summary>
    private static void Tapping(ushort modifier, ushort key)
    {
        if (modifier != 0) SendKey(modifier, false);
        SendKey(key, false);
        SendKey(key, true);
        if (modifier != 0) SendKey(modifier, true);
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
        _lastSentSeq = GetClipboardSequenceNumber();

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

                // 拉取期间一律不推。拉取本身就是"拖选 + Ctrl+C"，
                // 那个 Ctrl+C 一定会让剪贴板序号跳一次，而 _lastSentSeq 是在
                // 按完之后才写的 —— 监听线程 400ms 一轮，完全来得及在那之前
                // 就把这一次抓走（实测就抓到了，日志里紧挨着一条
                // "剪贴板变化 -> 推给网页"）。
                //
                // 手动拉取时页面用 pullingUntil 挡得住，但**自动拉取不是页面发起的**，
                // 页面不知道，于是同一条消息会被处理两遍（clipboard + pulled）。
                // 所以这道闸必须在桥这边。
                if (DateTime.Now < _suppressClipboardUntil)
                {
                    Log("剪贴板变化 忽略（拉取期间，共 " + text.Length + " 字符）");
                    continue;
                }

                // 跳过"我们自己刚粘进去"的那一次变化。
                //
                // 这里原来只有 `if (text == _lastSentText) continue;` —— 那是错的：
                // 只要内容跟"桥最后一次粘出去的"相同就永久忽略，于是用户事后
                // 主动 Ctrl+C 复制同一条消息时，桥一声不响，页面永远收不到。
                // 单机自测（测试模式把对方的回复也发到微信）时必现。
                //
                // 现在两条判据都要，但都只吞一次：
                //   now == _lastSentSeq  -> 这次变化就是我们造成的，精确
                //   text == _lastSentText -> 兜住竞态：序号是粘贴前读的、文本是粘贴后读的
                if (now == _lastSentSeq)
                {
                    _lastSentSeq = 0; _lastSentText = "";
                    Log("剪贴板变化 忽略（我们自己刚粘的那一次）");
                    continue;
                }
                if (text == _lastSentText)
                {
                    _lastSentText = "";   // 只吞一次，之后同样的内容照推
                    Log("剪贴板变化 忽略（回声，仅此一次）");
                    continue;
                }

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

            if (path.StartsWith("/pull"))
            {
                string pulled = ManualPull();
                string body = "{\"ok\":" + (pulled != null ? "true" : "false")
                            + ",\"len\":" + (pulled == null ? 0 : pulled.Length)
                            + ",\"text\":\"" + JsonEscape(pulled == null ? "" : pulled) + "\"}";
                WriteHttp(stream, "200 OK", "application/json; charset=utf-8",
                          Encoding.UTF8.GetBytes(body));
                client.Close();
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
                         + ",\"loggedIn\":" + ((have && (_readMode == "mem"
                             ? _lastReadSawSession
                             : WeChatLooksLoggedIn(_wechatHwnd))) ? "true" : "false")
                         + ",\"size\":\"" + (have ? WindowSizeText(_wechatHwnd) : "-") + "\""
                         + ",\"foreground\":" + (WeChatIsForeground() ? "true" : "false")
                         + ",\"autoPull\":" + (_autoPull ? "true" : "false")
                         + ",\"read\":\"" + _readMode + "\""
                         + ",\"session\":\"" + JsonEscape(_sessionId) + "\""
                         + ",\"lastPull\":\"" + JsonEscape(_lastPullNote) + "\""
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
                else if (type == "autopull")
                {
                    // JSON 的 true 到这里是 .NET 的 Boolean，Convert.ToString 出来是
                    // "True"（大写 T）—— 直接比字面量 "true" 永远不成立，
                    // 表现就是"开关只能关不能开"。两种形态都要认。
                    object onObj;
                    bool on = false;
                    if (msg.TryGetValue("on", out onObj) && onObj != null)
                    {
                        if (onObj is bool) on = (bool)onObj;
                        else on = Convert.ToString(onObj).Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                    _autoPull = on;
                    _autoPullQuietUntil = DateTime.Now.AddSeconds(3);
                    Log("自动拉取: " + (on ? "开启" : "关闭"));
                    WsSendText(client, "{\"type\":\"autoPull\",\"on\":" + (on ? "true" : "false") + "}");
                }
                else if (type == "pull")
                {
                    // 页面走 WS 而不是 fetch：file:// 页面用 fetch 打本地端口
                    // 有 CORS / 本地网络访问（LNA）的风险，而 WS 早就验证可用。
                    WsSendText(client, "{\"type\":\"pulling\"}");
                    string pulled = ManualPull();
                    WsSendText(client, "{\"type\":\"pulled\",\"ok\":"
                                     + (pulled != null ? "true" : "false")
                                     + ",\"len\":" + (pulled == null ? 0 : pulled.Length)
                                     + ",\"text\":\"" + JsonEscape(pulled == null ? "" : pulled) + "\"}");
                }
                else if (type == "session")
                {
                    // 锁定要读的会话：1:1 是 wxid_xxx，群是 xxx@chatroom，
                    // 文件传输助手是 filehelper；空串 = 自动取最近有动静的会话。
                    object sidObj;
                    string sid = "";
                    if (msg.TryGetValue("id", out sidObj) && sidObj != null)
                        sid = Convert.ToString(sidObj).Trim();
                    _sessionId = sid;
                    _msgTable = "";
                    _msgWatermark.Clear();
                    Log("读取会话: " + (sid.Length == 0 ? "自动（任何有动静的消息表）" : sid));
                    WsSendText(client, "{\"type\":\"session\",\"id\":\"" + JsonEscape(sid) + "\"}");
                }
                else if (type == "bind")
                {
                    // 绑定要读的会话。两种方式二选一：
                    //   session = 对方 wxid / 群号（表名 = "Msg_" + md5(会话id)）
                    //   table   = 直接给 Msg_<md5> 表名（页面上的会话列表用这个）
                    object o;
                    string sid = "", tbl = "";
                    if (msg.TryGetValue("session", out o) && o != null)
                        sid = Convert.ToString(o).Trim();
                    if (msg.TryGetValue("table", out o) && o != null)
                        tbl = Convert.ToString(o).Trim();
                    if (tbl.Length > 0) sid = "";
                    // 会话 id 比表名更好用：用它能算出表名，还能在"根页不在
                    // 页缓存里"时按会话行的 last_msg_locald_id 去页映像里认页。
                    if (sid.Length > 0) tbl = "";
                    _sessionId = sid;
                    _msgTable = tbl;
                    _msgWatermark.Clear();
                    Log("绑定会话: session=" + (sid.Length == 0 ? "(无)" : sid)
                        + " table=" + (tbl.Length == 0 ? "(无，自动)" : tbl));
                    WsSendText(client, "{\"type\":\"bound\",\"session\":\""
                                     + JsonEscape(sid) + "\",\"table\":\""
                                     + JsonEscape(tbl) + "\",\"who\":\""
                                     + JsonEscape(sid) + "\"}");
                }
                else if (type == "tables")
                {
                    // 页缓存里现在读得到的会话消息表（带最新一条预览），
                    // 供页面列出让人挑一个绑定。
                    WsSendText(client, "{\"type\":\"tables\",\"list\":" + TablesJson() + "}");
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
            if (args[i] == "--read" && i + 1 < args.Length) _readMode = args[i + 1];
            if (args[i].StartsWith("--read=", StringComparison.Ordinal))
                _readMode = args[i].Substring(7);
            if (args[i] == "--session" && i + 1 < args.Length) _sessionId = args[i + 1];
            if (args[i].StartsWith("--session=", StringComparison.Ordinal))
                _sessionId = args[i].Substring(10);
            if (args[i] == "--table" && i + 1 < args.Length) _msgTable = args[i + 1];
            if (args[i].StartsWith("--table=", StringComparison.Ordinal))
                _msgTable = args[i].Substring(8);
        }
        if (_readMode != "clip") _readMode = "mem";
        _startedAt = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;

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

        Log("读取方式: " + (_readMode == "mem"
            ? "内存（读 SQLCipher 解密后的页缓存，不用密钥、不动鼠标）"
            : "剪贴板（拖长方形 + Ctrl+C，旧路径）"));
        Log("读取会话: " + (_msgTable.Length > 0
            ? "绑定表 " + _msgTable
            : (_sessionId.Length > 0
               ? "绑定 " + _sessionId
               : "自动（任何有动静的消息表都读，不依赖会话表）")));

        Thread watcher = new Thread(ClipboardWatcher);
        watcher.IsBackground = true;
        watcher.Start();

        Thread auto = new Thread(AutoPullLoop);
        auto.IsBackground = true;
        auto.Start();

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
