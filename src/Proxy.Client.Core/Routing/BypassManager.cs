using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Proxy.Client.Core.Routing;

/// <summary>
/// 直连分流与绕过管理器 (例如 baidu.com, *.cn, 局域网等不走代理)
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

    public void ParseRules(string rules)
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

    /// <summary>
    /// 判断目标域名或 IP 是否应该跳过代理 (直接本地直连)
    /// </summary>
    public bool ShouldBypass(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        host = host.Trim().ToLowerInvariant();

        // 1. 本地回环与简单名称直连 (不含点的单名如 localhost, intranet)
        if (host == "localhost" || host.StartsWith("127.") || !host.Contains('.'))
        {
            return true;
        }

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

        // 4. 正则与通配符匹配 (如 192.168.1.1 匹配 192.168.*)
        foreach (var reg in _regexRules)
        {
            if (reg.IsMatch(host))
            {
                return true;
            }
        }

        return false;
    }
}
