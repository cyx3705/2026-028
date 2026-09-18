using System.Threading.Channels;

namespace WeChatConnector;

/// <summary>
/// 纯内存通道实现：<b>完全不连接微信</b>。
///
/// 用途：
/// 1. 在没有真实微信后端时，先把「加密器 -&gt; 通道 -&gt; 对端」整条链路跑通；
/// 2. 单元测试里断言「到底发出去了什么密文」；
/// 3. 作为写真实通道（WeChatFerry / Gewechat / Wechaty）时的对照基线。
/// </summary>
public sealed class MockWeChatChannel : IWeChatChannel
{
    private readonly Channel<WeChatEnvelope> _inbox =
        Channel.CreateUnbounded<WeChatEnvelope>(new UnboundedChannelOptions { SingleReader = true });

    private readonly List<SentMessage> _sent = [];
    private readonly Lock _gate = new();
    private long _seq;

    /// <inheritdoc />
    public string Name => "mock";

    /// <inheritdoc />
    public bool IsRunning { get; private set; }

    /// <summary>所有已发出的消息，供测试断言。</summary>
    public IReadOnlyList<SentMessage> Sent
    {
        get { lock (_gate) { return _sent.ToArray(); } }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        IsRunning = true;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken ct = default)
    {
        IsRunning = false;
        _inbox.Writer.TryComplete();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task SendAsync(WeChatTarget target, ReadOnlyMemory<byte> ciphertext, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (!IsRunning)
        {
            throw new InvalidOperationException("通道尚未启动，请先调用 StartAsync。");
        }

        lock (_gate)
        {
            _sent.Add(new SentMessage(target, ciphertext.ToArray(), DateTimeOffset.Now));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 模拟「微信侧推来一条消息」。真实实现里这一步由 SDK 的回调驱动。
    /// </summary>
    /// <param name="senderId">发送者 ID。</param>
    /// <param name="senderName">发送者昵称。</param>
    /// <param name="source">消息所属会话。</param>
    /// <param name="ciphertext">密文载荷。</param>
    public void SimulateInbound(
        string senderId,
        string? senderName,
        WeChatTarget source,
        ReadOnlyMemory<byte> ciphertext)
    {
        ArgumentNullException.ThrowIfNull(source);

        var envelope = new WeChatEnvelope
        {
            MessageId = Interlocked.Increment(ref _seq).ToString(),
            Source = source,
            SenderId = senderId,
            SenderName = senderName,
            Ciphertext = ciphertext.ToArray(),
            ReceivedAt = DateTimeOffset.Now,
        };

        _inbox.Writer.TryWrite(envelope);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<WeChatEnvelope> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var envelope in _inbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return envelope;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
