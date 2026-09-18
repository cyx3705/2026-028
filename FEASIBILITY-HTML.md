# 可行性分析：能不能做成「HTML 单体，打开即用」

> 问题：现在的 C# 连接器太重（MSBuild、NuGet、`-m:1` 绕过 workload bug），
> 能不能换成一个 **HTML 单文件，双击打开就能收发**，先不加密，发什么就是什么。

---

## 结论（先说答案）

**纯 HTML 单文件做不到。**
**HTML 单文件 + 一个极小的本地桥可以，而且比现在的 C# 方案轻一个数量级。**

关键在于要认清重量在哪：

> **换成 HTML 能砍掉「界面层」的全部重量，但砍不掉「微信协议层」的重量。**
> 微信个人号的协议必须由本地原生进程承载，浏览器物理上碰不到。

---

## 一、为什么纯 HTML 单独不可能

三条硬约束，任何一条单独成立就足以否决：

### 1. 浏览器没有原始 TCP/UDP 套接字

JS 只有 `fetch`(HTTP) / `WebSocket` / `WebRTC` 三种对外通道。

微信个人号协议是**自定义二进制协议跑在 TCP/TLS 上**（iPad 协议、桌面端协议都是）。
浏览器无法构造这种连接，**没有例外**。

> 唯一的理论出路是 Chrome 的 [Direct Sockets API](https://developer.chrome.com/docs/iwa/direct-sockets)，
> 但它**只对 Isolated Web App (IWA) 开放** —— 需要打包、签名、特殊安装。
> 跟「双击 HTML 打开」完全不沾边，直接排除。

### 2. WeChatFerry 是进程注入

它的原理是往 **Windows 微信客户端进程里注入 DLL**。
这是原生代码，浏览器运行环境里做不到，连近似都做不到。

### 3. 接收消息靠 webhook 回调，而浏览器不能当服务端

Gewechat 的模型是：

| 方向 | 方式 |
|---|---|
| 发送 | HTTP POST → `http://ip:2531/v2/api/...` |
| **接收** | Gewechat **主动推**到你的 `callback_url` |

**浏览器没有监听端口的能力**，收不到任何入站请求。
所以「收什么就收什么」这一半，纯网页**结构上无法实现**。

> 附带一个实测到的坑：dify-on-wechat 的对接文档明确写了
> `gewechat_callback_url` 的 IP **不能填 `127.0.0.1` 或 `localhost`**，否则报错，
> 必须用局域网 IP。

---

## 二、本机实测环境事实

这些是我在这台机器上实际查到的，不是假设：

| 项 | 实测值 | 影响 |
|---|---|---|
| 浏览器 | **只有 Edge 153.0.4234.32**（没有 Chrome） | ≥143，**LNA 已默认开启** |
| Python | 3.14.5（`python` / `py` 都在 PATH） | 桥可以用零依赖 stdlib 写 |
| gewechat 端口 2531/2532 | free（未运行） | 需要 Docker 起服务 |
| 回调端口 9919 | free | 需要自己起接收端 |

### Local Network Access（LNA）——这是新变量

微软[官方文档](https://learn.microsoft.com/en-us/deployedge/ms-edge-local-network-access)（2026-03 更新）确认：

- **Edge 143 起 LNA 默认开启**（Chrome 142 起）
- LNA 当前覆盖：`fetch()`、子资源请求、子框架导航
- **LNA 当前「尚未」覆盖：WebSocket、WebTransport、WebRTC** —— 但**计划覆盖**
- **`local → local` / `local → loopback` 请求不受限制**
- LNA 权限**只能在安全上下文（HTTPS 或 localhost）里申请**；HTTP 页面调用 Permissions API **永远返回 `denied`**

对你的意义：**页面从 `localhost` 提供时，连 `localhost` 是安全的**；
而 `file://` 的地址空间归属是灰色地带，**不要赌**。

---

## 三、三种候选架构对比

### 方案 A：`file://` 直接开 + WebSocket 连桥 ❌ 不推荐

```
file:///xxx/im.html  ──ws://127.0.0.1:8765──▶  桥  ──▶  微信后端
```

- ✅ 真正「双击打开」，不需要服务
- ❌ `file://` 是 **opaque origin**，`fetch` 必挂 CORS（[MDN 明确说明](https://developer.mozilla.org/en-US/docs/Web/HTTP/Guides/CORS/Errors/CORSRequestNotHttp)）
  —— 所以**只能用 WebSocket，不能用 fetch**
- ❌ WebSocket 目前不受 LNA 限制，**但官方明说「计划纳入」** —— 这是颗定时炸弹
- ⚠️ `file://` 的地址空间归属未定义，LNA 行为不可预测

**结论**：现在能跑，但随时可能被浏览器更新打死。不适合作为长期方案。

### 方案 B：桥同时托管页面（同源）✅ **推荐**

```
http://127.0.0.1:8765/          ← 桥把 HTML 吐出来
        │  fetch / WebSocket，同源
        ▼
   桥 (Python, ~100 行)  ──▶  微信后端
```

- ✅ **同源** → 完全没有 CORS 问题
- ✅ `local → loopback` → **LNA 不触发**（官方明确豁免）
- ✅ HTTP + WebSocket 都能用，不用绕过
- ✅ 仍然是「打开一个网页就能用」，只是地址是 `http://127.0.0.1:8765`
- ✅ HTML 仍然是**单个自包含文件**（内联 CSS/JS），只是由桥托管
- ⚠️ 需要先跑桥（一个 `.bat` 双击搞定）

**结论**：唯一没有隐患的方案。

### 方案 C：浏览器扩展 🟡 备选

微软文档原话：

> "We don't currently have plans to apply LNA restrictions to extensions.
> Currently, extensions that have the necessary host permissions are allowed
> to make local network requests."

- ✅ **免疫 CORS 和 LNA**（有 host permissions 就行）
- ❌ 不是「双击打开」，要加载解压的扩展 / 打包
- ❌ **仍然解决不了接收问题** —— 扩展同样不能监听入站端口

**结论**：绕过了网络限制，但核心矛盾（webhook 收不到）没解决，收益不划算。

---

## 四、重量对比

| | 现在（C#） | 方案 B（HTML + 桥） |
|---|---|---|
| 构建 | MSBuild + NuGet + `-m:1` 绕过 SDK bug | **无构建** |
| 依赖 | .NET 9 SDK、workload（已损坏） | Python 3.14（已有）/ 或 stdlib 零依赖 |
| 文件数 | 2 项目 + sln + 若干 cs | 1 HTML + 1 PY + 1 BAT |
| 改一行的代价 | 重新 build | **刷新浏览器** |
| 界面迭代 | 改 C# 重编译 | 改 HTML 直接刷新 |
| 仍然保留的重量 | 微信后端（hook/Docker） | 微信后端（hook/Docker） |

**注意最后一行**：微信协议层的重量两边一样，砍不掉。
如果用 Gewechat，你**照样要跑 Docker**；如果用 WeChatFerry，**照样要装指定版本微信 + 注入**。

所以真实的节省是：**干掉 UI 层的构建链**，从「改一行要 build」变成「改一行刷新」。

---

## 五、实测结果（✅ 已验证）

第 1、2 项已经做完 spike，**结论都通过了**。实测环境 **Edge 153.0.4234.32**，
真实时钟 + CDP 驱动真实浏览器：

| # | 待验证 | 结果 |
|---|---|---|
| 1 | `file://` → `ws://127.0.0.1` 在 Edge 153 上是否真的通 | ✅ **通** |
| 2 | `http://127.0.0.1` 页面 → `localhost` 是否触发 LNA | ✅ **不触发**，且同源无 CORS |
| 3 | Gewechat 的 API 是否返回 CORS 头 | ⬜ 未验（走方案 B 同源托管后已不需要） |
| 4 | Gewechat 是否有「拉取消息」的轮询接口 | ⬜ 未验（接后端时再确认） |

| 打开方式 | `location.protocol` | `location.origin` | `isSecureContext` | WebSocket |
|---|---|---|---|---|
| 双击 `im.html` | `file:` | `file://` | **true** | ✅ 连通 |
| `http://127.0.0.1:8765/` | `http:` | `http://127.0.0.1:8765` | true | ✅ 连通 |

**意外收获**：`file://` 在 Edge 里的 `isSecureContext` 居然是 `true`，
origin 也不是预期的 `null`/opaque，而是 `file://`。
所以方案 A（纯双击 HTML）**今天确实可用**，不是只能靠方案 B。

> ⚠️ **踩过的坑（值得记）**：第一次用
> `msedge --headless --virtual-time-budget=8000 --dump-dom` 抓 DOM 时，
> `file://` 那格显示「测试中…」= 看起来连不上。
> **这是假阴性** —— virtual-time 会快进定时器，而 WebSocket 是真实网络 I/O，
> 在 virtual time 走完前根本来不及触发回调。
> 换成真实时钟 + 让页面用 1×1 图片信标把结果回传给桥，两个 origin 都通过。
>
> **教训：测网络行为不要用 `--virtual-time-budget`。**

复现方式见 [`web/README.md`](./web/README.md)。
覆盖度：服务端 **17/17**，浏览器端 **8/8**（两种 origin 各跑一遍）。

---

## 六、建议路线

如果确认要做，我建议：

**第 0 步**：~~跑 spike 定死打开方式~~ ✅ **已完成** —— 两种都通，但推荐 `http://127.0.0.1`（见上）。

**第 1 步**：~~最轻的可用版本~~ ✅ **已完成**，代码在 [`web/`](./web/)：
- `bridge.py` —— Python **stdlib 零依赖**（`http.server` + 手写 WebSocket 握手/帧，约 150 行）
  - `/` 返回内联的 HTML
  - `/ws` 提供 WebSocket
  - 后端先挂 **Mock**（内存回显），**完全不碰微信**
- `im.html` —— 单文件，聊天界面 + WebSocket 客户端
- `start.bat` —— 双击起桥 + 开浏览器

这一步做完，就是「双击 → 浏览器打开 → 打字发出 → 收到」，**零加密、零微信依赖、零构建**。
和 C# 那版的 `MockWeChatChannel` 是同一个抽象层，只是换了个壳，但少了整条 MSBuild 链路。

**第 2 步**：把 Mock 换成真实后端（WeChatFerry 或 Gewechat）。⬜ **未开始**
这一步才开始有微信相关的风险和成本。

---

## 七、代价与风险

- ⚠️ **LNA 是移动靶**：WebSocket 现在豁免，官方明说会纳入。方案 B 天然规避，方案 A 会被打死。
  **这是选 B 不选 A 的核心理由。**
- ⚠️ 微信个人号自动化仍然**违反 ToS、有封号风险**；`--disable-web-security` 之类的偏方不要用。
- ⚠️ Gewechat 要求**服务与登录手机同省**，且「只支持接收文字消息」，回调 IP 不能是 `127.0.0.1`。
- ✅ 第 1 步（HTML + Mock 桥）**不引入任何微信依赖**，风险为零，可以放心先做。

---

## 八、参考

- [MDN · Reason: CORS request not HTTP](https://developer.mozilla.org/en-US/docs/Web/HTTP/Guides/CORS/Errors/CORSRequestNotHttp)（`file://` 为 opaque origin）
- [Microsoft Learn · Local Network Access restrictions](https://learn.microsoft.com/en-us/deployedge/ms-edge-local-network-access)（Edge 143 起默认开启；WebSocket 暂豁免；local→loopback 不受限）
- [Chrome for Developers · Direct Sockets (IWA)](https://developer.chrome.com/docs/iwa/direct-sockets)（唯一能拿原始套接字的路，但需 IWA 打包）
- [dify-on-wechat · gewechat 对接文档](https://github.com/pest1999/dify-on-wechat/blob/master/docs/gewechat/README.md)（2531/2532 端口、回调限制、同省要求）
- [Gewechat API 文档 (Apifox)](https://apifox.com/apidoc/shared-69ba62ca-cb7d-437e-85e4-6f3d3df271b1/api-197179336)
