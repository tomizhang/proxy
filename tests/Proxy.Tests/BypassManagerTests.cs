using Proxy.Client.Core.Routing;
using Xunit;

namespace Proxy.Tests;

public class BypassManagerTests
{
    private readonly BypassManager _manager;

    public BypassManagerTests()
    {
        string defaultRules = "*.baidu.com;*.qq.com;*.taobao.com;*.jd.com;*.alipay.com;*.bilibili.com;*.cn;localhost;127.*;192.168.*;10.*";
        _manager = new BypassManager(defaultRules);
    }

    [Theory]
    [InlineData("baidu.com")]
    [InlineData("www.baidu.com")]
    [InlineData("map.baidu.com")]
    [InlineData("tieba.baidu.com")]
    [InlineData("qq.com")]
    [InlineData("v.qq.com")]
    [InlineData("taobao.com")]
    [InlineData("item.taobao.com")]
    [InlineData("sina.com.cn")]
    [InlineData("gov.cn")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("192.168.1.100")]
    [InlineData("10.0.0.1")]
    public void ShouldBypass_DomesticAndLocal_ReturnsTrue(string host)
    {
        Assert.True(_manager.ShouldBypass(host), $"主机 {host} 应该被直连绕过");
    }

    [Theory]
    [InlineData("google.com")]
    [InlineData("www.google.com")]
    [InlineData("youtube.com")]
    [InlineData("github.com")]
    [InlineData("openai.com")]
    [InlineData("cloudflare.com")]
    [InlineData("twitter.com")]
    public void ShouldBypass_OverseasHosts_ReturnsFalse(string host)
    {
        Assert.False(_manager.ShouldBypass(host), $"主机 {host} 不应该被直连，应走代理隧道");
    }
}
