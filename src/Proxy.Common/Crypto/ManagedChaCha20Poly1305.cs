using System;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Proxy.Common.Crypto;

/// <summary>
/// RFC 8439 标准 ChaCha20-Poly1305 纯 C# 托管实现
/// 在 Windows CNG 不支持 ChaCha20Poly1305 的系统上提供 100% 跨平台兼容
/// </summary>
public static class ManagedChaCha20Poly1305
{
    public static void Encrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> plaintext,
        Span<byte> ciphertext,
        Span<byte> tag)
    {
        if (key.Length != 32) throw new ArgumentException("Key must be 32 bytes");
        if (nonce.Length != 12) throw new ArgumentException("Nonce must be 12 bytes");
        if (tag.Length != 16) throw new ArgumentException("Tag must be 16 bytes");
        if (ciphertext.Length < plaintext.Length) throw new ArgumentException("Ciphertext buffer too small");

        // 1. 生成一次性 Poly1305 密钥 (Counter = 0)
        Span<byte> polyKey = stackalloc byte[64];
        ChaCha20Block(key, nonce, 0, polyKey);

        // 2. 加密明文 (从 Counter = 1 开始)
        ChaCha20Process(key, nonce, 1, plaintext, ciphertext);

        // 3. 计算 Poly1305 MAC 标签: AAD || Pad || Ciphertext || Pad || len(AAD) || len(Ciphertext)
        Poly1305Mac(polyKey.Slice(0, 32), ReadOnlySpan<byte>.Empty, ciphertext.Slice(0, plaintext.Length), tag);
    }

    public static bool Decrypt(
        ReadOnlySpan<byte> key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        Span<byte> plaintext)
    {
        if (key.Length != 32 || nonce.Length != 12 || tag.Length != 16) return false;
        if (plaintext.Length < ciphertext.Length) return false;

        // 1. 生成一次性 Poly1305 密钥
        Span<byte> polyKey = stackalloc byte[64];
        ChaCha20Block(key, nonce, 0, polyKey);

        // 2. 校验 MAC
        Span<byte> computedTag = stackalloc byte[16];
        Poly1305Mac(polyKey.Slice(0, 32), ReadOnlySpan<byte>.Empty, ciphertext, computedTag);

        if (!CryptographicOperations.FixedTimeEquals(tag, computedTag))
        {
            return false;
        }

        // 3. 解密
        ChaCha20Process(key, nonce, 1, ciphertext, plaintext);
        return true;
    }

    private static void ChaCha20Process(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint initialCounter, ReadOnlySpan<byte> input, Span<byte> output)
    {
        Span<byte> block = stackalloc byte[64];
        uint counter = initialCounter;
        int offset = 0;

        while (offset < input.Length)
        {
            ChaCha20Block(key, nonce, counter++, block);
            int count = Math.Min(64, input.Length - offset);
            for (int i = 0; i < count; i++)
            {
                output[offset + i] = (byte)(input[offset + i] ^ block[i]);
            }
            offset += count;
        }
    }

    private static void ChaCha20Block(ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce, uint counter, Span<byte> output)
    {
        Span<uint> state = stackalloc uint[16];
        // 常量 "expand 32-byte k"
        state[0] = 0x61707865;
        state[1] = 0x3320646e;
        state[2] = 0x79622d32;
        state[3] = 0x6b206574;

        for (int i = 0; i < 8; i++)
        {
            state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key.Slice(i * 4, 4));
        }

        state[12] = counter;
        state[13] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(0, 4));
        state[14] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(4, 4));
        state[15] = BinaryPrimitives.ReadUInt32LittleEndian(nonce.Slice(8, 4));

        Span<uint> working = stackalloc uint[16];
        state.CopyTo(working);

        for (int i = 0; i < 10; i++)
        {
            // 列轮
            QR(ref working[0], ref working[4], ref working[8], ref working[12]);
            QR(ref working[1], ref working[5], ref working[9], ref working[13]);
            QR(ref working[2], ref working[6], ref working[10], ref working[14]);
            QR(ref working[3], ref working[7], ref working[11], ref working[15]);
            // 对角轮
            QR(ref working[0], ref working[5], ref working[10], ref working[15]);
            QR(ref working[1], ref working[6], ref working[11], ref working[12]);
            QR(ref working[2], ref working[7], ref working[8], ref working[13]);
            QR(ref working[3], ref working[4], ref working[9], ref working[14]);
        }

        for (int i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.Slice(i * 4, 4), working[i] + state[i]);
        }
    }

    private static void QR(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        a += b; d ^= a; d = BitOperations.RotateLeft(d, 16);
        c += d; b ^= c; b = BitOperations.RotateLeft(b, 12);
        a += b; d ^= a; d = BitOperations.RotateLeft(d, 8);
        c += d; b ^= c; b = BitOperations.RotateLeft(b, 7);
    }

    private static void Poly1305Mac(ReadOnlySpan<byte> key, ReadOnlySpan<byte> aad, ReadOnlySpan<byte> ciphertext, Span<byte> tag)
    {
        // 模数 P = 2^130 - 5
        BigInteger p = (BigInteger.One << 130) - 5;

        // r clamp: r &= 0x0ffffffc0ffffffc0ffffffc0fffffff
        byte[] rBytes = new byte[17];
        key.Slice(0, 16).CopyTo(rBytes);
        rBytes[3] &= 15;
        rBytes[7] &= 15;
        rBytes[11] &= 15;
        rBytes[15] &= 15;
        rBytes[4] &= 252;
        rBytes[8] &= 252;
        rBytes[12] &= 252;
        BigInteger r = new BigInteger(rBytes, isUnsigned: true, isBigEndian: false);

        // s: key[16..32]
        byte[] sBytes = new byte[17];
        key.Slice(16, 16).CopyTo(sBytes);
        BigInteger s = new BigInteger(sBytes, isUnsigned: true, isBigEndian: false);

        BigInteger acc = BigInteger.Zero;

        void ProcessBlock(ReadOnlySpan<byte> data)
        {
            int offset = 0;
            byte[] blockBytes = new byte[17];
            while (offset < data.Length)
            {
                int len = Math.Min(16, data.Length - offset);
                Array.Clear(blockBytes, 0, blockBytes.Length);
                data.Slice(offset, len).CopyTo(blockBytes);
                blockBytes[len] = 0x01; // 填充 1 字节
                BigInteger n = new BigInteger(blockBytes, isUnsigned: true, isBigEndian: false);
                acc = ((acc + n) * r) % p;
                offset += len;
            }
        }

        ProcessBlock(aad);
        ProcessBlock(ciphertext);

        // 拼接长度: 8字节 aad.len + 8字节 ciphertext.len
        byte[] lenBlock = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(lenBlock.AsSpan(0, 8), (ulong)aad.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(lenBlock.AsSpan(8, 8), (ulong)ciphertext.Length);
        
        byte[] lenWithOne = new byte[17];
        lenBlock.CopyTo(lenWithOne, 0);
        lenWithOne[16] = 0x01;
        BigInteger lenNum = new BigInteger(lenWithOne, isUnsigned: true, isBigEndian: false);
        acc = ((acc + lenNum) * r) % p;

        acc = (acc + s) % (BigInteger.One << 128);

        byte[] tagBytes = acc.ToByteArray(isUnsigned: true, isBigEndian: false);
        tag.Clear();
        tagBytes.AsSpan(0, Math.Min(16, tagBytes.Length)).CopyTo(tag);
    }
}
