using System;
using System.Text;
using Proxy.Common.Crypto;
using Proxy.Common.Protocol;
using Xunit;

namespace Proxy.Tests;

public class CryptoAndProtocolTests
{
    [Theory]
    [InlineData("aes-256-gcm")]
    [InlineData("chacha20-poly1305")]
    [InlineData("custom-obfs")]
    [InlineData("plain")]
    public void CryptoProvider_RoundTrip_Success(string algo)
    {
        string password = "StrongPassword@2026";
        using var crypto = CryptoFactory.Create(algo, password);

        byte[] original = Encoding.UTF8.GetBytes("Hello, Cloudflare Tunnel with Custom Encryption!");
        byte[] encrypted = crypto.Encrypt(original);

        Assert.NotEmpty(encrypted);

        byte[] decrypted = crypto.Decrypt(encrypted);
        Assert.Equal(original, decrypted);
    }

    [Theory]
    [InlineData("aes-256-gcm")]
    [InlineData("chacha20-poly1305")]
    [InlineData("custom-obfs")]
    public void CryptoProvider_TamperedData_FailsVerification(string algo)
    {
        string password = "TestPassword_123456";
        using var crypto = CryptoFactory.Create(algo, password);

        byte[] original = Encoding.UTF8.GetBytes("Secure payload to protect");
        byte[] encrypted = crypto.Encrypt(original);

        // 篡改密文包末尾的一个字节
        encrypted[^1] ^= 0xFF;

        byte[] dest = new byte[original.Length + 64];
        bool success = crypto.TryDecrypt(encrypted, dest, out _);

        Assert.False(success, $"算法 {algo} 在密文被篡改后未检出错误！");
    }

    [Fact]
    public void ProxyFrame_ConnectFrame_RoundTrip()
    {
        var frame = ProxyFrame.CreateConnect(123456789UL, "example.com", 443, AddressType.Domain);
        byte[] bytes = frame.Serialize();

        bool ok = ProxyFrame.TryDeserialize(bytes, out var decoded);
        Assert.True(ok);
        Assert.NotNull(decoded);
        Assert.Equal(ProxyCommand.ConnectTcp, decoded.Command);
        Assert.Equal(123456789UL, decoded.SessionId);
        Assert.Equal(AddressType.Domain, decoded.AddressType);
        Assert.Equal("example.com", decoded.TargetHost);
        Assert.Equal(443, decoded.TargetPort);
        Assert.True(decoded.Payload.IsEmpty);
    }

    [Fact]
    public void ProxyFrame_DataFrame_RoundTrip()
    {
        byte[] data = Encoding.UTF8.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n");
        var frame = ProxyFrame.CreateData(987654321UL, data);
        byte[] bytes = frame.Serialize();

        bool ok = ProxyFrame.TryDeserialize(bytes, out var decoded);
        Assert.True(ok);
        Assert.NotNull(decoded);
        Assert.Equal(ProxyCommand.DataTcp, decoded.Command);
        Assert.Equal(987654321UL, decoded.SessionId);
        Assert.Equal(data, decoded.Payload.ToArray());
    }

    [Fact]
    public void ProxyFrame_UdpFrame_RoundTrip()
    {
        byte[] dnsQuery = [0x12, 0x34, 0x01, 0x00, 0x00, 0x01];
        var frame = ProxyFrame.CreateUdp(555UL, "1.1.1.1", 53, AddressType.IPv4, dnsQuery);
        byte[] bytes = frame.Serialize();

        bool ok = ProxyFrame.TryDeserialize(bytes, out var decoded);
        Assert.True(ok);
        Assert.NotNull(decoded);
        Assert.Equal(ProxyCommand.UdpPacket, decoded.Command);
        Assert.Equal("1.1.1.1", decoded.TargetHost);
        Assert.Equal(53, decoded.TargetPort);
        Assert.Equal(dnsQuery, decoded.Payload.ToArray());
    }

    [Fact]
    public void ProxyFrame_PingPong_RoundTrip()
    {
        var ping = ProxyFrame.CreatePing();
        var pong = ProxyFrame.CreatePong();

        Assert.True(ProxyFrame.TryDeserialize(ping.Serialize(), out var decPing));
        Assert.Equal(ProxyCommand.Ping, decPing!.Command);

        Assert.True(ProxyFrame.TryDeserialize(pong.Serialize(), out var decPong));
        Assert.Equal(ProxyCommand.Pong, decPong!.Command);
    }
}
