/**
 * 「我当接收方」的接收路径测试。
 *
 * 这个文件补的是一个真实的缺口：handleIncoming 里那条"收到对方公钥"的分支
 * （前缀识别 / 自己的公钥要拒 / setPeerPub / refreshPills）以及它引发的
 * 界面状态切换（药丸变绿、输入框解锁、指纹显示），之前一行测试都没有。
 *
 * 做法：把 chat.html 的整个 <script> 在 Node 里加载起来，
 * 用一套最小 DOM 桩顶掉浏览器 API，然后**直接调页面里那个 handleIncoming**，
 * 走的是和"对面在微信里 Ctrl+C"完全相同的入口。
 * 断言的是桩 DOM 上的真实状态（药丸文字/class、输入框 disabled、日志文本）。
 *
 * 跑法：node test_receive.js [可选 HTML 路径]
 */
"use strict";

const fs = require("fs");
const path = require("path");
const { webcrypto } = require("crypto");

if (!globalThis.crypto || !globalThis.crypto.subtle) {
  globalThis.crypto = webcrypto;
}

/* ---------------------------------------------------------------------
 * 最小 DOM 桩
 * ------------------------------------------------------------------- */

function makeEl(tag){
  const el = {
    tagName: tag || "div",
    className: "",
    textContent: "",
    innerHTML: "",
    value: "",
    disabled: false,
    placeholder: "",
    checked: false,
    style: {},
    children: [],
    scrollTop: 0,
    scrollHeight: 0,
    hidden: false,
    _attrs: {},
    appendChild(c){ el.children.push(c); return c; },
    removeChild(c){ el.children = el.children.filter(x => x !== c); },
    remove(){},
    focus(){ if (globalThis.document) globalThis.document.activeElement = el; },
    select(){},
    /* 记下监听器，click() 真去调 —— 否则按钮上的逻辑在测试里根本进不去 */
    addEventListener(type, fn){ (el._on[type] = el._on[type] || []).push(fn); },
    click(){
      const ev = { target: el, stopPropagation(){}, preventDefault(){} };
      el.focus();
      (el._on.click || []).forEach(f => f.call(el, ev));
    },
    querySelector(){ return null; },
    querySelectorAll(){ return []; },
    setAttribute(k, v){ el._attrs[k] = String(v); },
    getAttribute(k){ return Object.prototype.hasOwnProperty.call(el._attrs, k) ? el._attrs[k] : null; },
    removeAttribute(k){ delete el._attrs[k]; },
    closest(){ return null; },
    getBoundingClientRect(){ return { top:0, left:0, width:100, height:20 }; },
    _on: {},
  };
  el.classList = {
    add(c){ if (!el.classList.contains(c)) el.className = (el.className + " " + c).trim(); },
    remove(c){ el.className = el.className.split(/\s+/).filter(x => x && x !== c).join(" "); },
    contains(c){ return el.className.split(/\s+/).indexOf(c) >= 0; },
    toggle(c, on){
      if (on === undefined) on = !el.classList.contains(c);
      if (on) el.classList.add(c); else el.classList.remove(c);
    },
  };
  return el;
}

const elements = {};
function $(id){
  if (!elements[id]) elements[id] = makeEl(id);
  return elements[id];
}

const documentListeners = {};
globalThis.document = {
  body: makeEl("body"),
  getElementById: (id) => $(id),
  querySelector: () => null,
  querySelectorAll: () => [],
  createElement: (t) => makeEl(t),
  activeElement: null,
  addEventListener: (type, fn) => { (documentListeners[type] = documentListeners[type] || []).push(fn); },
  dispatchEvent: (ev) => { (documentListeners[ev.type] || []).forEach(fn => fn.call(globalThis.document, ev)); },
  execCommand: () => false,
};

// 给页面提供可控的 navigator/location；剪贴板桩在下面按场景切换成功、拒绝和失败。
for (const [k, v] of [["navigator", { clipboard: undefined }],
                      ["location", { protocol: "file:", host: "", href: "file:///chat.html" }]]) {
  try { Object.defineProperty(globalThis, k, { value:v, writable:true, configurable:true }); }
  catch (e) { try { globalThis[k] = v; } catch (e2) { /* 保留运行时默认值 */ } }
}
globalThis.alert = () => {};

// 确认/复制路径测试用的可控剪贴板桩。
const clipboardState = { mode:"none", writes:[], errorName:"NotAllowedError" };
globalThis.__clipboardState = clipboardState;
globalThis.navigator.clipboard = {
  writeText: (text) => {
    clipboardState.writes.push(String(text));
    if (clipboardState.mode === "reject") {
      const e = new Error("clipboard permission denied");
      e.name = clipboardState.errorName;
      return Promise.reject(e);
    }
    if (clipboardState.mode === "throw") throw new Error("clipboard unavailable");
    return Promise.resolve();
  },
};

