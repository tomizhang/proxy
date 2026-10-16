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
}
