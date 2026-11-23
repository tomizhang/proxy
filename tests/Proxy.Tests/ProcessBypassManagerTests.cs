using System.Diagnostics;
using Proxy.Client.Core.Routing;
using Xunit;

namespace Proxy.Tests;

public class ProcessBypassManagerTests
{
    [Theory]
    [InlineData("rustdesk.exe", true)]
    [InlineData("RUSTDESK.EXE", true)]
    [InlineData("rustdesk", true)]
    [InlineData("C:\\Program Files\\RustDesk\\rustdesk.exe", true)]
    [InlineData("wechat.exe", true)]
    [InlineData("wechat", true)]
    [InlineData("qq.exe", true)]
    [InlineData("qq", true)]
    [InlineData("chrome.exe", false)]
    [InlineData("msedge.exe", false)]
    [InlineData("v2ray.exe", false)]
    public void IsProcessMatched_WorksCorrectly(string processName, bool expected)
    {
        var manager = new ProcessBypassManager("rustdesk.exe;wechat.exe;qq.exe;dingtalk.exe;baidunetdisk.exe;steam.exe");
        bool actual = manager.IsProcessMatched(processName);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ParseProcessList_EmptyOrWhitespace_HandlesGracefully()
    {
        var manager = new ProcessBypassManager("");
        Assert.False(manager.IsProcessMatched("rustdesk.exe"));

        manager.ParseProcessList("  ;  ,, \r\n ");
        Assert.False(manager.IsProcessMatched("rustdesk.exe"));

        manager.ParseProcessList("telegram.exe; discord.exe");
        Assert.True(manager.IsProcessMatched("telegram"));
        Assert.True(manager.IsProcessMatched("discord.exe"));
    }

    [Fact]
    public void GetProcessNameByPid_CurrentProcess_ReturnsProcessName()
    {
        var manager = new ProcessBypassManager("");
        var current = Process.GetCurrentProcess();
        string? name = manager.GetProcessNameByPid(current.Id);
        Assert.NotNull(name);
        Assert.Equal(current.ProcessName, name);
    }

    [Theory]
    [InlineData("rustdesk.exe", true)]  // 命中直连白名单 -> 直连
    [InlineData("wechat.exe", true)]    // 命中直连白名单 -> 直连
    [InlineData("chrome.exe", false)]   // 未在排除名单 -> 走代理
    [InlineData("msedge.exe", false)]   // 未在排除名单 -> 走代理
    public void ShouldBypassProcessName_BypassSelectedMode_WorksAsExpected(string procName, bool expectedBypass)
    {
        var manager = new ProcessBypassManager("rustdesk.exe;wechat.exe;qq.exe");
        bool bypass = manager.ShouldBypassProcessName(procName, Proxy.Common.Models.ProcessProxyMode.BypassSelected, out var log);
        Assert.Equal(expectedBypass, bypass);
        Assert.NotEmpty(log);
    }

    [Theory]
    [InlineData("chrome.exe", false)]   // 命中选中名单 -> 走代理 (不直连)
    [InlineData("msedge.exe", false)]   // 命中选中名单 -> 走代理 (不直连)
    [InlineData("wechat.exe", true)]    // 未在代理名单 -> 直连
    [InlineData("rustdesk.exe", true)]  // 未在代理名单 -> 直连
    [InlineData("notepad.exe", true)]   // 未在代理名单 -> 直连
    public void ShouldBypassProcessName_ProxySelectedMode_WorksAsExpected(string procName, bool expectedBypass)
    {
        // 白名单代理模式：只代理 chrome 和 edge
        var manager = new ProcessBypassManager("chrome.exe;msedge.exe");
        bool bypass = manager.ShouldBypassProcessName(procName, Proxy.Common.Models.ProcessProxyMode.ProxySelected, out var log);
        Assert.Equal(expectedBypass, bypass);
        Assert.NotEmpty(log);
    }

    [Theory]
    [InlineData("code.exe", true)]
    [InlineData("code", true)]
    [InlineData("code-tunnel.exe", true)]
    [InlineData("cursor.exe", true)]
    [InlineData("cursor-helper.exe", true)]
    [InlineData("codex.exe", false)]       // 不应被误判匹配
    [InlineData("codec.exe", false)]       // 不应被误判匹配
    [InlineData("cursorial.exe", false)]   // 不应被误判匹配
    public void IsProcessMatched_StrictPrefixAndExactMatching(string procName, bool expected)
    {
        var manager = new ProcessBypassManager("code.exe;cursor.exe");
        bool matched = manager.IsProcessMatched(procName);
        Assert.Equal(expected, matched);
    }

    [Fact]
    public void GetPidByRemoteEndpoint_NullOrInvalid_ReturnsNegative()
    {
        int pidNull = ProcessBypassManager.GetPidByRemoteEndpoint(null, 443);
        Assert.Equal(-1, pidNull);

        int pidInvalidPort = ProcessBypassManager.GetPidByRemoteEndpoint(System.Net.IPAddress.Loopback, 0);
        Assert.Equal(-1, pidInvalidPort);
    }

    [Fact]
    public void ShouldBypass_WithRawTargetHost_ExecutesWithoutError()
    {
        var manager = new ProcessBypassManager("chrome.exe");
        bool bypass = manager.ShouldBypass(
            clientPort: 99999, // 非法/不存在端口
            targetHost: "www.google.com",
            targetPort: 443,
            mode: Proxy.Common.Models.ProcessProxyMode.ProxySelected,
            matchedProcess: out var matchedProc,
            logReason: out var logReason,
            rawTargetHost: "142.250.190.46");

        // 端口不存在，识别为非代理名单应用，在仅代理模式下直连
        Assert.True(bypass);
        Assert.NotEmpty(logReason);
    }
}
