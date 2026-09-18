# web/ —— 单文件网页版 IM（桥 + 页面）

**零依赖、零构建、双击即用。**

```
im.html  ──WebSocket──▶  bridge.py  ──▶  后端（现在是 Mock，回显）
   ▲                          │
   └────── 同一个端口托管 ─────┘
```

---

## 跑起来

```cmd
start.bat
```

会启动桥并自动打开浏览器。不想自动开浏览器：

```cmd
python bridge.py --no-open
```

然后手动访问 <http://127.0.0.1:8765/>

**也可以直接双击 `im.html`** —— 它同样能连上（见下面实测数据）。

---

## 实测结果（Edge 153，LNA 默认开启）

在**真实时钟**下用 CDP 驱动真实浏览器实测，不是推断：

| 打开方式 | `location.protocol` | `location.origin` | `isSecureContext` | WebSocket | 端到端收发 |
|---|---|---|---|---|---|
| 双击 `im.html` | `file:` | `file://` | **true** | ✅ 连通 | ✅ 通过 |
| `http://127.0.0.1:8765/` | `http:` | `http://127.0.0.1:8765` | true | ✅ 连通 | ✅ 通过 |

> ⚠️ 一个踩过的坑：用 `msedge --virtual-time-budget` 抓 DOM 时，`file://` 那格
> 显示"测试中…"（连不上）。**那是假阴性** —— virtual-time 会快进定时器，
> 但 WebSocket 是真实网络 I/O，来不及完成就被 dump 了。
> 换成真实时钟 + 让页面自己回传结果，两个都通过。
> **测网络行为不要用 `--virtual-time-budget`。**

### 测试覆盖

| 脚本 | 覆盖 | 结果 |
|---|---|---|
| `selftest.py` | 服务端：握手 / 帧编解码 / 126 扩展长度 / UTF-8 / **会话列表** / 业务回显 | **24 / 24** |
| `cdp_probe.py <url>` | 浏览器端：加载 / 连接 / **会话列表渲染** / **选中与切换** / 真实打字发送 / 读回气泡 / 长消息 | **16 / 16**（两种 origin 各一遍） |

自己复现：

```cmd
python bridge.py --no-open          REM 一个终端
python selftest.py                  REM 另一个终端
python cdp_probe.py http://127.0.0.1:8765/
python cdp_probe.py file:///C:/Users/Administrator/Desktop/easyTOOL/wechat-connector/web/im.html
python cdp_probe.py http://127.0.0.1:8765/ --shot ui-shot.png   REM 顺便截图
```

> `cdp_probe.py` 会开一个 headless Edge（独立 profile，不碰你自己的浏览器）。

---

## 文件

| 文件 | 行数 | 说明 |
|---|---|---|
| `bridge.py` | 316 | 桥。stdlib WebSocket 服务 + 静态托管 + Mock 后端。**零依赖** |
| `im.html` | 342 | 单文件聊天界面（含会话列表）。内联 CSS/JS，无外部资源 |
| `selftest.py` | 228 | 服务端协议自检 |
| `cdp_probe.py` | 346 | 浏览器端端到端验证（CDP）+ 可选截图 |
| `start.bat` | 6 | 双击启动 |
| `ui-shot.png` | — | 界面截图（`--shot` 生成） |

---

## 选择对话

左侧是**会话列表**，点一下就切换。上面有搜索框（联系人多了以后按名字或 wxid 过滤）。

**「文件传输助手」固定在列表第一位**，带 `内置` 标记。

> 它的 wxid 就是 `filehelper`，是微信内置的特殊会话。
> **发给它 = 发给自己**，不打扰任何人 —— 接真实后端后，这是风险最低的第一个测试目标。

其它行为：

- 每个会话**独立保存**消息，切来切去不会串
- 别的会话来消息时，列表上出现**未读红点**
- 标题栏显示会话名 + 目标 id（`文件传输助手` / `filehelper`），方便确认到底发给了谁

会话列表由桥下发（`convos` 消息）。接真实后端时，把
`MockBackend.list_conversations()` 换成「拉通讯录 + 群列表」即可，前端一行都不用改。

---

## 协议

客户端 → 服务端：

```json
{ "type": "send",   "to": "filehelper", "text": "你好" }
{ "type": "convos" }
```

服务端 → 客户端：

```json
{ "type": "hello",   "backend": "mock", "serverTime": 1758... }
{ "type": "convos",  "list": [
    { "id": "filehelper", "name": "文件传输助手", "kind": "contact",
      "note": "微信内置 · 发给自己", "builtin": true },
    { "id": "wxid_alice", "name": "Alice", "kind": "contact" }
] }
{ "type": "sent",    "id": "...", "to": "filehelper", "text": "你好", "ts": ... }
{ "type": "message", "id": "...", "from": "filehelper", "fromName": "文件传输助手",
                     "text": "你好", "ts": ..., "echo": true }
```

另外有 `GET /health`（探活）和 `GET /report`（浏览器把诊断结果回传到桥日志，
用 1x1 图片信标发的，不受 CORS 限制）。

---

## 接真实后端

改 `bridge.py` 里的 `MockBackend`，其余一行都不用动：

```python
class GewechatBackend:
    name = "gewechat"

    def handle_send(self, target, text):
        # POST http://127.0.0.1:2531/v2/api/message/postText
        ...

BACKEND = GewechatBackend()
```

接收方向要麻烦一些：Gewechat 是**推**给你的 `callback_url` 的，
所以桥还得**额外监听一个端口**收 webhook，再通过 WebSocket 转发给页面。
（回调 IP 不能填 `127.0.0.1`，必须用局域网 IP。）

---

## 已知限制

- **多会话只在界面层**：每个会话的消息是分开存的，但**桥不分状态** —— 后端仍是全局单例，
  多个标签页会互相看到对方发的消息。要真正多端隔离，得给每个 WS 连接加 session id。
- **没有持久化**：刷新页面历史就没了（当前阶段够用）。
- **没有鉴权**：只监听 `127.0.0.1`，本机可用。要暴露到局域网必须先加 token。
- **没有加密**：按要求，现在是"发什么就是什么"。加密器的口子在
  `handle_send` / 收消息回推那两处，接上即可。
- **WebSocket 帧不支持分片续帧**（`OP_CONT` 直接忽略）—— 浏览器实测不会对我们
  发的消息分片，够用；要更严谨可以补。
- ⚠️ **LNA 是移动靶**：目前 WebSocket 豁免于 Local Network Access 限制，
  但 Chrome/Edge 官方明说**计划纳入**。届时 `file://` 直开可能失效，
  而 `http://127.0.0.1` 托管（local→loopback）已被官方明确豁免，不受影响。
  **所以推荐用 `start.bat`，而不是长期依赖双击 HTML。**
