using System;
using System.Text;

namespace Proxy.Common.Utils;

/// <summary>
/// Shadowsocks 节点解析信息
/// </summary>
public sealed class ShadowsocksNodeInfo
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Method { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? Tag { get; set; }
    public string? Plugin { get; set; }
    public string? PluginOpts { get; set; }
    public string RawUrl { get; set; } = string.Empty;

    public override string ToString()
    {
        string name = !string.IsNullOrWhiteSpace(Tag) ? Tag : $"{Host}:{Port}";
        return $"{name} ({Method})";
    }
}

/// <summary>
/// 标准 Shadowsocks (SIP002 及 Legacy 规范) 链接解析器
/// 支持 Shadowsocks 2022 (如 2022-blake3-aes-256-gcm) 及各种传统 AEAD 加密链接
/// </summary>
public static class ShadowsocksUrlParser
{
    public static bool TryParse(string? rawUrl, out ShadowsocksNodeInfo? node, out string? error)
    {
        node = null;
        error = null;

        if (string.IsNullOrWhiteSpace(rawUrl))
        {
            error = "链接为空。";
            return false;
        }

        string trimmed = rawUrl.Trim().Trim('"', '\'', '`');
        if (!trimmed.StartsWith("ss://", StringComparison.OrdinalIgnoreCase))
        {
            error = "链接不是以 ss:// 开头。";
            return false;
        }

        try
        {
            string content = trimmed.Substring(5);
            string? tag = null;

            // 1. 提取 #tag (备注名)
            int hashIdx = content.IndexOf('#');
            if (hashIdx >= 0)
            {
                tag = Uri.UnescapeDataString(content.Substring(hashIdx + 1).Trim());
                content = content.Substring(0, hashIdx);
            }

            // 2. 提取 ?query (插件等参数)
            string? query = null;
            int qIdx = content.IndexOf('?');
            if (qIdx >= 0)
            {
                query = content.Substring(qIdx + 1);
                content = content.Substring(0, qIdx);
            }

            string method = string.Empty;
            string password = string.Empty;
            string host = string.Empty;
            int port = 0;

            if (content.Contains('@'))
            {
                // SIP002 标准格式: ss://[userinfo]@[serverinfo]
                int atIdx = content.LastIndexOf('@');
                string userinfo = content.Substring(0, atIdx);
                string serverinfo = content.Substring(atIdx + 1);

                // 解码 userinfo: 可能为 base64 编码，或明文 method:password
                string decodedUserinfo;
                if (userinfo.Contains(':') && !userinfo.Contains('=') && !userinfo.Contains('+') && !userinfo.Contains('/'))
                {
                    decodedUserinfo = userinfo;
                }
                else
                {
                    decodedUserinfo = SafeBase64Decode(userinfo);
                }

                // 拆分 method 与 password (对于 SS-2022，可能为 method:server_key:client_key，首个冒号为算法)
                int colonIdx = decodedUserinfo.IndexOf(':');
                if (colonIdx <= 0)
                {
                    error = "无法从用户信息中解析加密算法与密码。";
                    return false;
                }

                method = decodedUserinfo.Substring(0, colonIdx).Trim();
                password = decodedUserinfo.Substring(colonIdx + 1).Trim();

                // 解析 serverinfo (支持 [IPv6]:port 或 host:port)
                if (!ParseHostAndPort(serverinfo, out host, out port))
                {
                    error = $"无法解析服务器地址与端口: {serverinfo}";
                    return false;
                }
            }
            else
            {
                // 旧版 Legacy 格式: ss://BASE64(method:password@hostname:port)
                string decoded = SafeBase64Decode(content);
                int atIdx = decoded.LastIndexOf('@');
                if (atIdx <= 0)
                {
                    error = "无法从 Base64 内容中解析出 @ 分隔符。";
                    return false;
                }

                string userinfo = decoded.Substring(0, atIdx);
                string serverinfo = decoded.Substring(atIdx + 1);

                int colonIdx = userinfo.IndexOf(':');
                if (colonIdx <= 0)
                {
                    error = "无法从用户信息中提取加密算法与密码。";
                    return false;
                }

                method = userinfo.Substring(0, colonIdx).Trim();
                password = userinfo.Substring(colonIdx + 1).Trim();

                if (!ParseHostAndPort(serverinfo, out host, out port))
                {
                    error = $"无法解析服务器地址与端口: {serverinfo}";
                    return false;
                }
            }

            string? plugin = null;
            string? pluginOpts = null;
            if (!string.IsNullOrEmpty(query))
            {
                var queryParams = query.Split('&', StringSplitOptions.RemoveEmptyEntries);
                foreach (var param in queryParams)
                {
                    var kv = param.Split(new[] { '=' }, 2);
                    if (kv.Length == 2 && kv[0].Equals("plugin", StringComparison.OrdinalIgnoreCase))
                    {
                        string pluginStr = Uri.UnescapeDataString(kv[1]);
                        int semiIdx = pluginStr.IndexOf(';');
                        if (semiIdx >= 0)
                        {
                            plugin = pluginStr.Substring(0, semiIdx);
                            pluginOpts = pluginStr.Substring(semiIdx + 1);
                        }
                        else
                        {
                            plugin = pluginStr;
                        }
                    }
                }
            }

            if (string.IsNullOrEmpty(host) || port <= 0 || port > 65535)
            {
                error = "服务器地址或端口不合法。";
                return false;
            }

            node = new ShadowsocksNodeInfo
            {
                Host = host,
                Port = port,
                Method = method,
                Password = password,
                Tag = tag,
                Plugin = plugin,
                PluginOpts = pluginOpts,
                RawUrl = trimmed
            };

            return true;
        }
        catch (Exception ex)
        {
            error = $"解析发生异常: {ex.Message}";
            return false;
        }
    }

    private static bool ParseHostAndPort(string serverinfo, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        serverinfo = serverinfo.Trim();
        if (serverinfo.StartsWith('['))
        {
            // IPv6 地址如 [2001:db8::1]:8388
            int closeBracket = serverinfo.IndexOf(']');
            if (closeBracket <= 0) return false;
            host = serverinfo.Substring(1, closeBracket - 1);
            if (closeBracket + 2 <= serverinfo.Length && serverinfo[closeBracket + 1] == ':')
            {
                return int.TryParse(serverinfo.Substring(closeBracket + 2), out port);
            }
            return false;
        }

        int colon = serverinfo.LastIndexOf(':');
        if (colon <= 0 || colon == serverinfo.Length - 1) return false;

        host = serverinfo.Substring(0, colon).Trim();
        return int.TryParse(serverinfo.Substring(colon + 1), out port);
    }

    /// <summary>
    /// 安全的 URL-Safe Base64 解码 (自动补齐 '=' 并替换 '-' '_')
    /// </summary>
    public static string SafeBase64Decode(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        string s = input.Trim().Replace('-', '+').Replace('_', '/');
        int rem = s.Length % 4;
        if (rem > 0)
        {
            s = s.PadRight(s.Length + (4 - rem), '=');
        }

        byte[] bytes = Convert.FromBase64String(s);
        return Encoding.UTF8.GetString(bytes);
    }
}
