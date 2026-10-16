using System;
using System.Linq;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Proxy.Common.Crypto;
using Proxy.Common.Models;
using Proxy.Server.Sessions;

var serverConfig = new ProxyServerConfig
{
    Port = 5005, // 默认设为 5005，与 Nginx 配置完全匹配
    Path = "/tunnel",
    Password = "DefaultSecurePassword123!",
    Algorithm = "aes-256-gcm"
};

// 精确解析命令行参数，不受任何 profile 干扰
for (int i = 0; i < args.Length; i++)
{
    string arg = args[i].Trim().ToLowerInvariant();
    if ((arg == "--port" || arg == "-p") && i + 1 < args.Length && int.TryParse(args[i + 1], out int p))
    {
        serverConfig.Port = p;
    }
    else if (arg == "--path" && i + 1 < args.Length)
    {
        serverConfig.Path = args[i + 1];
    }
    else if ((arg == "--password" || arg == "--token" || arg == "-pwd") && i + 1 < args.Length)
    {
        serverConfig.Password = args[i + 1];
    }
    else if ((arg == "--algo" || arg == "--algorithm") && i + 1 < args.Length)
    {
        serverConfig.Algorithm = args[i + 1];
    }
}

if (!serverConfig.Path.StartsWith('/'))
{
    serverConfig.Path = "/" + serverConfig.Path;
}

var builder = WebApplication.CreateBuilder(args);

// 显式绑定 Kestrel 监听 0.0.0.0:Port
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(serverConfig.Port);
});

builder.Services.AddLogging(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Information);
});

var app = builder.Build();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

// 处理隧道升级请求
async Task HandleTunnelRequestAsync(HttpContext context, ILogger logger)
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        logger.LogWarning("收到非 WebSocket 请求 (Path: {Path}, Remote: {Remote})", context.Request.Path, context.Connection.RemoteIpAddress);
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsync("WebSocket Upgrade Expected");
        return;
    }

    string? token = context.Request.Query["token"].FirstOrDefault() 
                 ?? context.Request.Headers["Authorization"].FirstOrDefault();

    if (!string.Equals(token, serverConfig.Password, StringComparison.Ordinal) &&
        !string.Equals(token, $"Bearer {serverConfig.Password}", StringComparison.Ordinal))
    {
        logger.LogWarning("未授权的隧道连接尝试 (Token不匹配) 来自: {RemoteIp}", context.Connection.RemoteIpAddress);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Forbidden: Invalid Token");
        return;
    }

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    logger.LogInformation("✅ 客户端隧道连接已成功建立: {RemoteIp}", context.Connection.RemoteIpAddress);

    using var crypto = CryptoFactory.Create(serverConfig.Algorithm, serverConfig.Password);
    await using var handler = new TunnelConnectionHandler(webSocket, crypto, logger);

    await handler.ProcessAsync();
    logger.LogInformation("客户端隧道连接已断开: {RemoteIp}", context.Connection.RemoteIpAddress);
}

// 映射隧道端点
app.Map(serverConfig.Path, async (HttpContext context, ILogger<Program> logger) =>
{
    await HandleTunnelRequestAsync(context, logger);
});

// 根路径伪装 (普通 HTTP 访问时返回 Nginx 欢迎页)
if (serverConfig.Path != "/")
{
    app.MapGet("/", (HttpContext context) =>
    {
        if (context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Results.Empty;
        }
        return Results.Text("<!DOCTYPE html><html><body><h1>Welcome to nginx!</h1></body></html>", "text/html");
    });
}

Console.WriteLine("==================================================");
Console.WriteLine($"🚀 代理服务端已启动成功！");
Console.WriteLine($"   监听地址: http://0.0.0.0:{serverConfig.Port}");
Console.WriteLine($"   隧道路径: {serverConfig.Path}");
Console.WriteLine($"   加密套件: {serverConfig.Algorithm}");
Console.WriteLine($"   认证密码: {serverConfig.Password}");
Console.WriteLine("==================================================");

await app.RunAsync();
