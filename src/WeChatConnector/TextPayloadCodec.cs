namespace WeChatConnector;

/// <summary>
/// 把密文编码成「能塞进微信文本消息」的字符串，以及反向解码。
///
/// 为什么需要这一层：微信文本消息是字符串，而密文是任意字节，
/// 直接当 UTF-8 传会损坏。这里用 Base64Url（URL 安全、无填充、无 '+' '/' '='），
/// 避免各种网关 / 中间层对特殊字符做转义。
/// </summary>
public static class TextPayloadCodec
{
    /// <summary>密文字节 -&gt; Base64Url 字符串。</summary>
    public static string Encode(ReadOnlySpan<byte> ciphertext) =>
        Convert.ToBase64String(ciphertext)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    /// <summary>Base64Url 字符串 -&gt; 密文字节。</summary>
    public static byte[] Decode(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var s = text.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
            case 1: throw new FormatException("不是合法的 Base64Url 字符串");
        }

        return Convert.FromBase64String(s);
    }

    /// <summary>
    /// 判断一段文本看起来是不是本编码格式的载荷（用于过滤掉非机器人消息）。
    /// </summary>
    public static bool LooksLikePayload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 8)
        {
            return false;
        }

        foreach (var c in text)
        {
            var ok = c is >= 'A' and <= 'Z'
                     or >= 'a' and <= 'z'
                     or >= '0' and <= '9'
                     or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
