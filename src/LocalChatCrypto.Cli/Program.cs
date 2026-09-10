using LocalChatCrypto;

const string identityFile = "identity.json";
const string sessionFile = "session.json";

try
{
    if (args.Length == 0)
    {
        PrintHelp();
        return 1;
    }

    return args[0].ToLowerInvariant() switch
    {
        "gen" => CmdGen(),
        "pub" => CmdPub(),
        "agree" => CmdAgree(RequireArg(args, 1, "对方公钥 OHK1....")),
        "enc" => CmdEnc(RequireArg(args, 1, "明文")),
        "dec" => CmdDec(RequireArg(args, 1, "密文 OH1....")),
        "fp" => CmdFp(),
        "selftest" => CmdSelfTest(),
        "-h" or "--help" or "help" => PrintHelp(),
        _ => Fail($"未知命令: {args[0]}"),
    };
}
catch (ChatCryptoException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 2;
}

static int PrintHelp()
{
    Console.WriteLine("""
        本机协商加密

        用法:
          LocalChatCrypto.Cli gen
          LocalChatCrypto.Cli pub
          LocalChatCrypto.Cli agree <对方公钥>
          LocalChatCrypto.Cli enc <明文>
          LocalChatCrypto.Cli dec <密文>
          LocalChatCrypto.Cli fp
          LocalChatCrypto.Cli selftest

        本机文件: identity.json  私钥
                  session.json   会话密钥
        """);
    return 0;
}

static int CmdGen()
{
    if (File.Exists(identityFile))
    {
        return Fail($"{identityFile} 已存在，未覆盖。");
    }

    using var keys = KeyPair.Generate();
    File.WriteAllText(identityFile, keys.Export());
    Console.WriteLine(keys.PublicKey);
    return 0;
}

static int CmdPub()
{
    using var keys = LoadIdentity();
    Console.WriteLine(keys.PublicKey);
    return 0;
}

static int CmdAgree(string remotePublicKey)
{
    using var keys = LoadIdentity();
    using var session = keys.Agree(remotePublicKey);
    File.WriteAllText(sessionFile, session.Export());
    Console.WriteLine(session.Fingerprint);
    return 0;
}

static int CmdEnc(string plaintext)
{
    using var session = LoadSession();
    Console.WriteLine(session.Encrypt(plaintext));
    return 0;
}

static int CmdDec(string ciphertext)
{
    using var session = LoadSession();
    Console.WriteLine(session.Decrypt(ciphertext));
    return 0;
}

static int CmdFp()
{
    using var session = LoadSession();
    Console.WriteLine(session.Fingerprint);
    return 0;
}

static int CmdSelfTest()
{
    using var alice = KeyPair.Generate();
    using var bob = KeyPair.Generate();
    using var aliceSession = alice.Agree(bob.PublicKey);
    using var bobSession = bob.Agree(alice.PublicKey);

    if (aliceSession.Fingerprint != bobSession.Fingerprint)
    {
        return Fail("双方指纹不一致。");
    }

    const string text = "你好，协商加密。🙂";
    var cipher = aliceSession.Encrypt(text);
    var plain = bobSession.Decrypt(cipher);
    if (plain != text)
    {
        return Fail("加解密往返失败。");
    }

    var tampered = cipher[..^2] + (cipher[^1] == 'A' ? "B" : "A");
    try
    {
        bobSession.Decrypt(tampered);
        return Fail("篡改密文应当失败。");
    }
    catch (ChatCryptoException)
    {
        // expected
    }

    using var other = KeyPair.Generate();
    using var otherSession = other.Agree(alice.PublicKey);
    try
    {
        otherSession.Decrypt(cipher);
        return Fail("错误会话应当无法解密。");
    }
    catch (ChatCryptoException)
    {
        // expected
    }

    Console.WriteLine("selftest ok " + aliceSession.Fingerprint);
    return 0;
}

static KeyPair LoadIdentity()
{
    if (!File.Exists(identityFile))
    {
        throw new ChatCryptoException($"找不到 {identityFile}，先运行 gen。");
    }

    return KeyPair.Import(File.ReadAllText(identityFile));
}

static SessionKey LoadSession()
{
    if (!File.Exists(sessionFile))
    {
        throw new ChatCryptoException($"找不到 {sessionFile}，先运行 agree。");
    }

    return SessionKey.Import(File.ReadAllText(sessionFile));
}

static string RequireArg(string[] args, int index, string name)
{
    if (args.Length <= index || string.IsNullOrWhiteSpace(args[index]))
    {
        throw new ChatCryptoException($"缺少参数: {name}");
    }

    return args[index];
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
