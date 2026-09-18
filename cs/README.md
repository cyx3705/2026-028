# cs/ —— 微信剪贴板桥（C# / .NET Framework）

**零依赖、零安装、零构建工具。** 用 Windows 自带的 C# 编译器编译。

```
im.html ──WebSocket──▶ bridge.exe ──剪贴板＋键盘──▶ 微信当前对话
                            ▲
                            └── 剪贴板监听 ◀── 你在微信里 Ctrl+C
```

---

## 为什么是"老C#"

| | 需要的运行时 | 用户要装什么 |
|---|---|---|
| .NET 9（之前那版） | .NET 9 SDK | SDK + 运行时，还得 `-m:1` 绕 bug |
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

**体积**：`bridge.cs` 源码 **29.5 KB** → `bridge.exe` **19.5 KB**。

> 29.5 KB 的源码小到可以直接**内嵌进 HTML 页面**——
> 这就是"网页里带安装包"那件事的落点。用户点一下，页面把源码吐成 `.cs`，
> 再用系统自带的 `csc.exe` 现场编译，全程不下载任何东西。

---

## 用法

```cmd
start.bat
```

会自动编译（如果还没编译过）并启动，然后打开 <http://127.0.0.1:8765/>。

**发送**：在网页里打字 → 点发送 → 桥把内容写进剪贴板、把微信切到前台、`Ctrl+V` `Enter`。
**发送目标 = 微信中当前打开的那个对话**，桥不选对话，所以你发之前在微信里切好即可。

**接收**：在微信里选中消息 `Ctrl+C` → 桥发现剪贴板变化 → 推给网页。

---

## 实测结果

全部在这台机器上真跑过（微信 **4.1.13.65**，Edge 153）：

| 项 | 结果 |
|---|---|
| 找到微信窗口 | ✅ `pid=3100 class=Qt51514QWindowIcon` |
| 发送到「文件传输助手」 | ✅ 截图确认，时间戳 `11:51`、预览文本正确 |
| **没有跑到别的窗口** | ✅ 其它会话时间戳全部未变 |
| 密文形态渲染 | ✅ `E2E1-M.aB3dEf7hIjKlMnOp...` 正常显示 |
| 接收方向（剪贴板监听） | ✅ `{"type":"clipboard","text":"WATCH-TEST-1 来自剪贴板"}` |

复现：

```cmd
python probe.py --status          REM 查微信窗口状态
python probe.py --shot wx.png     REM 截图微信窗口（用来肉眼验收）
python probe.py --send "内容"      REM 发一条
python probe.py --watch "内容"     REM 验证接收方向
```

`--shot` 是刻意做的：**桥说自己成功不算数，得看截图**。

---

## 安全护栏

发按键是**盲发**，这是整个方案最危险的地方。所以有三道闸：

1. **激活**：`AttachThreadInput` + `SetForegroundWindow` 把微信切到前台
2. **校验**：检查前台窗口**确实是微信进程**，且 class 名含 `Qt`
3. **再校验**：校验通过后才 `SendInput`

**任一校验失败 → 直接中止，一个按键都不发**，并向网页返回
`error: 无法把微信窗口切到前台，已中止（按键未发送）`。

### ⚠️ 已知风险（还没修）

- **粘贴目标取决于微信内部哪个输入框有焦点。**
  如果你在微信里点了「搜索」框，密文会粘进搜索框而不是消息框。
  桥无法区分 —— 因为 UIA 读不到微信内部结构（见下）。
  **发之前请确认微信的消息输入框有焦点。**
- **校验与发键之间有约 80ms 的窗口期**，理论上焦点可能被抢走。
  风险很低，但不是零。
- 桥只监听 `127.0.0.1`，没做鉴权。本机自用没问题，暴露到局域网前必须加 token。

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

所以：读不到 → 只能靠剪贴板。这也正是这套方案**版本无关**的原因：
它不碰协议、不碰界面结构，只依赖"剪贴板"和"Ctrl+V"这两个永远不变的东西。

---

## 踩过的坑

- **`csc.exe` 只支持 C# 5**：不能用字符串插值 `$""`、`?.`、`out var`、
  表达式体成员、自动属性初始化器。写的时候要一直记着。
- `GetWindowThreadProcessId` 的第二个参数是 `out uint`，**不能传 `IntPtr.Zero`**。
- 缺 `using System.Diagnostics;`（用了 `Process` 却没引）。
- 端口被占用时崩得很隐晦（`Exception.ToString()` 都失败）。启动前先确认 8765 空闲。

---

## 下一步

1. **加密**：网页侧接 ECDH + AES-GCM（见 [`../DESIGN-E2E.md`](../DESIGN-E2E.md)），
   发出去的就是真密文，收进来自动解密。
2. **自举安装**：把 `bridge.cs` 内嵌进 HTML，点一下用系统 `csc.exe` 现场编译。
3. **修焦点风险**：发送前先在聊天区底部（消息输入框位置）点一下，再粘贴。