// WebSocket 桩：连不上也不回调，于是 Bridge 保持"未连接"且不排重试 —— 结果确定。
// 实例收集起来（__sockets），需要时把 readyState 打开、手动喂一帧进来，
// 这样"桥回了什么 -> 界面变成什么"这条链能在 Node 里真跑一遍。
globalThis.__sockets = [];
globalThis.WebSocket = function(){
  this.readyState = 0;
  this.sent = [];
  this.throwOnSend = false;
  this.send = (t) => { if (this.throwOnSend) throw new Error("socket closed"); this.sent.push(String(t)); };
  this.close = () => {};
  globalThis.__sockets.push(this);
};
globalThis.WebSocket.OPEN = 1;

// beacon 用的 1x1 图片
globalThis.Image = function(){ this.src = ""; };

// Crypto 模块会读写 localStorage
const _store = {};
globalThis.localStorage = {
  getItem: (k) => (k in _store ? _store[k] : null),
  setItem: (k, v) => { _store[k] = String(v); },
  removeItem: (k) => { delete _store[k]; },
};

/* ---------------------------------------------------------------------
 * 加载页面脚本
 * ------------------------------------------------------------------- */

const htmlPath = process.argv[2]
  ? path.resolve(process.argv[2])
  : path.join(__dirname, "chat.html");
const html = fs.readFileSync(htmlPath, "utf8");
const m = html.match(/<script>([\s\S]*?)<\/script>/);
if (!m) { console.error("找不到 <script>"); process.exit(1); }

// 在同一个 eval 作用域里把需要的东西交出来（strict 模式下声明不会外泄）
let P;
eval(m[1] + `
;globalThis.__P = {
  Crypto: Crypto,
  Bridge: Bridge,
  handleIncoming: handleIncoming,
  recvManual: recvManual,
  parseWeChatDump: parseWeChatDump,
  onPulled: onPulled,
  onlyNewMessages: onlyNewMessages,
  refreshPills: refreshPills,
  PREFIX_KEY: PREFIX_KEY,
  PREFIX_MSG: PREFIX_MSG,
  b64uEncode: b64uEncode,
  getMyKeySent: function(){ return myKeySent; },
  sendViaBridge: sendViaBridge,
  copyText: copyText,
  openConfirm: openConfirm,
  finishConfirm: finishConfirm,
  cancelConfirm: cancelConfirm,
  submitConfirm: submitConfirm,
  getConfirmState: function(){ return confirmState; },
  showPasteFallback: showPasteFallback,
  buildInstaller: buildInstaller,
  isMyEcho: isMyEcho,
  $: $,
};
`);
P = globalThis.__P;

/* ---------------------------------------------------------------------
 * 断言
 * ------------------------------------------------------------------- */

let pass = 0, fail = 0;
function ok(name){ pass++; console.log("  [OK]   " + name); }
function bad(name, extra){ fail++; console.log("  [FAIL] " + name + (extra ? "\n         <- " + extra : "")); }
function check(cond, name, extra){ cond ? ok(name) : bad(name, extra); }

/** 读桩 DOM 里 sys 日志的全部文本 */
function logLines(){
  return $(("log")).children.map(c => c.textContent);
}
function logHas(sub){
  return logLines().some(t => t.indexOf(sub) >= 0);
}

const pillKey = () => $("pillKey");
const input   = () => $("input");

/** 等 setPeerPub 那条 promise 链跑完（它是异步的，refreshPills 在后面） */
const settle = () => new Promise(r => setTimeout(r, 60));

/* ---------------------------------------------------------------------
 * 测试
 * ------------------------------------------------------------------- */

