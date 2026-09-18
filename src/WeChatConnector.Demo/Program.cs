using System.Text;
using WeChatConnector;
using WeChatConnector.Demo;

Console.OutputEncoding = Encoding.UTF8;

Console.WriteLine("=== 微信 I/O 极简接口 · Demo（不连接真实微信）===");
Console.WriteLine();

// 1) 选一个加密器。真实场景换成你的本地会话加密器。
IPayloadCipher cipher = new DemoOnlyXorCipher();

// 2) 选一个通道。真实场景换成 WeChatFerry / Gewechat / Wechaty 实现。
await using var wechat = new MockWeChatChannel();

await wechat.StartAsync();
Console.WriteLine($"[通道] '{wechat.Name}' 已启动，IsRunning={wechat.IsRunning}");
Console.WriteLine();

using var cts = new CancellationTokenSource();

// --- 接收循环：微信来的密文 -> 解密 ---
var receiver = Task.Run(async () =>
{
    try
    {
        await foreach (var env in wechat.ReceiveAsync(cts.Token))
        {
            var plain = cipher.Decrypt(env.Ciphertext.Span);
            Console.WriteLine($"  [收到] {env.SenderName} ({env.SenderId}) 来自 {env.Source}");
            Console.WriteLine($"         密文（微信里实际可见）: {TextPayloadCodec.Encode(env.Ciphertext.Span)}");
            Console.WriteLine($"         明文（解密后）        : {plain}");
            Console.WriteLine();
        }
    }
    catch (OperationCanceledException)
    {
        // 正常退出
    }
});

// --- 发送：明文 -> 加密器 -> 密文 -> 微信 ---
const string outgoing = "你好，这是一条要发出去的明文";
var outgoingCipher = cipher.Encrypt(outgoing);

await wechat.SendAsync(WeChatTarget.Contact("wxid_alice"), outgoingCipher);

Console.WriteLine("[发出] target = contact:wxid_alice");
Console.WriteLine($"       明文         : {outgoing}");
Console.WriteLine($"       密文（微信里）: {TextPayloadCodec.Encode(outgoingCipher.Span)}");
Console.WriteLine();

// --- 模拟微信侧推来一条密文消息 ---
Console.WriteLine("[模拟] 微信侧推送了一条消息...");
wechat.SimulateInbound(
    senderId: "wxid_alice",
    senderName: "Alice",
    source: WeChatTarget.Contact("wxid_alice"),
    ciphertext: cipher.Encrypt("收到，这是我的回复"));

await Task.Delay(400);

cts.Cancel();
try
{
    await receiver;
}
catch (OperationCanceledException)
{
    // 正常退出
}

// --- 汇总 ---
Console.WriteLine($"[汇总] 本次共发出 {wechat.Sent.Count} 条消息：");
foreach (var m in wechat.Sent)
{
    Console.WriteLine($"  -> {m.Target}  {TextPayloadCodec.Encode(m.Ciphertext)}");
}

Console.WriteLine();

// --- 验证编码往返 ---
var probe = cipher.Encrypt("往返校验");
var encoded = TextPayloadCodec.Encode(probe.Span);
var roundTrip = cipher.Decrypt(TextPayloadCodec.Decode(encoded));
Console.WriteLine($"[校验] Base64Url 往返: {(roundTrip == "往返校验" ? "通过" : "失败")}");

await wechat.StopAsync();
Console.WriteLine($"[通道] 已停止，IsRunning={wechat.IsRunning}");
