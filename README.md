# wechat-connector

一个**极简的微信输入/输出接口**（C#）。

用途很窄，就是一件事：

> 把**本地加密后的密文**送进/取出来自微信，微信在这里只当一条**传输管道**。

```
明文
 │
 ▼
┌──────────────────┐
│ 本地会话加密器    │  ← 你的实现，通过 IPayloadCipher 接进来
└──────────────────┘
 │  密文 (byte[])
 ▼
┌──────────────────┐
│ IWeChatChannel   │  ← 本项目的核心抽象，唯一需要替换的接缝
└──────────────────┘
 │  Base64Url 文本
 ▼
   微 信
```

**本项目不做加解密，也不关心明文语义。** 它只负责搬运密文。

---

## 目录结构

```
wechat-connector/
├── WeChatConnector.sln
├── build.cmd                      # 一键构建
├── run.cmd                        # 一键跑 Demo
├── CANDIDATES.md                  # 微信机器人连接器调研（Wechaty / LangBot / Gewechat ...）
└── src/
    ├── WeChatConnector/           # 核心库（零外部依赖）
    │   ├── IWeChatChannel.cs      # ★ 连接器接口 —— 就这一个接缝
    │   ├── Models.cs              # WeChatTarget / WeChatEnvelope / SentMessage
    │   ├── TextPayloadCodec.cs    # 密文 <-> Base64Url 文本
    │   └── MockWeChatChannel.cs   # 纯内存实现，不连微信
    └── WeChatConnector.Demo/      # 控制台 Demo
        ├── IPayloadCipher.cs      # ★ 本地加密器的接缝
        └── Program.cs
```

---

## 快速开始

```cmd
build.cmd
run.cmd
```

Demo 输出（实测）：

```
=== 微信 I/O 极简接口 · Demo（不连接真实微信）===

[通道] 'mock' 已启动，IsRunning=True

[发出] target = contact:wxid_alice
       明文         : 你好，这是一条要发出去的明文
       密文（微信里）: gNjNiojSgdD1xdH2krXcgdv1lPjFjcvuyOD_if6Xi-HPyunhhe38g_Li

[模拟] 微信侧推送了一条消息...
  [收到] Alice (wxid_alice) 来自 contact:wxid_alice
         密文（微信里实际可见）: gvHbiqXfgdD1xdH2krXcg-vklf_ggPbxyMvj
         明文（解密后）        : 收到，这是我的回复

[汇总] 本次共发出 1 条消息：
  -> contact:wxid_alice  gNjNiojSgdD1xdH2krXcgdv1lPjFjcvuyOD_if6Xi-HPyunhhe38g_Li

[校验] Base64Url 往返: 通过
[通道] 已停止，IsRunning=False
```

注意 `密文（微信里）` 那一行 —— **这就是微信服务器和对端实际能看到的东西**。

---

## 核心接口

### 1. `IWeChatChannel` —— 微信通道

```csharp
public interface IWeChatChannel : IAsyncDisposable
{
    string Name { get; }
    bool IsRunning { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    // 发：密文进
    Task SendAsync(WeChatTarget target, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default);

    // 收：密文出
    IAsyncEnumerable<WeChatEnvelope> ReceiveAsync(CancellationToken ct = default);
}
```

设计要点：

- **载荷是 `ReadOnlyMemory<byte>`，不是 `string`** —— 强制在类型层面区分「密文」和「文本」，
  避免有人在通道里顺手做字符串处理而破坏密文。
- **编解码下沉到实现**：`TextPayloadCodec` 负责 Base64Url，因为微信文本消息传不了任意字节
  （`.` `+` `/` `=` 这些字符在网关/中间层容易被转义）。
- **`WeChatTarget` 用工厂方法**（`Contact(wxid)` / `Group(roomId)`），避免把 wxid 和 roomId 搞混。

### 2. `IPayloadCipher` —— 你的本地加密器

```csharp
public interface IPayloadCipher
{
    ReadOnlyMemory<byte> Encrypt(string plaintext);
    string Decrypt(ReadOnlySpan<byte> ciphertext);
}
```

