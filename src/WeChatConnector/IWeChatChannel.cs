namespace WeChatConnector;

/// <summary>
/// 微信输入 / 输出通道 —— 整个系统里唯一需要替换的「连接器接缝」。
///
/// 核心约定（很重要）：
/// 本接口<b>只搬运密文</b>，不关心明文语义，也<b>不做任何加解密</b>。
/// 加解密由上游的「本地会话加密器」负责，通过 IPayloadCipher 接进来。
///
/// 因此替换后端时，上层业务代码一行都不用改：
///   MockWeChatChannel      —— 纯内存，不连微信，用于跑通链路和单测
///   WeChatFerryChannel     —— 走 WeChatFerry.Net（Windows hook，需指定微信版本）
///   GewechatHttpChannel    —— 走 Gewechat REST API（语言无关，最干净）
///   WechatyServiceChannel  —— 走 Wechaty puppet-service（gRPC）
/// </summary>
public interface IWeChatChannel : IAsyncDisposable
{
    /// <summary>通道名称，用于日志与诊断。</summary>
    string Name { get; }

    /// <summary>通道是否处于运行状态。</summary>
    bool IsRunning { get; }

    /// <summary>启动通道（登录 / 建连 / 订阅回调）。</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>停止通道并释放底层连接。</summary>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// 把一段密文发送到指定微信目标。
    /// </summary>
    /// <param name="target">联系人（wxid）或群（roomId）。</param>
    /// <param name="ciphertext">密文原始字节；由具体实现负责编码成微信文本（通常是 Base64Url）。</param>
    /// <param name="ct">取消标记。</param>
    Task SendAsync(WeChatTarget target, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default);

    /// <summary>
    /// 持续接收微信侧推来的消息，<see cref="WeChatEnvelope.Ciphertext"/> 仍然是密文。
    /// </summary>
    IAsyncEnumerable<WeChatEnvelope> ReceiveAsync(CancellationToken ct = default);
}
