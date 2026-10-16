using System;
using System.Security.Cryptography;

namespace Proxy.Common.Crypto;

/// <summary>
/// AES-256-GCM 加密实现
/// 封装格式: [Nonce(12B)] [Tag(16B)] [Ciphertext(NB)]
/// </summary>
public sealed class AesGcmCryptoProvider : ICryptoProvider
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int Overhead = NonceSize + TagSize;

    private readonly byte[] _key;
    private bool _disposed;

    public string AlgorithmName => "aes-256-gcm";

    public AesGcmCryptoProvider(string password) : this(DeriveKey(password))
    {
    }

    public AesGcmCryptoProvider(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("AES-256-GCM 需要 32 字节 (256 位) 密钥", nameof(key));
        }
        _key = (byte[])key.Clone();
    }

    public static byte[] DeriveKey(string password)
    {
        return SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password));
    }

    public int GetEncryptedLength(int plaintextLength)
    {
        return Overhead + plaintextLength;
    }

    public int Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        ThrowIfDisposed();
        int totalLen = GetEncryptedLength(plaintext.Length);
        if (destination.Length < totalLen)
        {
            throw new ArgumentException("目标缓冲区容量不足", nameof(destination));
        }

        Span<byte> nonce = destination.Slice(0, NonceSize);
        Span<byte> tag = destination.Slice(NonceSize, TagSize);
        Span<byte> ciphertext = destination.Slice(Overhead, plaintext.Length);

        // 生成随机 Nonce
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

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

        ReadOnlySpan<byte> nonce = encryptedPackage.Slice(0, NonceSize);
        ReadOnlySpan<byte> tag = encryptedPackage.Slice(NonceSize, TagSize);
        ReadOnlySpan<byte> ciphertext = encryptedPackage.Slice(Overhead, plaintextLen);
        Span<byte> plaintext = destination.Slice(0, plaintextLen);

        try
        {
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            bytesWritten = plaintextLen;
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
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
            throw new CryptographicException("AES-GCM 解密或验签失败");
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
