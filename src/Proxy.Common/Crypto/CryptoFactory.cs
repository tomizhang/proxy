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

    public static string NormalizeAlgorithm(string? algorithm)
    {
        string raw = (algorithm ?? string.Empty).Trim().ToLowerInvariant();
        if (raw.Contains("aes-256-gcm") || raw == "aes" || raw == "gcm" || raw.Contains("消息加密"))
        {
            if (raw.Contains("chacha")) return ChaCha20Poly1305;
            return Aes256Gcm;
        }
        if (raw.Contains("chacha"))
        {
            return ChaCha20Poly1305;
        }
        if (raw.Contains("custom-obfs") || raw == "custom" || raw == "obfs" || raw.Contains("简单加密"))
        {
            return CustomObfs;
        }
        if (raw.Contains("plain") || raw == "none" || raw == "raw" || raw.Contains("急速") || raw.Contains("极速"))
        {
            return Plain;
        }

        return string.IsNullOrEmpty(raw) ? Aes256Gcm : raw;
    }

    public static ICryptoProvider Create(string algorithm, string password)
    {
        string normalized = NormalizeAlgorithm(algorithm);
        return normalized switch
        {
            Aes256Gcm => new AesGcmCryptoProvider(password),
            ChaCha20Poly1305 => new ChaCha20Poly1305CryptoProvider(password),
            CustomObfs => new CustomObfsCryptoProvider(password),
            Plain => new PlainCryptoProvider(),
            _ => throw new NotSupportedException($"不支持的加密算法: {algorithm}")
        };
    }
}
