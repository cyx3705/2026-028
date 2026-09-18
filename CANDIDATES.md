# 微信机器人「连接器」候选方案调研

> 目标：找一个**开源连接器**，但**不接微信机器人本体**，而是把连接器这层抽象接到**本地的会话加密器**上。
> 也就是说，我们要复用的是「连接器 / 传输层可插拔」这个**架构接缝**，不是微信协议本身。

---

## 一、结论先行

「连接器」这个抽象，在微信机器人生态里最标准、最干净的实现是 **Wechaty 的 Puppet**。

- Wechaty 核心 = 统一的会话 API（Message / Contact / Room）
- 传输层 = 一堆 `wechaty-puppet-*` 插件包，**官方明确支持你自己写一个**
- 而且官方提供了一个 **`wechaty-puppet-mock`**，文档原话是：
  > "Can be used as a **starter template for writing your own puppet provider**."

这正好就是我们要的东西：**拿 mock 当骨架，把「连微信」换成「连本地会话加密器」**，上层 Wechaty API 全部白拿。

---

## 二、候选清单

### ⭐ 1. Wechaty —— 契合度 ★★★★★

| 项 | 内容 |
|---|---|
| 仓库 | https://github.com/wechaty/wechaty |
| 语言 | TypeScript / Node.js（另有 Python、Go 多语言版） |
| 协议 | Apache-2.0 |
| 连接器机制 | **Puppet Provider**，包名约定必须以 `wechaty-puppet-` 开头 |

**官方 Provider 列表**（说明这套抽象是真被反复验证过的）：

`wechat`(Web) · `whatsapp` · `official-account` · `gitter` · `lark` · `padlocal` · `wechat4u` · `xp` · `oicq` · `simplepad` · `service` · **`mock`** · **`DIY`**

**写一个自己的 Puppet 有多简单？** 官方 DIY 文档给出的骨架：

```ts
import { Puppet } from 'wechaty'

export class PuppetMyTest extends Puppet {
  // ... implementation here ...
}
export default PuppetMyTest
```

`package.json` 里声明一个 `wechaty` 字段做配置 schema，包名以 `wechaty-puppet-` 开头，就完事了。

**Mock 的用法**（我们要抄的模板）：

```ts
import { PuppetMock, Mocker, SimpleEnvironment } from 'wechaty-puppet-mock'

const mocker = new Mocker()
mocker.use(SimpleEnvironment())
const puppet = new PuppetMock({ mocker })
const wechaty = new Wechaty({ puppet })
wechaty.start()
```

注意这里的形状：**Puppet 接管了所有「外部世界」的 I/O**，包括登录（`mocker.scan()` / `mocker.login()`）、联系人、房间、收发消息。
把 `Mocker` 换成「本地会话加密器客户端」，就是我们要做的事。

**为什么它是最佳答案**：它的 `login / logout / message / contact / room` 全部经由 Puppet，
连**登录态（session）本身**都是 Puppet 的职责 —— 而「会话加密器」要管的正是登录态。
接缝位置完全吻合。

**⚠️ 关于它自带的微信 Provider**（如果之后又想接真微信，必读）：
- `wechaty-puppet-wechat`（Web 协议）**基本已废**：2017 年后注册的微信号无法登录 Web 微信；
  2022-01-01 起 UOS patch 也无法登录。**Web API 自 2018 年起不能建群 / 拉人。**
- 跨会话 contact / room 的 ID 会变。
- 想突破只能用非 Web 的 Puppet（如 PadLocal 等，多数是**付费** Puppet Service）。
- 来源：https://wechaty.js.org/docs/puppet-providers/wechat

---

### 2. LangBot —— 契合度 ★★★★

| 项 | 内容 |
|---|---|
| 仓库 | https://github.com/RockChinQ/LangBot |
| 定位 | 生产级多平台 IM 机器人开发平台 |
| 连接器机制 | **Platform Adapter**，有独立开发文档 |

覆盖平台：QQ · 企业微信 · 企微智能机器人 · 公众号 · 飞书 · 钉钉 · Discord · Slack · Telegram · LINE

**优点**：适配器层的边界划得很清楚，插件/Agent/知识库都现成。
**缺点**：它是**一整个平台**，你要跑起来一整套。如果只想验证「连接器 → 本地加密器」这一条链路，属于杀鸡用牛刀。

> 文档：https://deepwiki.com/RockChinQ/LangBot/10.5-platform-adapter-development

---

### 3. Gewechat —— 契合度 ★★★

