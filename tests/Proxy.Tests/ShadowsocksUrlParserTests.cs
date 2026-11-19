using System;
using Proxy.Common.Models;
using Proxy.Common.Utils;
using Xunit;

namespace Proxy.Tests;

public class ShadowsocksUrlParserTests
{
    [Fact]
    public void Should_Parse_User_Supplied_Shadowsocks_2022_Url_Correctly()
    {
        // 用户提供的真实 SS-2022 订阅链接
        string url = "ss://MjAyMi1ibGFrZTMtYWVzLTI1Ni1nY206bGJWVXNOQmR1UFFMcXc2cEFqUEswTFIwMnhKbGpsRlZLdWR1M2E3NWZrdz06NVZOMDFyaTRiN0FibWkzK3Z1MDhsRWxxbzBoYU5ZRzgvM3J6OGc1ZG5Rcz0@89.34.227.89:39844?#main-shadowsocks-ugrh3iqou0";

        bool ok = ShadowsocksUrlParser.TryParse(url, out var node, out string? err);

        Assert.True(ok, $"解析失败: {err}");
        Assert.NotNull(node);
        Assert.Equal("89.34.227.89", node!.Host);
        Assert.Equal(39844, node.Port);
        Assert.Equal("2022-blake3-aes-256-gcm", node.Method);
        Assert.Equal("lbVUsNBduPQLqw6pAjPK0LR02xJljlFVKudu3a75fkw=:5VN01ri4b7Abmi3+vu08lElqo0haNYG8/3rz8g5dnQs=", node.Password);
        Assert.Equal("main-shadowsocks-ugrh3iqou0", node.Tag);

        // 测试配置应用
        var config = new ProxyClientConfig();
        bool applied = config.ApplyShadowsocksUrl(url, out string? applyErr);
        Assert.True(applied, applyErr);
        Assert.Equal(ProxyProtocolType.Shadowsocks, config.ProtocolType);
        Assert.Equal("89.34.227.89", config.ServerHost);
        Assert.Equal(39844, config.ServerPort);
        Assert.Equal("2022-blake3-aes-256-gcm", config.Algorithm);
        Assert.Equal("lbVUsNBduPQLqw6pAjPK0LR02xJljlFVKudu3a75fkw=:5VN01ri4b7Abmi3+vu08lElqo0haNYG8/3rz8g5dnQs=", config.Password);
        Assert.Equal("main-shadowsocks-ugrh3iqou0", config.NodeName);
    }

    [Fact]
    public void Should_Parse_Standard_Sip002_Url()
    {
        // 标准 SIP002 格式: base64(aes-256-gcm:mysecretpassword) -> YWVzLTI1Ni1nY206bXlzZWNyZXRwYXNzd29yZA
        string url = "ss://YWVzLTI1Ni1nY206bXlzZWNyZXRwYXNzd29yZA@198.51.100.1:8388#MyNode";

        bool ok = ShadowsocksUrlParser.TryParse(url, out var node, out string? err);

        Assert.True(ok, err);
        Assert.NotNull(node);
        Assert.Equal("198.51.100.1", node!.Host);
        Assert.Equal(8388, node.Port);
        Assert.Equal("aes-256-gcm", node.Method);
        Assert.Equal("mysecretpassword", node.Password);
        Assert.Equal("MyNode", node.Tag);
    }

    [Fact]
    public void Should_Parse_Legacy_Shadowsocks_Url()
    {
        // 传统格式: base64(chacha20-ietf-poly1305:p@ssword@jp.node.com:443)
        // Y2hhY2hhMjAtaWV0Zi1wb2x5MTMwNTpwQHNzd29yZEBqcC5ub2RlLmNvbTo0NDM=
        string url = "ss://Y2hhY2hhMjAtaWV0Zi1wb2x5MTMwNTpwQHNzd29yZEBqcC5ub2RlLmNvbTo0NDM=#Japan-01";

        bool ok = ShadowsocksUrlParser.TryParse(url, out var node, out string? err);

        Assert.True(ok, err);
        Assert.NotNull(node);
        Assert.Equal("jp.node.com", node!.Host);
        Assert.Equal(443, node.Port);
        Assert.Equal("chacha20-ietf-poly1305", node.Method);
        Assert.Equal("p@ssword", node.Password);
        Assert.Equal("Japan-01", node.Tag);
    }

    [Fact]
    public void Should_Parse_IPv6_Shadowsocks_Url()
    {
        // IPv6 格式
        string url = "ss://YWVzLTEyOC1nY206dGVzdA@[2001:db8::1]:1080#IPv6Node";

        bool ok = ShadowsocksUrlParser.TryParse(url, out var node, out string? err);

        Assert.True(ok, err);
        Assert.NotNull(node);
        Assert.Equal("2001:db8::1", node!.Host);
        Assert.Equal(1080, node.Port);
        Assert.Equal("aes-128-gcm", node.Method);
        Assert.Equal("test", node.Password);
        Assert.Equal("IPv6Node", node.Tag);
    }
}
