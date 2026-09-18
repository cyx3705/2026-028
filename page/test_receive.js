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
 * 跑法：node test_receive.js
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
    appendChild(c){ el.children.push(c); return c; },
    removeChild(c){ el.children = el.children.filter(x => x !== c); },
    remove(){},
    focus(){},
    select(){},
    click(){},
    addEventListener(){},
    querySelector(){ return null; },
    querySelectorAll(){ return []; },
    setAttribute(){},
    getBoundingClientRect(){ return { top:0, left:0, width:100, height:20 }; },
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

globalThis.document = {
  body: makeEl("body"),
  getElementById: (id) => $(id),
  querySelector: () => null,
  querySelectorAll: () => [],
  createElement: (t) => makeEl(t),
  addEventListener: () => {},
};

// Node 24 自带只读的 globalThis.navigator / location，赋不上就跳过 ——
// 页面只在 copyText 里用 navigator.clipboard，而 Node 的 navigator 没有它，
// 效果跟"浏览器里没有剪贴板 API"一样，正是我们要的分支
for (const [k, v] of [["navigator", { clipboard: undefined }],
                      ["location", { protocol: "file:", host: "", href: "file:///chat.html" }]]) {
  try { globalThis[k] = v; } catch (e) { /* 只读，沿用 Node 自带的 */ }
}
try { globalThis.navigator = globalThis.navigator; } catch (e) {}
globalThis.alert = () => {};

// WebSocket 桩：连不上也不回调，于是 Bridge 保持"未连接"且不排重试 —— 结果确定
globalThis.WebSocket = function(){
  this.readyState = 0;
  this.send = () => {};
  this.close = () => {};
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

const html = fs.readFileSync(path.join(__dirname, "chat.html"), "utf8");
const m = html.match(/<script>([\s\S]*?)<\/script>/);
if (!m) { console.error("找不到 <script>"); process.exit(1); }

// 在同一个 eval 作用域里把需要的东西交出来（strict 模式下声明不会外泄）
let P;
eval(m[1] + `
;globalThis.__P = {
  Crypto: Crypto,
  handleIncoming: handleIncoming,
  refreshPills: refreshPills,
  PREFIX_KEY: PREFIX_KEY,
  PREFIX_MSG: PREFIX_MSG,
  b64uEncode: b64uEncode,
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

  console.log("");
  console.log("通过 " + pass + " / " + (pass + fail));
  if (fail) { console.log("有 " + fail + " 项失败"); process.exit(1); }
  console.log("接收路径与界面状态切换都是对的。");
  process.exit(0);
})().catch((e) => {
  console.error("测试本身崩了: " + (e && e.stack || e));
  process.exit(1);
});