真实实现随便怎么来 —— HTTP 服务、Unix socket、gRPC、直接调的库，都只要实现这两个方法。

Demo 里有两个实现：

| 实现 | 说明 |
|---|---|
| `PlaintextCipher` | 直通，不做处理，仅用于观察链路 |
| `DemoOnlyXorCipher` | ⚠️ **不是加密，没有任何安全性**，只为演示「明文→密文」发生在哪一步 |

---

## 已实现 / 待实现

| 组件 | 状态 |
|---|---|
| `IWeChatChannel` 接口 | ✅ |
| `WeChatTarget` / `WeChatEnvelope` 模型 | ✅ |
| `TextPayloadCodec`（Base64Url） | ✅ |
| `MockWeChatChannel`（内存，可单测） | ✅ |
| Demo 控制台 | ✅ |
| 真实微信后端 | ⬜ 见下 |
| 真实本地加密器 | ⬜ 你的部分 |
| 单元测试项目 | ⬜ |

---

## 接入真实微信后端

`MockWeChatChannel` 换掉即可，**上层代码一行不用改**。可选路线：

### A. WeChatFerry.Net —— C# 原生，但风险最高

NuGet 包 `WeChatFerry.Net`（作者 SilkageNet，最新 1.0.11 / 2025-04-06）。

```csharp
using var client = new WCFClient();
client.OnRecvMsg += (s, e) => { /* e.Content 是密文文本 -> TextPayloadCodec.Decode */ };
if (!await client.Start()) return;
client.SendTxt("filehelper", TextPayloadCodec.Encode(ciphertext));
```

- 目标框架 `net6.0-windows7.0` / `net8.0-windows7.0`，**仅 Windows**
- 依赖 `Google.Protobuf` + `NanomsgNG.NET`
- **必须装指定版本的微信客户端**（当前默认 `3.9.12.17`），版本一变就挂
- ⚠️ 底层是**往微信进程注入的 hook**，封号风险与 ToS 风险显著

### B. Gewechat —— REST，最干净（推荐）

https://github.com/sdu-lifei/gewechat

对外就是 HTTP API + 回调，跟语言无关。C# 侧只需要 `HttpClient`，
且**最容易把 base URL 指到本地 mock**，联调成本最低。

### C. Wechaty + puppet-service

https://github.com/wechaty/wechaty

架构其实是最正统的（见 `CANDIDATES.md`），但 C# 侧要走 gRPC 桥接，
且自带 Web 协议已基本失效。除非你打算长期做多平台，否则不建议。

> 详细的方案对比与取舍，见 [`CANDIDATES.md`](./CANDIDATES.md)。

---

## 构建环境注意事项

**当前这台机器上，必须加 `-m:1`：**

```cmd
dotnet build WeChatConnector.sln -m:1
```

原因：MSBuild 默认多节点构建会走**命名管道**，在当前沙箱下被拦截，
表现为「0 个错误但生成失败」。`build.cmd` / `run.cmd` 已经带上了这个参数。

另外本机 .NET 9 SDK 的 workload 安装不完整
（`sdk\9.0.318\Sdks\` 下缺 `Microsoft.NET.SDK.Workload*Locator` 目录，
会报 `MSB4276`）。这在多节点路径上会致命，`-m:1` 一并绕过了。

`build.cmd` / `run.cmd` 还会把 `DOTNET_CLI_HOME` 和 `NUGET_PACKAGES`
指向项目内的 `.dotnet/` `.nuget/`，让构建自包含、不写用户目录。
在你自己机器上直接 `dotnet build` 也是可以的。

---

## 风险提醒

- 微信个人号自动化**违反微信用户协议**，存在封号风险；hook 方案尤其明显。
- 如果最终不接真实微信（只用加密器 + 本地通道），那**完全不需要引入任何微信 hook**，
  也就没有上述风险 —— 目前这版代码就是这种状态，零微信依赖。
- `DemoOnlyXorCipher` **不是加密**，请务必替换。
