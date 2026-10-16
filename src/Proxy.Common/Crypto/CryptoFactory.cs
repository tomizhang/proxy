using System;

namespace Proxy.Common.Crypto;

public static class CryptoFactory
{
    public const string Aes256Gcm = "aes-256-gcm";
    public const string ChaCha20Poly1305 = "chacha20-poly1305";
    public const string CustomObfs = "custom-obfs";
    public const string Plain = "plain";

    public static readonly string[] SupportedAlgorithms = 
    [
        Aes256Gcm,
        ChaCha20Poly1305,
        CustomObfs,
        Plain
    ];

    public static ICryptoProvider Create(string algorithm, string password)
    {
        return (algorithm?.Trim().ToLowerInvariant()) switch
        {
            Aes256Gcm or "aes" or "gcm" => new AesGcmCryptoProvider(password),
            ChaCha20Poly1305 or "chacha" or "chacha20" => new ChaCha20Poly1305CryptoProvider(password),
            CustomObfs or "custom" or "obfs" => new CustomObfsCryptoProvider(password),
            Plain or "none" => new PlainCryptoProvider(),
            _ => throw new NotSupportedException($"不支持的加密算法: {algorithm}")
        };
    }
}
