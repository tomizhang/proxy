using System;
using System.Security.Cryptography;

namespace Proxy.Common.Crypto;

/// <summary>
/// 自定义混淆加密算法 (Custom Stream Obfuscator with Dynamic Salt & HMAC)
/// 演示完全自定义的协议加密与混淆逻辑：
/// 结构: [Salt(8B)] [HMAC-SHA256-Prefix(8B)] [ObfuscatedPayload(NB)]
/// </summary>
public sealed class CustomObfsCryptoProvider : ICryptoProvider
{
    private const int SaltSize = 8;
    private const int HmacSize = 8;
    private const int Overhead = SaltSize + HmacSize;

    private readonly byte[] _key;
    private bool _disposed;

    public string AlgorithmName => "custom-obfs";

    public CustomObfsCryptoProvider(string password)
    {
        _key = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password));
    }

    public CustomObfsCryptoProvider(byte[] key)
    {
        _key = (byte[])key.Clone();
    }

    public int GetEncryptedLength(int plaintextLength) => Overhead + plaintextLength;

    public int Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        ThrowIfDisposed();
        int totalLen = GetEncryptedLength(plaintext.Length);
        if (destination.Length < totalLen)
        {
            throw new ArgumentException("目标缓冲区容量不足", nameof(destination));
        }

        Span<byte> salt = destination.Slice(0, SaltSize);
        RandomNumberGenerator.Fill(salt);

        Span<byte> payloadDst = destination.Slice(Overhead, plaintext.Length);

        // 混淆逻辑：基于 Salt + Key 的流式 Xor
        ulong state = BitConverter.ToUInt64(salt) ^ BitConverter.ToUInt64(_key.AsSpan(0, 8));
        for (int i = 0; i < plaintext.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            byte mask = (byte)(state ^ _key[i % _key.Length]);
            payloadDst[i] = (byte)(plaintext[i] ^ mask);
        }

        // 计算防篡改校验 HMAC
        Span<byte> hmacTarget = destination.Slice(SaltSize, HmacSize);
        Span<byte> fullHmac = stackalloc byte[32];
        using (var hmac = new HMACSHA256(_key))
        {
            hmac.TryComputeHash(destination.Slice(Overhead, plaintext.Length), fullHmac, out _);
        }
        fullHmac.Slice(0, HmacSize).CopyTo(hmacTarget);

        return totalLen;
    }

    public bool TryDecrypt(ReadOnlySpan<byte> encryptedPackage, Span<byte> destination, out int bytesWritten)
    {
        ThrowIfDisposed();
        bytesWritten = 0;
        if (encryptedPackage.Length < Overhead)
        {
            return false;
        }

        int plaintextLen = encryptedPackage.Length - Overhead;
        if (destination.Length < plaintextLen)
        {
            return false;
        }

        ReadOnlySpan<byte> salt = encryptedPackage.Slice(0, SaltSize);
        ReadOnlySpan<byte> hmacGiven = encryptedPackage.Slice(SaltSize, HmacSize);
        ReadOnlySpan<byte> payload = encryptedPackage.Slice(Overhead, plaintextLen);

        // 校验 HMAC
        Span<byte> fullHmac = stackalloc byte[32];
        using (var hmac = new HMACSHA256(_key))
        {
            hmac.TryComputeHash(payload, fullHmac, out _);
        }

        if (!CryptographicOperations.FixedTimeEquals(hmacGiven, fullHmac.Slice(0, HmacSize)))
        {
            return false; // HMAC 校验失败
        }

        // 解除混淆
        ulong state = BitConverter.ToUInt64(salt) ^ BitConverter.ToUInt64(_key.AsSpan(0, 8));
        for (int i = 0; i < plaintextLen; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            byte mask = (byte)(state ^ _key[i % _key.Length]);
            destination[i] = (byte)(payload[i] ^ mask);
        }

        bytesWritten = plaintextLen;
        return true;
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        byte[] buffer = new byte[GetEncryptedLength(plaintext.Length)];
        Encrypt(plaintext, buffer);
        return buffer;
    }

    public byte[] Decrypt(ReadOnlySpan<byte> encryptedPackage)
    {
        if (encryptedPackage.Length < Overhead)
        {
            throw new CryptographicException("密文包长度过短");
        }

        byte[] plain = new byte[encryptedPackage.Length - Overhead];
        if (!TryDecrypt(encryptedPackage, plain, out _))
        {
            throw new CryptographicException("CustomObfs 解密验签失败");
        }
        return plain;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            CryptographicOperations.ZeroMemory(_key);
            _disposed = true;
        }
    }
}
