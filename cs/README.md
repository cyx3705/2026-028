# cs/ —— 微信剪贴板桥（C# / .NET Framework）

**零依赖、零安装、零构建工具。** 用每个 Windows 都自带的 C# 编译器编译。

```
chat.html ──WebSocket──▶ bridge.exe ──剪贴板＋键盘──▶ 微信当前对话
                              ▲
                              └── 剪贴板监听 ◀── 你在微信里 Ctrl+C
```

---

## 为什么是"老C#"

| | 需要的运行时 | 用户要装什么 |
|---|---|---|
| .NET 9（已放弃的那版） | .NET 9 SDK | SDK + 运行时，还得 `-m:1` 绕 bug |
| **本方案** | **.NET Framework 4.x** | **什么都不用装** |

```
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
版本 4.8.4084.0  ·  仅支持 C# 5  ·  每个 Windows 都自带
```

`bridge.exe` 的引用列表（实测）：

```
mscorlib 4.0.0.0
System 4.0.0.0
System.Drawing 4.0.0.0
System.Web.Extensions 4.0.0.0
                    ← 全部是 .NET Framework 的一部分，没有一个外部包
```

**体积**：`bridge.cs` 源码 **36 KB** → `bridge.exe` **22 KB**。

> 36 KB 的源码小到可以直接**内嵌进 HTML 页面**——这就是"网页里带安装包"的落点。
> 用户点一下，页面把源码吐成 `.bat`，用系统自带的 `csc.exe` 现场编译，全程不下载任何东西。

---

## 用法

```cmd
start.bat
```

会自动编译（如果还没编译过）并启动。

桥**不托管页面**：页面用 `file://` 直接打开就能连 `ws://127.0.0.1:8765/ws`。

**发送**：页面里打字 → 点发送 → 桥把密文写进剪贴板、把微信切到前台、`Ctrl+V` `Enter`。
**发送目标 = 微信中当前打开的那个对话**，桥不选对话，所以发之前在微信里切好即可。

**接收**：在微信里选中消息 `Ctrl+C` → 桥发现剪贴板变化 → 推给页面。

### HTTP 端点

| 端点 | 作用 |
|---|---|
| `GET /status` | 微信窗口状态、pid、登录态、尺寸、是否有前台、客户端数 |
| `GET /shot` | 微信窗口截图 PNG（**验收用**：桥说自己成功不算数，得看截图） |
| `GET /wincheck?hwnd=<十进制句柄>` | 对任意窗口跑一遍"是否已登录"的判据，调试用 |
| `WS /ws` | 页面通道。收 `{"type":"send","text":…}` / `{"type":"status"}` |

---

## 独立探针

不必开浏览器就能跟桥说话：

```cmd
python wsprobe.py --status          REM 安全探针：不碰微信、一个键都不按
python wsprobe.py --send "内容"      REM 真发一条到微信当前对话
```

`--status` 是刻意分开的：`send` 最终会调 `SendToWeChat()`（动剪贴板 + 发按键），
而 `status` 走完全独立的查询分支。所以

- `status` 有回执、`send` 超时 → WebSocket 线程活着，卡在发送里面
- `status` 也没回执 → 整个通道死了

**注意**：别在 PowerShell 里自己拼 JSON 传参——PS 5.1 会把参数里的双引号吃掉，
`{"type":"send"}` 变成 `{type:send}`，桥的 `JavaScriptSerializer` 直接报
`无效的 JSON 基元`，表现是"桥不回话"，很容易误判成桥坏了。

---

## 安全护栏

发按键是**盲发**，这是整个方案最危险的地方。所以有三道闸：

1. **激活**：`AttachThreadInput` + `SetForegroundWindow` 把微信切到前台
2. **校验**：检查前台窗口**确实是微信进程**，且 class 名含 `Qt`
3. **登录态**：窗口尺寸和样式必须像已登录主界面

**任一校验失败 → 直接中止，一个按键都不发**，并向页面返回
`error: 无法把微信窗口切到前台，已中止（按键未发送）` 之类的具体原因。

### 第 3 道闸的判据（实测数据）

**踩过的坑**：微信被手机端顶下线、PC 弹着登录二维码时，前两道闸照样通过
（同一个 pid、同一个 Qt 窗口类），结果密文被粘进二维码窗口，**桥还报告 `ok`**。

