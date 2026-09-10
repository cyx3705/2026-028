using System.Security.Cryptography;
using System.Text.Json;

namespace LocalChatCrypto;

/// <summary>
/// 双方协商后的会话密钥。用它加密/解密正文。
/// </summary>
public sealed class SessionKey : IDisposable
{
    private byte[]? _key;

    internal SessionKey(byte[] key)
    {
        if (key.Length != Wire.SessionKeySize)
        {
            throw new ChatCryptoException("会话密钥长度不正确。");
        }

        _key = key;
        Fingerprint = Wire.Fingerprint(key);
    }

    /// <summary>短指纹，双方应一致。可用电话核对。</summary>
    public string Fingerprint { get; }

    public static bool IsCiphertext(string? text)
    {
        return !string.IsNullOrWhiteSpace(text)
            && Wire.Compact(text).StartsWith(Wire.MsgPrefix, StringComparison.Ordinal);
    }

    public static bool IsPublicKey(string? text)
    {
        return !string.IsNullOrWhiteSpace(text)
            && Wire.Compact(text).StartsWith(Wire.KeyPrefix, StringComparison.Ordinal);
    }

    public string Encrypt(string plaintext)
    {
        ObjectDisposedException.ThrowIf(_key is null, this);
        ArgumentNullException.ThrowIfNull(plaintext);

        var plain = Wire.Utf8(plaintext);
        if (plain.Length > Wire.MaxPlaintextBytes)
        {
            throw new ChatCryptoException("明文超过 1MB。");
        }

        var nonce = RandomNumberGenerator.GetBytes(Wire.NonceSize);
        var ciphertext = new byte[plain.Length];
        var tag = new byte[Wire.TagSize];

        using (var aes = new AesGcm(_key, Wire.TagSize))
        {
            aes.Encrypt(nonce, plain, ciphertext, tag, Wire.Aad);
        }

        var packed = new byte[nonce.Length + ciphertext.Length + tag.Length];
        nonce.CopyTo(packed, 0);
        ciphertext.CopyTo(packed, nonce.Length);
        tag.CopyTo(packed, nonce.Length + ciphertext.Length);
        return Wire.ToPrefixedBase64(Wire.MsgPrefix, packed);
    }

    public string Decrypt(string ciphertext)
    {
        ObjectDisposedException.ThrowIf(_key is null, this);
        var packed = Wire.FromPrefixedBase64(ciphertext, Wire.MsgPrefix, "密文");
        if (packed.Length < Wire.NonceSize + Wire.TagSize)
        {
            throw new ChatCryptoException("密文太短。");
        }

        var nonce = packed.AsSpan(0, Wire.NonceSize);
        var tag = packed.AsSpan(packed.Length - Wire.TagSize, Wire.TagSize);
        var body = packed.AsSpan(Wire.NonceSize, packed.Length - Wire.NonceSize - Wire.TagSize);
        var plain = new byte[body.Length];

        try
        {
            using var aes = new AesGcm(_key, Wire.TagSize);
            aes.Decrypt(nonce, body, tag, plain, Wire.Aad);
        }
        catch (CryptographicException ex)
        {
            throw new ChatCryptoException("解密失败：密钥不对或密文被改过。", ex);
        }

        return Wire.Utf8String(plain);
    }

    /// <summary>导出会话密钥 JSON，只保存在本机。</summary>
    public string Export()
    {
        ObjectDisposedException.ThrowIf(_key is null, this);
        var record = new SessionRecord
        {
            V = 1,
            Key = Convert.ToBase64String(_key),
            Fingerprint = Fingerprint,
        };
        return JsonSerializer.Serialize(record);
    }

    public static SessionKey Import(string json)
    {
        SessionRecord record;
        try
        {
            record = JsonSerializer.Deserialize<SessionRecord>(json)
                ?? throw new ChatCryptoException("会话文件为空。");
        }
        catch (JsonException ex)
        {
            throw new ChatCryptoException("会话文件不是合法 JSON。", ex);
        }

        if (record.V != 1 || string.IsNullOrWhiteSpace(record.Key))
        {
            throw new ChatCryptoException("会话文件版本或字段不支持。");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(record.Key);
        }
        catch (FormatException ex)
        {
            throw new ChatCryptoException("会话密钥损坏。", ex);
        }

        var session = new SessionKey(key);
        if (!string.IsNullOrWhiteSpace(record.Fingerprint)
            && record.Fingerprint != session.Fingerprint)
        {
            session.Dispose();
            throw new ChatCryptoException("会话指纹与密钥不一致。");
        }

        return session;
    }

    public void Dispose()
    {
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }

    private sealed class SessionRecord
    {
        public int V { get; set; }
        public string Key { get; set; } = "";
        public string Fingerprint { get; set; } = "";
    }
}
