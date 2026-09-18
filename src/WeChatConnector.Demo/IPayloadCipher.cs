using System.Text;

namespace WeChatConnector.Demo;

/// <summary>
/// 上游「本地会话加密器」的接缝。
///
/// 真实实现请替换成你自己的加密器 —— 无论它是 HTTP 服务、Unix socket、
/// gRPC，还是可以直接调用的库，都只需要实现这两个方法。
/// </summary>
public interface IPayloadCipher
{
    /// <summary>明文 -&gt; 密文。</summary>
    ReadOnlyMemory<byte> Encrypt(string plaintext);

    /// <summary>密文 -&gt; 明文。</summary>
    string Decrypt(ReadOnlySpan<byte> ciphertext);
}

/// <summary>
/// 直通实现：不做任何处理。仅用于调试，观察链路上明文长什么样。
/// </summary>
public sealed class PlaintextCipher : IPayloadCipher
{
    /// <inheritdoc />
    public ReadOnlyMemory<byte> Encrypt(string plaintext) => Encoding.UTF8.GetBytes(plaintext);

    /// <inheritdoc />
    public string Decrypt(ReadOnlySpan<byte> ciphertext) => Encoding.UTF8.GetString(ciphertext);
}

/// <summary>
/// ⚠️ 演示用占位实现 —— <b>这不是加密，没有任何安全性</b>。
///
/// 它存在的唯一目的，是让 Demo 能直观展示「明文 → 密文」这一步发生在哪里。
/// 请勿在任何真实场景中使用；请把它替换成你的本地会话加密器。
/// </summary>
public sealed class DemoOnlyXorCipher : IPayloadCipher
{
    private const string DefaultKey = "demo-only-not-secure";
    private readonly byte[] _key;

    /// <summary>用一个字符串当密钥（仅为让演示可复现）。</summary>
    public DemoOnlyXorCipher(string key = DefaultKey) => _key = Encoding.UTF8.GetBytes(key);

    /// <inheritdoc />
    public ReadOnlyMemory<byte> Encrypt(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= _key[i % _key.Length];
        }

        return bytes;
    }

    /// <inheritdoc />
    public string Decrypt(ReadOnlySpan<byte> ciphertext)
    {
        var bytes = ciphertext.ToArray();
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= _key[i % _key.Length];
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
