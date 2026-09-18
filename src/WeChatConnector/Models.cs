namespace WeChatConnector;

/// <summary>发送目标类型。</summary>
public enum WeChatTargetKind
{
    /// <summary>单聊：联系人 wxid。</summary>
    Contact,

    /// <summary>群聊：群 ID（通常是 @chatroom 结尾）。</summary>
    Group,
}

/// <summary>
/// 一个微信发送目标。用工厂方法构造，避免把 wxid / roomId 搞混。
/// </summary>
public sealed record WeChatTarget
{
    private WeChatTarget(string id, WeChatTargetKind kind)
    {
        Id = id;
        Kind = kind;
    }

    /// <summary>联系人 wxid 或群 roomId。</summary>
    public string Id { get; }

    /// <summary>目标类型。</summary>
    public WeChatTargetKind Kind { get; }

    /// <summary>构造一个单聊目标。</summary>
    public static WeChatTarget Contact(string wxid) => new(Require(wxid), WeChatTargetKind.Contact);

    /// <summary>构造一个群聊目标。</summary>
    public static WeChatTarget Group(string roomId) => new(Require(roomId), WeChatTargetKind.Group);

    private static string Require(string id) =>
        string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException("目标 ID 不能为空", nameof(id))
            : id;

    public override string ToString() =>
        Kind == WeChatTargetKind.Group ? $"group:{Id}" : $"contact:{Id}";
}

/// <summary>
/// 一条从微信进来的消息。注意 <see cref="Ciphertext"/> 是密文，需要交给本地加密器解密。
/// </summary>
public sealed record WeChatEnvelope
{
    /// <summary>消息唯一 ID（用于幂等 / 去重）。</summary>
    public required string MessageId { get; init; }

    /// <summary>消息来自哪个会话（单聊或群）。</summary>
    public required WeChatTarget Source { get; init; }

    /// <summary>发送者 ID。</summary>
    public required string SenderId { get; init; }

    /// <summary>发送者昵称，可能取不到。</summary>
    public string? SenderName { get; init; }

    /// <summary>密文原始字节。</summary>
    public required ReadOnlyMemory<byte> Ciphertext { get; init; }

    /// <summary>接收时间。</summary>
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 一条已发出的消息记录，主要给测试做断言用。
/// </summary>
/// <param name="Target">发送目标。</param>
/// <param name="Ciphertext">发出的密文。</param>
/// <param name="SentAt">发送时间。</param>
public sealed record SentMessage(WeChatTarget Target, byte[] Ciphertext, DateTimeOffset SentAt);
