using System;

namespace Proxy.Common.Crypto;

/// <summary>
/// 可插拔加密接口
/// </summary>
public interface ICryptoProvider : IDisposable
{
    /// <summary>
    /// 加密算法名称 (如 aes-256-gcm, chacha20-poly1305, plain)
    /// </summary>
    string AlgorithmName { get; }

    /// <summary>
    /// 计算加密后所需的目标缓冲区大小 (包含 Nonce, Tag 等元数据)
    /// </summary>
    int GetEncryptedLength(int plaintextLength);

    /// <summary>
    /// 加密明文并写入目标缓冲区
    /// </summary>
    /// <param name="plaintext">待加密明文</param>
    /// <param name="destination">目标输出缓冲区，长度必须 >= GetEncryptedLength</param>
    /// <returns>写入目标缓冲区的总字节数</returns>
    int Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> destination);

    /// <summary>
    /// 解密密文数据包
    /// </summary>
    /// <param name="encryptedPackage">密文包 (包含 Nonce, Tag 和密文)</param>
    /// <param name="destination">解密后明文目标缓冲区</param>
    /// <param name="bytesWritten">实际写入的明文字节数</param>
    /// <returns>解密与验签是否成功</returns>
    bool TryDecrypt(ReadOnlySpan<byte> encryptedPackage, Span<byte> destination, out int bytesWritten);

    /// <summary>
    /// 便捷方法：加密并返回新数组
    /// </summary>
    byte[] Encrypt(ReadOnlySpan<byte> plaintext);

    /// <summary>
    /// 便捷方法：解密并返回新数组，验签失败抛出异常
    /// </summary>
    byte[] Decrypt(ReadOnlySpan<byte> encryptedPackage);
}
