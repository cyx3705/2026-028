using System.Security.Cryptography;
using System.Text;

namespace LocalChatCrypto;

internal static class Wire
{
    public const string KeyPrefix = "OHK1.";
    public const string MsgPrefix = "OH1.";
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int SessionKeySize = 32;
    public const int MaxPlaintextBytes = 1024 * 1024;

    public static readonly byte[] Aad = "OH1"u8.ToArray();
    public static readonly byte[] HkdfInfo = "OH1-P256-AES256-GCM"u8.ToArray();

    public static string Compact(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
    }

    public static byte[] FromPrefixedBase64(string value, string prefix, string what)
    {
        var compact = Compact(value);
        if (!compact.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new ChatCryptoException($"{what}格式不对，应以 {prefix} 开头。");
        }

        try
        {
            return Convert.FromBase64String(compact[prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new ChatCryptoException($"{what}不是合法 Base64。", ex);
        }
    }

    public static string ToPrefixedBase64(string prefix, ReadOnlySpan<byte> data)
    {
        return prefix + Convert.ToBase64String(data);
    }

    public static string Fingerprint(ReadOnlySpan<byte> sessionKey)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(sessionKey, hash);
        var hex = Convert.ToHexString(hash[..8]);
        return $"{hex[..4]}-{hex[4..8]}-{hex[8..12]}-{hex[12..16]}";
    }

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    public static string Utf8String(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}
