using System;

namespace Proxy.Common.Crypto;

/// <summary>
/// 明文提供者 (不加密，直通传输，适用于调试和基准测试)
/// </summary>
public sealed class PlainCryptoProvider : ICryptoProvider
{
    public string AlgorithmName => "plain";

    public int GetEncryptedLength(int plaintextLength) => plaintextLength;

    public int Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        plaintext.CopyTo(destination);
        return plaintext.Length;
    }

    public bool TryDecrypt(ReadOnlySpan<byte> encryptedPackage, Span<byte> destination, out int bytesWritten)
    {
        encryptedPackage.CopyTo(destination);
        bytesWritten = encryptedPackage.Length;
        return true;
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext) => plaintext.ToArray();

    public byte[] Decrypt(ReadOnlySpan<byte> encryptedPackage) => encryptedPackage.ToArray();

    public void Dispose()
    {
    }
}
