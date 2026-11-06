using System;
using System.Buffers.Binary;
using System.Text;

namespace Proxy.Client.Core.Routing;

/// <summary>
/// 域名与主机名探测嗅探器 (用于从初始 TCP 载荷中提取 TLS SNI 域名或 HTTP Host 域名)
/// 使得 TUN 模式下即便发往 SOCKS5 的目标是 IP 地址，也能准确识别其实际访问的域名并执行跳过直连规则
/// </summary>
public static class DomainSniffer
{
    /// <summary>
    /// 尝试从载荷中提取真实目标域名 (支持 TLS Client Hello SNI 与 HTTP Host 请求头)
    /// </summary>
    public static string? SniffDomain(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty) return null;

        // 1. 尝试探测 TLS Client Hello (以 0x16 0x03 开头)
        if (data.Length >= 44 && data[0] == 0x16 && data[1] == 0x03)
        {
            var sni = SniffTlsSni(data);
            if (!string.IsNullOrEmpty(sni)) return sni;
        }

        // 2. 尝试探测 HTTP 纯文本请求头中的 Host:
        if (data.Length >= 10 &&
            (data.StartsWith("GET "u8) || data.StartsWith("POST "u8) ||
             data.StartsWith("CONNECT "u8) || data.StartsWith("HEAD "u8) ||
             data.StartsWith("PUT "u8) || data.StartsWith("DELETE "u8) ||
             data.StartsWith("OPTIONS "u8)))
        {
            var host = SniffHttpHost(data);
            if (!string.IsNullOrEmpty(host)) return host;
        }

        return null;
    }

    private static string? SniffTlsSni(ReadOnlySpan<byte> data)
    {
        try
        {
            // TLS Record Header: [ContentType(1), Version(2), Length(2)] -> 5 字节
            if (data.Length < 5) return null;
            if (data[0] != 0x16) return null; // 必须是 Handshake

            int recordLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(3, 2));

            int pos = 5;
            if (pos >= data.Length || data[pos] != 0x01) return null; // HandshakeType: 0x01 = Client Hello

            // Handshake Header: [Type(1), Length(3), Version(2), Random(32)]
            pos += 4; // 跳过 Type(1) + Length(3)
            pos += 2; // 跳过 Version(2)
            pos += 32; // 跳过 Random(32)

            if (pos >= data.Length) return null;

            // Session ID Length
            int sessionIdLen = data[pos];
            pos += 1 + sessionIdLen;
            if (pos + 2 > data.Length) return null;

            // Cipher Suites Length
            int cipherSuitesLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
            pos += 2 + cipherSuitesLen;
            if (pos + 1 > data.Length) return null;

            // Compression Methods Length
            int compMethodsLen = data[pos];
            pos += 1 + compMethodsLen;
            if (pos + 2 > data.Length) return null;

            // Extensions Length
            int extensionsLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
            pos += 2;
            int extensionsEnd = Math.Min(pos + extensionsLen, data.Length);

            while (pos + 4 <= extensionsEnd)
            {
                ushort extType = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
                ushort extLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos + 2, 2));
                pos += 4;

                if (pos + extLen > extensionsEnd) break;

                // Extension 0x0000: server_name (SNI)
                if (extType == 0x0000 && extLen >= 5)
                {
                    // ServerNameList: [Length(2)]
                    int serverNameListLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(pos, 2));
                    int snPos = pos + 2;
                    int snEnd = Math.Min(snPos + serverNameListLen, pos + extLen);

                    while (snPos + 3 <= snEnd)
                    {
                        byte nameType = data[snPos]; // 0 = host_name
                        ushort nameLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(snPos + 1, 2));
                        snPos += 3;

                        if (nameType == 0 && snPos + nameLen <= snEnd)
                        {
                            return Encoding.ASCII.GetString(data.Slice(snPos, nameLen)).Trim().ToLowerInvariant();
                        }
                        snPos += nameLen;
                    }
                }

                pos += extLen;
            }
        }
        catch
        {
            // 忽略解析畸形数据包
        }

        return null;
    }

    private static string? SniffHttpHost(ReadOnlySpan<byte> data)
    {
        try
        {
            int maxSearch = Math.Min(data.Length, 1024);
            string text = Encoding.ASCII.GetString(data.Slice(0, maxSearch));
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            foreach (var line in lines)
            {
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    string host = line.Substring(5).Trim();
                    int colon = host.IndexOf(':');
                    if (colon >= 0) host = host.Substring(0, colon);
                    return host.ToLowerInvariant();
                }
            }
        }
        catch { }

        return null;
    }
}