(async () => {
  console.log("=== 「我当接收方」接收路径验证 ===\n");

  // 让页面完成身份初始化
  await P.Crypto.ensureIdentity();
  P.refreshPills();

  // ---------- 初始状态 ----------
  check(pillKey().textContent === "密钥 未就绪",
        "初始：药丸显示「密钥 未就绪」", pillKey().textContent);
  check(pillKey().classList.contains("warn"),
        "初始：药丸是 warn（黄）", pillKey().className);
  check(input().disabled === true,
        "初始：输入框禁用");

  // ---------- 第 3 步：收下我自己的公钥，应该被拒 ----------
  const selfEnv = P.Crypto.myKeyEnvelope();
  check(selfEnv.indexOf("E2E1-K.") === 0, "自己公钥的信封前缀正确");
  P.handleIncoming(selfEnv, "clipboard");
  await settle();

  check(logHas("这是你自己的公钥，忽略"),
        "收下自己的公钥 → 日志出现「这是你自己的公钥，忽略」",
        JSON.stringify(logLines().slice(-3)));
  check(pillKey().textContent === "密钥 未就绪",
        "收下自己的公钥 → 药丸保持未就绪（没被自己的公钥顶掉）", pillKey().textContent);
  check(input().disabled === true, "收下自己的公钥 → 输入框仍然禁用");

  // ---------- 第 2b 步：收下对方的公钥 ----------
  const peerKp = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"]);
  const peerRaw = new Uint8Array(await crypto.subtle.exportKey("raw", peerKp.publicKey));
  const peerEnv = P.PREFIX_KEY + P.b64uEncode(peerRaw);

  P.handleIncoming(peerEnv, "clipboard");
  await settle();

  check(logHas("已收到对方公钥，会话密钥协商完成"),
        "收下对方公钥 → 日志出现「✓ 已收到对方公钥，会话密钥协商完成」",
        JSON.stringify(logLines().slice(-3)));
  check(logHas("双方指纹应一致"),
        "收下对方公钥 → 日志提示双方指纹应一致");
  check(pillKey().textContent === "密钥 已就绪",
        "收下对方公钥 → 药丸变成「密钥 已就绪」", pillKey().textContent);
  check(pillKey().classList.contains("ok"),
        "收下对方公钥 → 药丸变绿（class 含 ok）", pillKey().className);
  check($("keyDot").classList.contains("on"),
        "收下对方公钥 → 弹窗圆点变绿");
  check($("keyText").textContent === "会话密钥已协商完成",
        "收下对方公钥 → 弹窗状态文字变成「会话密钥已协商完成」",
        $("keyText").textContent);
  check(input().disabled === false,
        "收下对方公钥 → 输入框解锁");
  check($("send").disabled === false,
        "收下对方公钥 → 发送按钮解锁");
  check($("keyFinger").style.display === "block",
        "收下对方公钥 → 指纹行显示出来");
  check(String($("keyFinger").textContent).indexOf("我的指纹") === 0,
        "收下对方公钥 → 指纹行内容是「我的指纹 …」",
        $("keyFinger").textContent);
  check(String(input().placeholder).indexOf("输入内容") === 0,
        "收下对方公钥 → 输入框提示改成可发送",
        input().placeholder);

  // ---------- 坏公钥 ----------
  P.Crypto.clearPeer();
  P.refreshPills();
  check(pillKey().textContent === "密钥 未就绪", "clearPeer 后药丸回到未就绪");

  P.handleIncoming(P.PREFIX_KEY + "AAAA", "clipboard");   // 解码出来只有 3 字节
  await settle();
  check(logHas("公钥导入失败"),
        "收下格式不对的公钥 → 日志出现「公钥导入失败」",
        JSON.stringify(logLines().slice(-3)));
  check(pillKey().textContent === "密钥 未就绪",
        "收下格式不对的公钥 → 药丸保持未就绪（没被带坏）", pillKey().textContent);
  check(input().disabled === true, "收下格式不对的公钥 → 输入框仍禁用");

  // ---------- 非法 base64 ----------
  P.handleIncoming(P.PREFIX_KEY + "!!!!", "clipboard");
  await settle();
  check(logHas("公钥解析失败"), "收下非法 base64 → 日志出现「公钥解析失败」",
        JSON.stringify(logLines().slice(-3)));

  // ---------- 第 2b 之后再来一次，确认可重复 ----------
  const peerKp2 = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"]);
  const peerRaw2 = new Uint8Array(await crypto.subtle.exportKey("raw", peerKp2.publicKey));
  P.handleIncoming(P.PREFIX_KEY + P.b64uEncode(peerRaw2), "clipboard");
  await settle();
  check(pillKey().textContent === "密钥 已就绪",
        "换一把新公钥再收一次 → 药丸重新变绿", pillKey().textContent);
  check(input().disabled === false, "换一把新公钥再收一次 → 输入框解锁");

  // ---------- 手动收下（粘贴框那条路） ----------
  P.Crypto.clearPeer();
  P.refreshPills();

  const kp3 = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"]);
  const raw3 = new Uint8Array(await crypto.subtle.exportKey("raw", kp3.publicKey));
  const env3 = P.PREFIX_KEY + P.b64uEncode(raw3);

  check(P.recvManual("", "测试") === false, "手动收下：空内容被拒");
  check(logHas("内容是空的"), "手动收下：空内容有提示");

  check(P.recvManual("你好呀", "测试") === false, "手动收下：不是加密消息被拒");
  check(logHas("不像加密消息"), "手动收下：非加密内容有提示");

  check(P.recvManual(env3, "测试") === true, "手动收下：合法公钥信封被接受");
  await settle();
  check(pillKey().textContent === "密钥 已就绪",
        "手动收下对方公钥 → 药丸变绿", pillKey().textContent);
  check(input().disabled === false, "手动收下对方公钥 → 输入框解锁");

  // 从微信/记事本粘过来常带换行和空格，必须能容忍
  P.Crypto.clearPeer();
  P.refreshPills();
  const messy = "  " + env3.slice(0, 40) + "\n" + env3.slice(40) + "  \r\n";
  check(P.recvManual(messy, "带空白") === true, "手动收下：带换行和空格的也能收");
  await settle();
  check(pillKey().textContent === "密钥 已就绪",
        "手动收下（带空白）→ 药丸同样变绿", pillKey().textContent);

  // ---------- 自己的公钥：合法信封，但必须被拒 ----------
  P.Crypto.clearPeer();
  P.refreshPills();
  P.recvManual(P.Crypto.myKeyEnvelope(), "测试");
  await settle();
  check(logHas("这是你自己的公钥，忽略"),
        "手动收下自己的公钥 → 被拒", JSON.stringify(logLines().slice(-2)));
  check(pillKey().textContent === "密钥 未就绪",
        "手动收下自己的公钥 → 药丸保持黄色", pillKey().textContent);

  // ---------- 发出一份密文，确认手动收下也能解密 ----------
  const kp4 = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"]);
  const raw4 = new Uint8Array(await crypto.subtle.exportKey("raw", kp4.publicKey));
  P.recvManual(P.PREFIX_KEY + P.b64uEncode(raw4), "测试");
  await settle();
  const envMsg = await P.Crypto.testEncryptAsPeer("手动词收到的回复");
  const nBubbles = $(("log")).children.length;
  check(P.recvManual(envMsg, "测试") === true, "手动收下：密文信封被接受");
  await settle();
  check($(("log")).children.length > nBubbles, "手动收下密文 → 日志里多出一条气泡");

  // ---------- 整块多行（微信里多选消息后一次复制）----------
  P.Crypto.clearPeer();
  P.refreshPills();

  const kp5 = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"]);
  const raw5 = new Uint8Array(await crypto.subtle.exportKey("raw", kp5.publicKey));
  const envKey5 = P.PREFIX_KEY + P.b64uEncode(raw5);

  const batch1 = ["这是一条没加密的普通消息", "我: " + envKey5].join("\n");
  check(P.recvManual(batch1, "多行") === true, "整块多行：被接受");
  await settle();
  check(pillKey().textContent === "密钥 已就绪",
        "整块多行：里面的公钥被摘出来并生效", pillKey().textContent);
  check(logHas("从整块剪贴板里解析出"), "整块多行：日志报告解析出几条");

  const envMsg5 = await P.Crypto.testEncryptAsPeer("多行里的密文");
  const before5 = $(("log")).children.length;
  P.recvManual("张三: 你好\n我: " + envMsg5, "多行");
  await settle();
  check($(("log")).children.length > before5,
        "整块多行：密文行被解密并产生气泡");

  P.recvManual("随便一句\n又一句", "多行");
  await settle();
  check(logHas("从整块剪贴板里解析出"), "整块多行：纯中文多行也逐条显示");

  // ---------- 解析微信导出的消息文本 ----------
  // 格式来自实测：拖选消息后 Ctrl+C，微信放进剪贴板的就是这样
  const dump = [
    "Pinavia",
    "2026年09月18日 17:05",
    "E2E1-K.BDSEQpquWuhD",
    "",
    "Pinavia",
    "2026年09月18日 17:06",
    "【测试】桥自检",
    "",
    "Pinavia",
    "2026年09月18日 17:18",
    "123123",
  ].join("\n");

  const parsed = P.parseWeChatDump(dump);
  check(parsed.length === 3, "解析微信导出：拆出 3 条", JSON.stringify(parsed));
  check(parsed[0] === "E2E1-K.BDSEQpquWuhD",
        "解析微信导出：第 1 条正文正确（剥掉发送者和时间）", parsed[0]);
  check(parsed[2] === "123123", "解析微信导出：第 3 条正文正确", parsed[2]);

  const p2 = P.parseWeChatDump("某人\n2026年09月18日 10:00\n第一行\n第二行");
  check(p2.length === 1 && p2[0] === "第一行\n第二行",
        "解析微信导出：多行正文留在同一条里（不被拆散）", JSON.stringify(p2));

  const p3 = P.parseWeChatDump("就一句话");
  check(p3.length === 1 && p3[0] === "就一句话",
        "解析微信导出：没有时间戳也能兜住", JSON.stringify(p3));

  // ---------- 拉取结果的处理（走 onPulled，跟桥回了 pulled 一样）----------
  P.Crypto.clearPeer();
  P.refreshPills();

  const kp9 = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"]);
  const raw9 = new Uint8Array(await crypto.subtle.exportKey("raw", kp9.publicKey));
  const dumpForPull = [
    "Pinavia", "2026年09月18日 17:00", "一条中文消息",
    "", "Pinavia", "2026年09月18日 17:01", P.PREFIX_KEY + P.b64uEncode(raw9),
  ].join("\n");

  const n0 = $(("log")).children.length;
  P.onPulled({ ok: true, len: dumpForPull.length, text: dumpForPull });
  await settle();
  check($(("log")).children.length > n0, "拉取结果：产生了气泡");
  check(pillKey().textContent === "密钥 已就绪",
        "拉取结果：里面的公钥被摘出来并生效", pillKey().textContent);
  check(logHas("从微信拉回"), "拉取结果：日志报告拉回几条",
        JSON.stringify(logLines().slice(-3)));

  P.onPulled({ ok: false });
  check(logHas("拉取失败"), "拉取结果：ok=false 时有明确提示");

  // ---------- 增量：每次拉到的都是"看得见的一整屏"，相邻两次大量重叠 ----------
  const A = ["一", "二", "三", "四"];
  check(JSON.stringify(P.onlyNewMessages([], A)) === JSON.stringify(A),
        "增量：第一次拉取全都是新的");
  check(P.onlyNewMessages(A, A).length === 0,
        "增量：整屏没变 -> 没有新的");
  check(JSON.stringify(P.onlyNewMessages(A, ["三", "四"])) === "[]",
        "增量：往上滚了一屏（整屏都见过）-> 没有新的");
  check(JSON.stringify(P.onlyNewMessages(A, ["二", "三", "四", "五"])) === '["五"]',
        "增量：多了一条 -> 只挑出那一条",
        JSON.stringify(P.onlyNewMessages(A, ["二", "三", "四", "五"])));
  check(JSON.stringify(P.onlyNewMessages(["一", "二"], ["九", "十"])) === '["九","十"]',
        "增量：完全接不上 -> 当全是新的");

  // ---------- 自动拉取（桥在微信切到前台时自己触发，页面没点按钮）----------
  const dumpAuto = ["Pinavia", "2026年09月18日 17:30", "自动拉到的第一条"].join("\n");
  const a0 = $(("log")).children.length;
  P.onPulled({ ok: true, len: dumpAuto.length, text: dumpAuto, auto: true });
  await settle();
  check($(("log")).children.length > a0, "自动拉取：第一次会处理");
  check(logHas("自动拉到"), "自动拉取：日志说明这是自动拉的");

  const a1 = $(("log")).children.length;
  P.onPulled({ ok: true, len: dumpAuto.length, text: dumpAuto, auto: true });
  await settle();
  check($(("log")).children.length === a1,
        "自动拉取：同一屏内容第二次不再重复处理",
        $(("log")).children.length + " vs " + a1);

  const a2 = $(("log")).children.length;
  P.onPulled({ ok: false, auto: true });
  await settle();
  check($(("log")).children.length === a2, "自动拉取：失败保持安静，不刷屏");

  const dumpAuto2 = dumpAuto + "\n\nPinavia\n2026年09月18日 17:31\n自动拉到的第二条";
  const a3 = $(("log")).children.length;
  P.onPulled({ ok: true, len: dumpAuto2.length, text: dumpAuto2, auto: true });
  await settle();
  check($(("log")).children.length > a3, "自动拉取：多了一条就继续处理");

  // ---------- 会话选择：桥回的 tables 必须真的到界面上（踩过：被静默丢掉，
  //            于是面板永远卡在"正在识别…"，而桥其实 1 秒就回了）----------
  console.log("\n--- 会话选择（tables / bind）---");
  {
    const sock = globalThis.__sockets[globalThis.__sockets.length - 1];
    check(!!sock, "拿到了页面用的那个 WebSocket 桩");
    sock.readyState = 1;
    sock.onopen();

    $("pillBind").click();
    check($("popBind").classList.contains("show"), "点 🔗 胶囊展开会话面板（挂在胶囊下面）");
    check(sock.sent.some(t => t.indexOf('"tables"') >= 0),
          "展开时就去向桥要 tables", JSON.stringify(sock.sent));

    const list = [
      { table:"Msg_9e20f478899dc29eb19741386f9343c8", md5:"9e20f478899dc29eb19741386f9343c8",
        who:"filehelper", time:1789744105, type:1, how:"表", preview:"E2E1-K.BCYeP1lW" },
      { table:"Msg_2ba0e1d0acc5276865a36ae03e475d90", md5:"2ba0e1d0acc5276865a36ae03e475d90",
        who:"wxid_pno349onlek322", time:1789742614, type:244813135921, how:"表", preview:"这个叫极简" },
      { table:"Msg_72ec7dcfc3cd083be2202fb77ca20f1b", md5:"72ec7dcfc3cd083be2202fb77ca20f1b",
        who:"21274956184@chatroom", time:1789737665, type:3, how:"页映像", preview:"[图片]" },
    ];
    sock.onmessage({ data: JSON.stringify({ type:"tables", list: list }) });

    const rows = $("sessionList").children;
    check(rows.length === list.length + 1, "面板里出现 3 条会话 + 1 条「自动」",
          rows.length + " 行");
    check(String($("sessionHint").textContent).indexOf("3") >= 0,
          "标题写清识别到几个", String($("sessionHint").textContent));
    const row1 = rows[1].children[1].children;   // [对方, 最新消息正文]（桩 DOM 不聚合 textContent）
    check(String(row1[0].textContent).indexOf("filehelper") >= 0,
          "第一条是 filehelper", String(row1[0].textContent));
    check(String(row1[1].textContent).indexOf("E2E1-K.") >= 0,
          "第一条带着它的最新消息正文", String(row1[1].textContent));

    rows[2].click();
    check(sock.sent.some(t => t.indexOf('"bind"') >= 0 && t.indexOf("wxid_pno349onlek322") >= 0),
          "点一条会发 bind（带 session，桥才能按 wxid 定位）",
          JSON.stringify(sock.sent.slice(-1)));

    sock.onmessage({ data: JSON.stringify({
      type:"bound", table:"Msg_2ba0e1d0acc5276865a36ae03e475d90", session:"wxid_pno349onlek322",
      who:"wxid_pno349onlek322" }) });
    check(String($("pillBind").textContent).indexOf("wxid_pno349onlek322") >= 0,
          "bound 回执让顶栏胶囊显示已绑定谁", String($("pillBind").textContent));
    check($("pillBind").classList.contains("ok"), "绑定后胶囊变绿");
    check($("popBind").classList.contains("show") === false, "绑定后面板收起来");

    // 刷新按钮：再问一次（"重新识别最新的消息"）
    const before = sock.sent.length;
    $("pillBind").click();          // 再展开一次
    $("btnRefreshSessions").click();
    check(sock.sent.length > before && sock.sent[sock.sent.length - 1].indexOf('"tables"') >= 0,
          "「🔄 刷新」会重新要一遍 tables");

    // autoPull 回执（同一个静默丢弃 bug 的另一个受害者）
    sock.onmessage({ data: JSON.stringify({ type:"autoPull", on:false }) });
    check($("chkAutoPull").checked === false, "autoPull 回执能同步到开关");

    // 诊断：识别失败时要把"卡在哪一步"原样显示出来
    $("btnDiag").click();
    check(sock.sent[sock.sent.length - 1].indexOf('"diag"') >= 0, "「🩺 诊断」会向桥要诊断");
    sock.onmessage({ data: JSON.stringify({
      type:"diag", text:"微信窗口: pid=19036；严格页 1158 张；会话行 73 条" }) });
    check($("diagBox").hidden === false, "诊断面板展开");
    check(String($("diagBox").textContent).indexOf("会话行 73") >= 0,
          "诊断内容原样贴在面板里", String($("diagBox").textContent));
    check(String($("diagBox").textContent).indexOf("桥地址") >= 0,
          "诊断里带上了 页面/桥地址 这一段（哪台机器在识别，一眼看清）");
    check(String($("diagBox").textContent).indexOf("本机回环") >= 0,
          "并说清 127.0.0.1 意味着桥必须在这台机器上跑");

    // 一个都识别不出来时：面板报错 + 自动去要诊断
    const sentBeforeEmpty = sock.sent.length;
    sock.onmessage({ data: JSON.stringify({ type:"tables", list: [] }) });
    check(String($("sessionHint").textContent).indexOf("0") >= 0, "空列表时标题显示 0 个");
    check(sock.sent.slice(sentBeforeEmpty).some(t => t.indexOf('"diag"') >= 0),
          "空列表时自动向桥要诊断（不用用户自己找）");
  }

  // ---------- 回声：自己发出去的密文被桥读回来时，不能再当对方发的 ----------
  console.log("\n--- 自己发的不能认成对方发的 ---");
  {
    const sock2 = globalThis.__sockets[globalThis.__sockets.length - 1];
    sock2.readyState = 1;
    if (sock2.onopen) sock2.onopen();

    const mine = "E2E1-M.2222222222222222222222222222222222222222";
    check(P.sendViaBridge(mine, "测试") === true, "自己发一条出去（走真实发送出口）");
    check(P.isMyEcho(mine) === true, "发出的正文被记下来了");

    /** 日志里"气泡"（row2）的条数 —— 回声绝不能变成气泡 */
    const bubbles = () => $(("log")).children.filter(c => String(c.className).indexOf("row2") === 0).length;

    const b0 = bubbles();
    P.handleIncoming(mine, "wechat-pull");
    check(logHas("忽略（回声）"), "桥把自己发的那条读回来 -> 认出来并忽略");
    check(bubbles() === b0, "回声不产生气泡（不能变成对方发来的）",
          bubbles() + " vs " + b0);

    // 从手机发的那条（页面没发过）必须照常收下 —— 文件传输助手的回路测试
    // 就是靠这条路
    const fromPhone = "E2E1-M.3333333333333333333333333333333333333333";
    check(P.isMyEcho(fromPhone) === false, "没发过的正文不会被当回声");
    const nBeforePhone = $(("log")).children.length;
    P.handleIncoming(fromPhone, "wechat-pull");
    const added = $(("log")).children.slice(nBeforePhone).map(c => String(c.textContent));
    check(added.every(t => t.indexOf("忽略（回声）") < 0),
          "手机发的那条不走回声分支（回路测试不受影响）", JSON.stringify(added));
  }

  // ---------- 公钥发送确认层 ----------
  console.log("\n--- 公钥发送确认层 ---");
  {
    const sock = globalThis.__sockets[globalThis.__sockets.length - 1];
    sock.readyState = 1;
    if (sock.onopen) sock.onopen();
    const clip = globalThis.__clipboardState;
    clip.mode = "none";
    clip.writes.length = 0;
    document.execCommand = () => false;
    $("popKey").classList.remove("show");
    $("confirmModal").classList.remove("show");

    // 第一次点击只打开面板和确认框；确认前绝不产生帧或剪贴板写入。
    const frame0 = sock.sent.length;
    $("pillKey").click();
    await settle();
    check($("popKey").classList.contains("show"), "点「密钥」仍会打开密钥面板");
    check($("confirmModal").classList.contains("show"), "点「密钥」先打开页面内确认框");
    check($("confirmTitle").textContent === "确认发送公钥", "确认框标题明确为发送公钥");
    check(String($("confirmBody").textContent).indexOf("当前窗口中获得焦点的会话") >= 0,
          "确认正文说明发送到当前微信焦点会话");
    check(String($("confirmBody").textContent).indexOf(P.Crypto.myFingerprint()) >= 0,
          "确认正文只展示本机公钥指纹");
    check($("confirmSubmit").textContent === "确认发送", "发送确认按钮文案正确");
    check(document.activeElement === $("confirmCancel"), "确认框默认焦点在取消");
    check(sock.sent.length === frame0, "确认前没有 WebSocket 帧");
    check(clip.writes.length === 0, "确认前没有剪贴板写入");

    const fpBeforeCancel = P.Crypto.myFingerprint();
    $("confirmCancel").click();
    await settle();
    check(!$("confirmModal").classList.contains("show"), "取消后确认框关闭");
    check(sock.sent.length === frame0 && clip.writes.length === 0,
          "取消后 WebSocket 和剪贴板均无副作用");
    check(P.getMyKeySent() === false, "取消不改变 myKeySent");
    check(P.Crypto.myFingerprint() === fpBeforeCancel, "取消保留当前指纹");
    check(document.activeElement === $("pillKey"), "取消后焦点恢复到原入口");

    // Escape 与遮罩关闭都必须走同一取消路径。
    $("pillKey").click(); // 关掉仍打开的面板
    $("pillKey").click();
    await settle();
    document.dispatchEvent({ type:"keydown", key:"Escape", preventDefault(){} });
    check(!$("confirmModal").classList.contains("show"), "Escape 取消确认");
    check(sock.sent.length === frame0 && clip.writes.length === 0,
          "Escape 取消没有发送或复制");

    $("pillKey").click(); // 面板当前仍开，先关
    $("pillKey").click();
    await settle();
    $("confirmModal").click();
    check(!$("confirmModal").classList.contains("show"), "点击确认遮罩取消");
    check(sock.sent.length === frame0 && clip.writes.length === 0,
          "遮罩取消没有发送或复制");

    // 快速重复点击只能保留一个请求。
    $("pillKey").click(); // 关面板
    $("pillKey").click();
    $("pillKey").click();
    await settle();
    check($("confirmModal").classList.contains("show"), "快速重复点击仍只有一个确认框");
    $("confirmCancel").click();
    await settle();

    // 顶栏在线确认：恰好一条原协议 send 帧，不走剪贴板。
    clip.mode = "none";
    clip.writes.length = 0;
    const onlineFrame0 = sock.sent.length;
    $("pillKey").click(); // 关掉上一次仍打开的面板
    $("pillKey").click();
    await settle();
    check($("confirmModal").classList.contains("show"), "顶栏密钥按钮打开确认框");
    $("confirmSubmit").click();
    check($("confirmSubmit").disabled === true, "提交后立即禁用确认按钮");
    await settle();
    check(!$("confirmModal").classList.contains("show"), "在线发送完成后确认框关闭");
    check(sock.sent.length === onlineFrame0 + 1, "在线确认恰好产生一条公钥发送帧",
          JSON.stringify(sock.sent.slice(-1)));
    check(clip.writes.length === 0, "在线发送成功不写剪贴板");
    check(P.getMyKeySent() === true, "在线发送成功记录 myKeySent");

    // 面板里的「复制我的公钥」即使桥在线也只写剪贴板，不发送 WebSocket 帧。
    clip.writes.length = 0;
    const copyFrame0 = sock.sent.length;
    const sentBeforeExplicitCopy = P.getMyKeySent();
    $("btnCopyKey").click();
    await settle();
    check($("confirmModal").classList.contains("show"), "复制公钥按钮也要求确认");
    $("confirmSubmit").click();
    await settle();
    check(sock.sent.length === copyFrame0, "复制公钥确认不产生 WebSocket 帧");
    check(clip.writes.length === 1, "复制公钥确认只写剪贴板");
    check(P.getMyKeySent() === sentBeforeExplicitCopy, "单纯复制不改变 myKeySent");

    // 在线但 WebSocket 在提交瞬间失败：复制兜底且不伪报成功帧。
    sock.throwOnSend = true;
    clip.mode = "none";
    clip.writes.length = 0;
    const failedFrame0 = sock.sent.length;
    $("pillKey").click(); // 关掉复制完成后仍打开的面板
    $("pillKey").click();
    await settle();
    $("confirmSubmit").click();
    await settle();
    check(sock.sent.length === failedFrame0, "WebSocket 发送失败没有伪造发送帧");
    check(clip.writes.length === 1, "WebSocket 发送失败走剪贴板兜底");
    check(logHas("桥发送失败，公钥已复制"), "WebSocket 失败提示明确要求手动粘贴");
    sock.throwOnSend = false;

    // 桥离线：确认后复制成功。
    sock.readyState = 0;
    if (sock.onclose) sock.onclose();
    clip.mode = "none";
    clip.writes.length = 0;
    $("pillKey").click(); // 关闭面板
    $("pillKey").click();
    await settle();
    $("confirmSubmit").click();
    await settle();
    check(clip.writes.length === 1, "桥离线确认后复制公钥");
    check(logHas("桥离线，公钥已复制"), "离线复制成功提示目标和手动粘贴");

    // API 拒绝但 execCommand 成功，以及两条路径都失败。
    clip.mode = "reject";
    clip.writes.length = 0;
    document.execCommand = () => true;
    $("btnCopyKey").click();
    await settle();
    $("confirmSubmit").click();
    await settle();
    check(P.copyText.lastMethod === "execCommand", "剪贴板 API 拒绝后尝试隐藏 textarea 兜底");
    check($("keyCopyFallback").classList.contains("show") === false,
          "兜底复制成功时不显示失败入口");

    const sentBeforeCopyFail = P.getMyKeySent();
    document.execCommand = () => false;
    $("btnCopyKey").click();
    await settle();
    $("confirmSubmit").click();
    await settle();
    check(P.copyText.lastMethod === "execCommand" && P.copyText.lastError,
          "API 和 textarea 都失败时返回失败结果");
    check($("keyCopyFallback").classList.contains("show"), "复制失败显示手动复制入口");
    check(String($("keyCopyError").textContent).indexOf("Edge") >= 0,
          "复制失败提示 Edge 权限和手动复制步骤");
    check(P.getMyKeySent() === sentBeforeCopyFail, "复制失败不改变 myKeySent");

    // 安装脚本手动复制弹层：API 拒绝状态必须留在 pasteMsg，兼容路径仍可复制。
    clip.mode = "reject";
    document.execCommand = () => false;
    P.showPasteFallback();
    await settle();
    check($("pasteModal").classList.contains("show"), "手动保存弹层仍能打开");
    check(String($("pasteMsg").textContent).indexOf("NotAllowedError") >= 0,
          "安装脚本 API 拒绝状态显示在 pasteMsg");
    document.execCommand = () => true;
    $("btnCopyPaste").click();
    await settle();
    check(String($("pasteMsg").textContent).indexOf("已复制") >= 0,
          "安装脚本兼容复制成功有明确提示");
    $("btnClosePaste").click();
    check(!$('pasteModal').classList.contains("show"), "安装脚本弹层可关闭");

    // 安装脚本在写 bridge.exe 前识别旧进程，避免覆盖锁定文件后继续误启动。
    const installer = P.buildInstaller();
    check(installer.indexOf("bridge preflight blocked installation") >= 0,
          "安装脚本包含旧 bridge 进程检测");
    check(installer.indexOf("Get-Process bridge") >= 0
          && installer.indexOf("Get-NetTCPConnection -LocalPort 8765") >= 0,
          "安装脚本同时检查 bridge 进程和 8765 端口");
    check(installer.indexOf("if errorlevel 1") >= 0,
          "安装脚本会在解包失败时立即停止");
  }

  // ---------- 重置密钥确认 ----------
  console.log("\n--- 重置密钥确认 ---");
  {
    const fp0 = P.Crypto.myFingerprint();
    const sent0 = P.getMyKeySent();
    $("btnReset").click();
    await settle();
    check($("confirmModal").classList.contains("show"), "重置密钥先打开确认框");
    check($("confirmSubmit").textContent === "重置密钥", "重置确认按钮文案正确");
    check(String($("confirmBody").textContent).indexOf("旧配对立即失效") >= 0,
          "重置确认说明旧配对失效");
    check(String($("confirmBody").textContent).indexOf(fp0) >= 0,
          "重置确认展示当前指纹");
    $("confirmCancel").click();
    await settle();
    check(P.Crypto.myFingerprint() === fp0 && P.getMyKeySent() === sent0,
          "取消重置保留会话和指纹");

    $("btnReset").click();
    await settle();
    $("confirmSubmit").click();
    await settle();
    check(!$("confirmModal").classList.contains("show"), "确认重置后确认框关闭");
    check(P.Crypto.myFingerprint() !== fp0, "确认重置后本机指纹更新");
    check(P.Crypto.hasPeer() === false, "确认重置清空对方公钥");
    check($("input").disabled === true, "确认重置后输入框重新禁用");
    check(P.getMyKeySent() === false, "确认重置清除 myKeySent");
  }

  console.log("");
  console.log("通过 " + pass + " / " + (pass + fail));
  if (fail) { console.log("有 " + fail + " 项失败"); process.exit(1); }
  console.log("接收路径与界面状态切换都是对的。");
  process.exit(0);
})().catch((e) => {
  console.error("测试本身崩了: " + (e && e.stack || e));
  process.exit(1);
});