| 项 | 内容 |
|---|---|
| 仓库 | https://github.com/sdu-lifei/gewechat |
| 机制 | 微信 **iPad 协议**（非 HOOK 破解桌面端），对外提供 **HTTP API + 回调** |
| 连接器 | 不需要写 SDK，写一个 **HTTP 适配器**即可 |

**优点**：接口就是 HTTP，最容易 mock / 最容易替换 endpoint —— 把 base URL 指向本地加密器就行。
**缺点**：仍是微信协议的封装，抽象层次比 Wechaty 的 Puppet 低。属于「连的是微信服务，不是可插拔传输层」。

---

### 4. WeChatFerry —— 契合度 ★★（**风险最高**）

| 项 | 内容 |
|---|---|
| 仓库 | https://github.com/lich0821/WeChatFerry |
| 机制 | **Windows 微信客户端 HOOK**（注入），Python SDK / HTTP / gRPC |

**⚠️ 明确提醒**：这是往微信进程里注入代码，属于**逆向 hook**。
封号风险、客户端版本强绑定（微信一升级就挂）、以及 ToS 与法律风险都显著高于前几个。
**如果这条链路最终不接微信，那完全没必要引入它。**

---

### 5. hp0912/wechat-robot-mcp-server —— 契合度 ★★★

| 项 | 内容 |
|---|---|
| 仓库 | https://github.com/hp0912/wechat-robot-mcp-server |
| 机制 | 把微信机器人能力暴露为 **MCP Server** |

**适合**：你想要的「连接器」其实是给 AI Agent 用的 MCP 工具接口。
**不适合**：你要的是传输层抽象。

---

## 三、给你的判断依据

先回答一个问题，路线就定了：

**「本地会话加密器」到底扮演哪个角色？**

### 情况 A：它替代的是「微信传输层」
> 即：上层逻辑还是聊天的语义（发消息 / 收消息 / 会话列表），只是**不真的连微信**，
> 而是把这一整层换成「送进本地加密器」。

→ **选 Wechaty + 自写 Puppet（抄 `wechaty-puppet-mock`）**
理由：Puppet 接管的正是这一层，且 mock 就是为「没有真实后端」设计的。

### 情况 B：它替代的是「登录态存储」
> 即：连接器本身照常工作，但 **session / 登录凭据不落明文盘**，
> 每次读写 session 都经过本地加密器的 encrypt/decrypt。

→ **选 Wechaty，但改的是 `MemoryCard` / 登录态持久化那一段**
理由：Wechaty 有 `MemoryCard` 抽象（`memory.set/get`），是 session 落盘的统一出口，改这里最省事。
这种情况下**根本不用碰 Puppet**，工作量小得多。

### 情况 C：微信只是「形状参考」，实质是别的系统
> 即：你其实不需要微信生态，只是想要一个「连接器 → 本地加密器」的现成架构来抄。

→ **不必引入微信依赖**，直接照 Puppet 的接口形状写一个薄适配层。
理由：引入 Wechaty 会带进一大堆用不到的聊天语义和依赖。

---

## 四、待确认（下一步的前提）

1. **本地会话加密器是什么？** 还没有、还是已有程序？
2. 如果是已有的，它的**接口形态**是什么？
   - HTTP / REST ？（最理想，适配器最好写）
   - Unix domain socket / 命名管道？
   - gRPC / JSON-RPC？
   - CLI 子进程 / stdin-stdout？
   - 还是一个要 import 的库？
3. 「会话」指的是 **登录态凭据**，还是 **聊天消息内容**，还是**两者**？
4. 技术栈偏好：**TypeScript/Node**（顺着 Wechaty）还是 **Python**？

---

## 五、参考链接

- Wechaty 主仓库：https://github.com/wechaty/wechaty
- Puppet Provider 总览：https://wechaty.js.org/docs/puppet-providers
- **Puppet Mock（我们的模板）**：https://wechaty.js.org/docs/puppet-providers/mock
- **Puppet DIY（自己写的规范）**：https://wechaty.js.org/docs/puppet-providers/diy
- Puppet Spec：https://wechaty.js.org/docs/specs/puppet
- Web 协议已知问题：https://wechaty.js.org/docs/puppet-providers/wechat
- LangBot：https://github.com/RockChinQ/LangBot
- Gewechat：https://github.com/sdu-lifei/gewechat
- WeChatFerry：https://github.com/lich0821/WeChatFerry
- WeChat Robot MCP Server：https://github.com/hp0912/wechat-robot-mcp-server

---

*调研日期：本次会话 · 所有仓库均为 GitHub 公开开源项目*
