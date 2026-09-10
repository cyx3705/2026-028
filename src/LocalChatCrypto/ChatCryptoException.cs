namespace LocalChatCrypto;

public sealed class ChatCryptoException : Exception
{
    public ChatCryptoException(string message) : base(message)
    {
    }

    public ChatCryptoException(string message, Exception inner) : base(message, inner)
    {
    }
}
