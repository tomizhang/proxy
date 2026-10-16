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
}
