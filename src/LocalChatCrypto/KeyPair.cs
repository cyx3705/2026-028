using System.Security.Cryptography;
using System.Text.Json;

namespace LocalChatCrypto;

/// <summary>
/// 本机身份密钥。公钥可以交给对方，私钥只保存在本机。
/// </summary>
public sealed class KeyPair : IDisposable
{
    private ECDiffieHellman _ecdh;

    private KeyPair(ECDiffieHellman ecdh)
    {
        _ecdh = ecdh;
        PublicKey = Wire.ToPrefixedBase64(Wire.KeyPrefix, ecdh.ExportSubjectPublicKeyInfo());
    }

    /// <summary>可粘贴的公钥，形如 OHK1.xxxx</summary>
    public string PublicKey { get; }

    public static KeyPair Generate()
    {
        return new KeyPair(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));
    }

    /// <summary>从本机保存的 JSON 恢复私钥。</summary>
    public static KeyPair Import(string json)
    {
        IdentityRecord record;
        try
        {
            record = JsonSerializer.Deserialize<IdentityRecord>(json)
                ?? throw new ChatCryptoException("身份文件为空。");
        }
        catch (JsonException ex)
        {
            throw new ChatCryptoException("身份文件不是合法 JSON。", ex);
        }

        if (record.V != 1 || string.IsNullOrWhiteSpace(record.Pkcs8))
        {
            throw new ChatCryptoException("身份文件版本或字段不支持。");
        }

        byte[] pkcs8;
        try
        {
            pkcs8 = Convert.FromBase64String(record.Pkcs8);
        }
        catch (FormatException ex)
        {
            throw new ChatCryptoException("身份文件私钥损坏。", ex);
        }

        var ecdh = ECDiffieHellman.Create();
        try
        {
            ecdh.ImportPkcs8PrivateKey(pkcs8, out _);
            return new KeyPair(ecdh);
        }
        catch (CryptographicException ex)
        {
            ecdh.Dispose();
            throw new ChatCryptoException("无法导入私钥。", ex);
        }
    }

    /// <summary>导出含私钥的 JSON，只允许写到本机文件。</summary>
    public string Export()
    {
        ObjectDisposedException.ThrowIf(_ecdh is null, this);
        var record = new IdentityRecord
        {
            V = 1,
            Pkcs8 = Convert.ToBase64String(_ecdh.ExportPkcs8PrivateKey()),
            Spki = Convert.ToBase64String(_ecdh.ExportSubjectPublicKeyInfo()),
        };
        return JsonSerializer.Serialize(record);
    }

    /// <summary>与对方公钥做 ECDH，得到会话密钥。</summary>
    public SessionKey Agree(string remotePublicKey)
    {
        ObjectDisposedException.ThrowIf(_ecdh is null, this);
        var spki = Wire.FromPrefixedBase64(remotePublicKey, Wire.KeyPrefix, "公钥");

        using var remote = ECDiffieHellman.Create();
        try
        {
            remote.ImportSubjectPublicKeyInfo(spki, out _);
        }
        catch (CryptographicException ex)
        {
            throw new ChatCryptoException("对方公钥无法解析。", ex);
        }

        byte[] shared;
        try
        {
            shared = _ecdh.DeriveRawSecretAgreement(remote.PublicKey);
        }
        catch (CryptographicException ex)
        {
            throw new ChatCryptoException("密钥协商失败。", ex);
        }

        try
        {
            var session = HKDF.DeriveKey(
                HashAlgorithmName.SHA256,
                shared,
                Wire.SessionKeySize,
                salt: null,
                info: Wire.HkdfInfo);
            return new SessionKey(session);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    public void Dispose()
    {
        _ecdh?.Dispose();
        _ecdh = null!;
    }

    private sealed class IdentityRecord
    {
        public int V { get; set; }
        public string Pkcs8 { get; set; } = "";
        public string Spki { get; set; } = "";
    }
}
