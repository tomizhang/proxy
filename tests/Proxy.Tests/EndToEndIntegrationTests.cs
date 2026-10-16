using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Proxy.Client.Core;
using Proxy.Common.Crypto;
using Proxy.Common.Models;
using Proxy.Server.Sessions;
using Xunit;

namespace Proxy.Tests;

public class EndToEndIntegrationTests
{
    private static int GetAvailablePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Theory]
    [InlineData("aes-256-gcm")]
    [InlineData("custom-obfs")]
    public async Task FullProxy_EchoPipeline_Success(string algorithm)
    {
        int echoPort = GetAvailablePort();
        int serverPort = GetAvailablePort();
        int socks5Port = GetAvailablePort();
        string testPassword = "E2E_Secure_Password_999!";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // 1. 启动目标 Echo 服务器
        var echoListener = new TcpListener(IPAddress.Loopback, echoPort);
        echoListener.Start();
        var echoTask = Task.Run(async () =>
        {
            try
            {
                using var client = await echoListener.AcceptTcpClientAsync(cts.Token);
                using var stream = client.GetStream();
                byte[] buf = new byte[1024];
                int read = await stream.ReadAsync(buf, cts.Token);
                if (read > 0)
                {
                    await stream.WriteAsync(buf.AsMemory(0, read), cts.Token);
                }
            }
            catch { }
        }, cts.Token);

        // 2. 启动代理服务端 (Kestrel)
        var serverBuilder = WebApplication.CreateBuilder();
        serverBuilder.WebHost.UseKestrel(opts => opts.ListenLocalhost(serverPort));
        serverBuilder.Services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        var app = serverBuilder.Build();
        app.UseWebSockets();
        app.Map("/test-tunnel", async (HttpContext ctx, ILogger<EndToEndIntegrationTests> logger) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) return;
            string? token = ctx.Request.Query["token"];
            if (token != testPassword)
            {
                ctx.Response.StatusCode = 403;
                return;
            }

            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            using var crypto = CryptoFactory.Create(algorithm, testPassword);
            await using var handler = new TunnelConnectionHandler(ws, crypto, logger);
            await handler.ProcessAsync();
        });

        var serverRunTask = app.RunAsync(cts.Token);
        await Task.Delay(500, cts.Token); // 等待服务就绪

        // 3. 启动客户端引擎
        var clientConfig = new ProxyClientConfig
        {
            ServerHost = "127.0.0.1",
            ServerPort = serverPort,
            Path = "/test-tunnel",
            Password = testPassword,
            Algorithm = algorithm,
            UseTls = false,
            LocalSocks5Port = socks5Port,
            EnableSystemProxy = false, // 测试用例避免修改全局系统代理
            EnableTun = false // 单元测试中关闭 TUN 以免需要全局路由权限
        };

        var clientEngine = new ProxyClientEngine(clientConfig);
        await clientEngine.StartAsync();
        await Task.Delay(500, cts.Token);

        // 4. 模拟应用程序通过 SOCKS5 连接目标 Echo 服务器
        using var testClient = new TcpClient();
        await testClient.ConnectAsync(IPAddress.Loopback, socks5Port, cts.Token);
        using var testStream = testClient.GetStream();

        // SOCKS5 握手
        await testStream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cts.Token);
        byte[] authResp = new byte[2];
        await testStream.ReadExactlyAsync(authResp, cts.Token);
        Assert.Equal(0x05, authResp[0]);
        Assert.Equal(0x00, authResp[1]);

        // SOCKS5 Connect 请求到 127.0.0.1:echoPort
        byte[] connectReq = [0x05, 0x01, 0x00, 0x01, 127, 0, 0, 1, (byte)(echoPort >> 8), (byte)(echoPort & 0xFF)];
        await testStream.WriteAsync(connectReq, cts.Token);
        byte[] connectResp = new byte[10];
        await testStream.ReadExactlyAsync(connectResp, cts.Token);
        Assert.Equal(0x05, connectResp[0]);
        Assert.Equal(0x00, connectResp[1]); // 0x00 代表成功连接！

        // 发送测试消息
        string testMessage = "Hello Cloudflare C# Custom Proxy!";
        byte[] sendData = Encoding.UTF8.GetBytes(testMessage);
        await testStream.WriteAsync(sendData, cts.Token);

        // 接收 Echo 回传
        byte[] receiveBuf = new byte[sendData.Length];
        await testStream.ReadExactlyAsync(receiveBuf, cts.Token);
        string received = Encoding.UTF8.GetString(receiveBuf);

        Assert.Equal(testMessage, received);

        // 清理
        await clientEngine.StopAsync();
        await app.StopAsync();
        echoListener.Stop();
    }
}