| | 尺寸 | 可缩放 |
|---|---|---|
| 登录二维码界面 | 295×387 | ❌ 固定 |
| 已登录主界面 | 1017×690 / 1721×1041 | ✅ 有 `WS_THICKFRAME` |

### ⚠️ 已知风险

- **粘贴目标取决于微信内部哪个输入框有焦点。**
  如果微信没打开对话、或焦点停在「搜索」框，密文会粘到奇怪的地方。
  桥无法区分——**UIA 读不到微信内部结构**（见下）。
- **校验与发键之间有约 80ms 窗口期**，理论上焦点可能被抢走。风险很低，但不是零。
- 桥只监听 `127.0.0.1`，没做鉴权。本机自用没问题，暴露到局域网前必须加 token。

---

## 三个真踩过的 bug

### 1. 全局锁 + 阻塞写 = 整个桥无声哑掉

`WsSend` 原本是这么写的：

```csharp
lock (ClientsLock)                    // 全局锁
{
    s.Write(hdr, 0, hdr.Length);      // 在锁里做网络写
    s.Write(payload, 0, payload.Length);
    s.Flush();
}
```

客户端连接一旦变成死连接（刷新/关闭标签页、半开连接），`s.Write` 会阻塞到 TCP 重传超时。
而 `TcpClient` 默认 `SendTimeout = 0` 表示**无限等**——攥着全局锁的那个线程不放手。
后果：

| 操作 | 结果 |
|---|---|
| `Clients.Add`（注册新客户端） | 卡死 → 新页面收不到 `hello`、收不到任何回执 |
| `Broadcast` / `RemoveClient` | 卡死 |
| HTTP `/status` | **照常应答**（它读 `Clients.Count` 时没加锁） |

症状就是**"桥看着还活着，WebSocket 彻底哑了"**。现场快照是一排 `CLOSE_WAIT`：
浏览器早关了这些连接，桥一个都没处理。

**修法**：`lock (ClientsLock)` → `lock (client)`（只锁单个客户端，锁里绝不做跨客户端的事），
外加 `c.SendTimeout = 3000`，并在收帧处不再用 `catch { }` 静默吞掉 JSON 解析失败。

### 2. `var` 跨 `.then` 不可见的连锁反应

`setPeerPub` 里为了给测试模式留原始密钥字节，写了 `sendRaw = s;`——
而 `s` 是**上一个 `.then` 里的局部变量**，出了那个作用域就没了。
表现是 `ReferenceError: s is not defined`，一点「测试连接」页面就崩。
**`node --check` 完全查不出来**（语法是合法的）。修法是把 `s` / `r` 跟着 promise 一起传出来。

### 3. 端口被占用时崩得很隐晦

`Exception.ToString()` 都会失败，看不到任何有效信息。启动前先确认 8765 空闲——
用 `pretest.py`，它会用**真实 TCP 连接**确认（`Get-NetTCPConnection` 在这台机器上会谎报"空闲"）。

---

## 为什么不用 UI Automation 读屏

实测过，这条路是死的：

```
window: class='Qt51514QWindowIcon' pid=3100 proc=Weixin
UIA tree:
  [Pane] name=Y(len 21) class=MMUIRenderSubWindowHW
total descendants: 1
Text/ListItem elements: 0
```

微信 4.x 主界面是 **Qt 5.15.14 + 自绘渲染**（`MMUIRenderSubWindowHW` 是它自己的渲染子窗口），
整个窗口对无障碍层**只暴露 1 个元素**，消息文字完全不可见。

所以：读不到 → 只能靠剪贴板。这也正是这套方案**版本无关**的原因。

---

## 踩过的坑（语言/环境）

- **`csc.exe` 只支持 C# 5**：不能用字符串插值 `$""`、`?.`、`out var`、
  表达式体成员、自动属性初始化器。写的时候要一直记着。
- `GetWindowThreadProcessId` 的第二个参数是 `out uint`，**不能传 `IntPtr.Zero`**。
- 缺 `using System.Diagnostics;`（用了 `Process` 却没引）。
- 短路 `&&` 会让 `out` 的 `RECT` 保持未赋值，报 `CS0170`。
