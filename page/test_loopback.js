/**
 * 测试模式（假装第二端）的密码学验证。
 *
 * 做法：把 chat.html 里的 Crypto 模块原样抠出来，在 Node 里跑真实的 WebCrypto。
 * 不是照着逻辑重写一遍 —— 是同一份代码。所以这里通过，页面里就是通的。
 *
 * 验证的几件事：
 *   1. 假对方协商出的两把钥匙确实是分开的（sendKey != recvKey）
 *   2. 对方的接收键真的能解开我的出站密文（= 我的发送键）
 *   3. 对方的发送键加密的回复，我这边真能解开
 *   4. 重放被拒
 *   5. 篡改被拒
 *   6. 计数器真在递增，IV 不重复
 *
 * 跑法：node test_loopback.js
 */
"use strict";

const fs = require("fs");
const path = require("path");
const { webcrypto } = require("crypto");

// Node 18+ 自带 globalThis.crypto（webcrypto）；老版本才需要手动挂
if (!globalThis.crypto || !globalThis.crypto.subtle) {
  globalThis.crypto = webcrypto;
}

// Crypto 模块会读写 localStorage 存身份
const _store = {};
globalThis.localStorage = {
  getItem: (k) => (k in _store ? _store[k] : null),
  setItem: (k, v) => { _store[k] = String(v); },
  removeItem: (k) => { delete _store[k]; },
};

const html = fs.readFileSync(path.join(__dirname, "chat.html"), "utf8");
const m = html.match(/<script>([\s\S]*?)<\/script>/);
if (!m) { console.error("找不到 <script>"); process.exit(1); }

const script = m[1];
const cut = script.indexOf("var Bridge = (function(){");
if (cut < 0) { console.error("找不到 Bridge 起点，页面结构变了"); process.exit(1); }

// 只取 Bridge 之前的纯逻辑部分：常量 + 编码helper + Crypto 模块，全都不碰 DOM
const pure = script.slice(0, cut);

// 在同一个 eval 作用域里把 Crypto 交出来（strict 模式下 var 不会外泄）
eval(pure + "\n;globalThis.Crypto = Crypto; globalThis.PREFIX_MSG = PREFIX_MSG;");

let pass = 0, fail = 0;
function ok(name) { pass++; console.log("  [OK]   " + name); }
function bad(name, extra) { fail++; console.log("  [FAIL] " + name + (extra ? "  <- " + extra : "")); }
function check(cond, name, extra) { cond ? ok(name) : bad(name, extra); }

async function expectReject(promise, name) {
  let rejected = false, why = "";
  try { await promise; } catch (e) { rejected = true; why = e.message; }
  check(rejected, name, rejected ? "" : "居然没被拒绝");
  return why;
}

(async () => {
  console.log("=== 测试模式回路验证（真 WebCrypto）===\n");

  // ---- 本机身份 ----
  await Crypto.ensureIdentity();
  const myPub = Crypto.myPubRaw();
  check(!!myPub && myPub.length === 65, "本机 ECDH 公钥生成（65 字节未压缩点）");
  check(Crypto.ready() === false, "还没交换公钥时会话密钥未就绪");

  // ---- 假装第二端 ----
  const peerKp = await crypto.subtle.generateKey(
    { name: "ECDH", namedCurve: "P-256" }, true, ["deriveBits"]);
  const peerRaw = new Uint8Array(await crypto.subtle.exportKey("raw", peerKp.publicKey));
  check(peerRaw.length === 65, "假对方公钥生成");

  const sameAsMine = peerRaw.every((b, i) => b === myPub[i]);
  check(!sameAsMine, "假对方不是我自己");

  await Crypto.setPeerPub(peerRaw);
  check(Crypto.ready() === true, "协商完成，会话密钥就绪");
  check(Crypto.hasPeer() === true, "peerPub 已记录");

  // ---- 1. 我发 ----
  const text = "你好，这是一问一答";
  const env = await Crypto.encrypt(text);
  check(env.indexOf(PREFIX_MSG) === 0, "出站密文带 E2E1-M. 前缀", env.slice(0, 12));

  // ---- 2. 关键：两把钥匙真的是分开的 ----
  // 用我自己的接收键去解我自己的出站密文，必须失败。
  // 如果 setPeerPub 的切分写错了（两半给了同一把），这里会通过 —— 必须失败才算对。
  await expectReject(Crypto.decrypt(env),
    "我自己的接收键解不开我自己的出站密文（证明 sendKey != recvKey）");

  // ---- 3. 对方的接收键能解开我的出站密文 ----
  const seen = await Crypto.testDecryptAsPeer(env);
  check(seen === text, "对方用它的接收键解出原文", JSON.stringify(seen));

  // ---- 4. 对方回复 ----
  const reply = "收到：" + seen;
  const inEnv = await Crypto.testEncryptAsPeer(reply);
  check(inEnv.indexOf(PREFIX_MSG) === 0, "对方回复也是合法信封");
  check(inEnv !== env, "回复密文跟出站密文不同");

  // ---- 5. 我解开回复 ----
  const got = await Crypto.decrypt(inEnv);
  check(got === reply, "我解出对方的回复", JSON.stringify(got));

  // ---- 6. 重放被拒 ----
  await expectReject(Crypto.decrypt(inEnv), "同一条回复重放被拒");

  // ---- 7. 篡改被拒 ----
  const tampered = inEnv.slice(0, -6) + "AAAAAA";
  const why = await expectReject(Crypto.decrypt(tampered), "篡改后的密文被拒（AES-GCM 认证）");

  // ---- 8. 计数器递增、IV 不重复 ----
  const ivs = [];
  for (let i = 0; i < 3; i++) {
    const e = await Crypto.testEncryptAsPeer("msg" + i);
    ivs.push(e.slice(PREFIX_MSG.length, PREFIX_MSG.length + 16));
  }
  check(new Set(ivs).size === 3, "连续三条对方消息的 IV 各不相同");
  check(ivs[1] > ivs[0] && ivs[2] > ivs[1], "IV 单调递增（计数器在走）");

  // 对方的计数器和我的是独立的：我再发一条，计数从自己的序列继续
  const env2 = await Crypto.encrypt("第二条");
  const seen2 = await Crypto.testDecryptAsPeer(env2);
  check(seen2 === "第二条", "第二轮的出站密文同样可被对方解开");

  // ---- 9. 乱序检测：把旧计数重放回去 ----
  const replay2 = await Crypto.testEncryptAsPeer("第三条");
  await Crypto.decrypt(replay2);
  // 再造一条计数更小的（直接复用旧信封）必然被拒
  await expectReject(Crypto.decrypt(replay2), "重复计数被拒");

  console.log("");
  console.log("通过 " + pass + " / " + (pass + fail));
  if (fail) { console.log("有 " + fail + " 项失败"); process.exit(1); }
  console.log("测试模式的回路是通的。");
})().catch((e) => {
  console.error("测试本身崩了: " + (e && e.stack || e));
  process.exit(1);
});
