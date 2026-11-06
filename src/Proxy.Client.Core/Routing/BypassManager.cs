using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;

namespace Proxy.Client.Core.Routing;

/// <summary>
/// 直连分流与绕过管理器 (例如 baidu.com, *.cn, 局域网, 私网 IP 等不走代理直接本地联网)
/// </summary>
public sealed class BypassManager
{
    private readonly HashSet<string> _exactMatches = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _wildcardSuffixes = new();
    private readonly List<Regex> _regexRules = new();

    public BypassManager(string rules)
    {
        ParseRules(rules);
    }

    /// <summary>
    /// 重新解析直连规则 (支持动态热更新)
    /// </summary>
    public void ParseRules(string rules)
    {
        lock (_exactMatches)
        {
            _exactMatches.Clear();
            _wildcardSuffixes.Clear();
            _regexRules.Clear();

            if (string.IsNullOrWhiteSpace(rules)) return;

            var tokens = rules.Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawToken in tokens)
            {
                string token = rawToken.Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(token) || token == "<local>")
                {
                    continue;
                }

                if (token.StartsWith("*."))
                {
                    // *.baidu.com -> 匹配 baidu.com 及其所有子域名
                    string root = token.Substring(2);
                    _exactMatches.Add(root);
                    _wildcardSuffixes.Add("." + root);
                }
                else if (token.Contains('*'))
                {
                    // 通用通配符 (如 192.168.* 或 *.cn)
                    string pattern = "^" + Regex.Escape(token).Replace("\\*", ".*") + "$";
                    _regexRules.Add(new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled));
                }
                else
                {
                    _exactMatches.Add(token);
                    _wildcardSuffixes.Add("." + token);
                }
            }
        }
    }

    /// <summary>
    /// 判断是否为本地回环、局域网私网或系统保留 IP 地址 (在任何模式下均应永远直连跳过)
    /// </summary>
    public static bool IsPrivateOrLocalAddress(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return true;
        host = host.Trim().ToLowerInvariant();

        // 1. 本地主机名或无点单名 (如 localhost, mypc)
        if (host == "localhost" || host.StartsWith("127.") || !host.Contains('.'))
        {
            if (!IPAddress.TryParse(host, out _))
            {
                // 单名内网主机 (如 intranet, nas, router)
                return true;
            }
        }

        // 2. IP 地址精确网段判定
        if (IPAddress.TryParse(host, out var ip))
        {
            if (IPAddress.IsLoopback(ip)) return true;

            var bytes = ip.GetAddressBytes();
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && bytes.Length == 4)
            {
                byte b0 = bytes[0];
                byte b1 = bytes[1];

                // 10.0.0.0/8 (私网)
                if (b0 == 10) return true;

                // 172.16.0.0/12 (私网 172.16.0.0 - 172.31.255.255)
                if (b0 == 172 && b1 >= 16 && b1 <= 31) return true;

                // 192.168.0.0/16 (局域网)
                if (b0 == 192 && b1 == 168) return true;

                // 169.254.0.0/16 (链路本地 Link-Local)
                if (b0 == 169 && b1 == 254) return true;

                // 100.64.0.0/10 (运营商级 NAT CGNAT)
                if (b0 == 100 && (b1 & 0xC0) == 64) return true;

                // 127.0.0.0/8 (回环) 或 0.0.0.0
                if (b0 == 127 || b0 == 0) return true;

                // 224.0.0.0/4 (组播) 与 255.255.255.255 (广播)
                if (b0 >= 224) return true;
            }
            else if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast) return true;

                // Unique Local Address: fc00::/7 (fc00 - fdff)
                if (bytes.Length >= 2 && (bytes[0] & 0xFE) == 0xFC) return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 判断目标域名或 IP 是否应当跳过代理 (直接本地直连联网)
    /// </summary>
    public bool ShouldBypass(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim().ToLowerInvariant();

        // 1. 本地回环、内网私有 IP 与单名主机直连跳过
        if (IsPrivateOrLocalAddress(host))
        {
            return true;
        }

        lock (_exactMatches)
        {
            // 2. 精确匹配
            if (_exactMatches.Contains(host))
            {
                return true;
            }

            // 3. 后缀匹配 (如 news.baidu.com 匹配 .baidu.com)
            foreach (var suffix in _wildcardSuffixes)
            {
                if (host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            // 4. 正则与通配符匹配 (如 192.168.* 或 *.cn)
            foreach (var reg in _regexRules)
            {
                if (reg.IsMatch(host))
                {
                    return true;
                }
            }
        }

        // 5. 常见国内官方及常用国家顶级域名兜底 (.cn, .com.cn, .net.cn 等)
        if (host.EndsWith(".cn", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".com.cn", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".net.cn", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".edu.cn", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".gov.cn", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".org.cn", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
