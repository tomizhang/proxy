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

// 伪装 Nginx 默认欢迎页 (抗主动探测)
const string NginxWelcomeHtml = "<!DOCTYPE html>\n<html>\n<head>\n<title>Welcome to nginx!</title>\n<style>\n    body {\n        width: 35em;\n        margin: 0 auto;\n        font-family: Tahoma, Verdana, Arial, sans-serif;\n    }\n</style>\n</head>\n<body>\n<h1>Welcome to nginx!</h1>\n<p>If you see this page, the nginx web server is successfully installed and working. Further configuration is required.</p>\n<p>For online documentation and support please refer to <a href=\"http://nginx.org/\">nginx.org</a>.<br/>\nCommercial support is available at <a href=\"http://nginx.com/\">nginx.com</a>.</p>\n<p><em>Thank you for using nginx.</em></p>\n</body>\n</html>";

// 处理隧道升级请求 (支持常规 WSS 与伪装 HTTP-RPC 极速流)
async Task HandleTunnelRequestAsync(HttpContext context, ILogger logger)
{
    // 防主动扫描与探测：若非流式升级请求（如外部扫描器发送普通 GET / 或 HEAD），伪装返回常规 Nginx 静态网页
    if (!context.WebSockets.IsWebSocketRequest)
    {
        logger.LogInformation("收到外部常规 HTTP 请求探测 (Path: {Path}, Remote: {Remote}) -> 伪装返回 Nginx 页面", context.Request.Path, context.Connection.RemoteIpAddress);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(NginxWelcomeHtml);
        return;
    }

    // 从自定义无特征标头、标准头或 URL 查询中提取认证 Token (避免出现常规 Proxy 代理特征头)
    string? token = context.Request.Headers["X-Session-Token"].FirstOrDefault()
                 ?? context.Request.Headers["X-API-Key"].FirstOrDefault()
                 ?? context.Request.Query["token"].FirstOrDefault()
                 ?? context.Request.Headers["Authorization"].FirstOrDefault();

    if (!string.Equals(token, serverConfig.Password, StringComparison.Ordinal) &&
        !string.Equals(token, $"Bearer {serverConfig.Password}", StringComparison.Ordinal))
    {
        logger.LogWarning("未授权连接尝试 (Token不匹配) 来自: {RemoteIp} -> 伪装返回 404 (隐藏服务)", context.Connection.RemoteIpAddress);
        // 关键防御机制：坚决不返回 403 Forbidden 或任何代理报错，直接返回 404 Not Found 隐藏服务存在性！
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    // 动态协商加密算法 (支持客户端自选 消息加密/简单加密/急速模式，或遵循服务端默认配置)
    string clientAlgo = context.Request.Headers["X-Algorithm"].FirstOrDefault()
                     ?? context.Request.Query["algo"].FirstOrDefault()
                     ?? serverConfig.Algorithm;
    string activeAlgorithm = CryptoFactory.NormalizeAlgorithm(clientAlgo);

    using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
    logger.LogInformation("✅ 客户端隧道连接已成功建立: {RemoteIp} (模式: {Algorithm})", context.Connection.RemoteIpAddress, activeAlgorithm);

    using var crypto = CryptoFactory.Create(activeAlgorithm, serverConfig.Password);
    await using var handler = new TunnelConnectionHandler(webSocket, crypto, logger);

    await handler.ProcessAsync();
    logger.LogInformation("客户端隧道连接已断开: {RemoteIp}", context.Connection.RemoteIpAddress);
}

// 映射配置的主隧道端点
app.Map(serverConfig.Path, async (HttpContext context, ILogger<Program> logger) =>
{
    await HandleTunnelRequestAsync(context, logger);
});

// 默认同时映射 /api/v1/stream 端点，用于伪装 HTTP-RPC 极速流接口
if (!string.Equals(serverConfig.Path, "/api/v1/stream", StringComparison.OrdinalIgnoreCase))
{
    app.Map("/api/v1/stream", async (HttpContext context, ILogger<Program> logger) =>
    {
        await HandleTunnelRequestAsync(context, logger);
    });
}

// 兜底路径伪装：任何未知路径或探测访问均返回 Nginx 欢迎页，消除服务指纹
app.MapFallback((HttpContext context) =>
{
    context.Response.StatusCode = StatusCodes.Status200OK;
    context.Response.ContentType = "text/html; charset=utf-8";
    return Results.Text(NginxWelcomeHtml, "text/html");
});

Console.WriteLine("==================================================");
Console.WriteLine($"🚀 代理服务端已启动成功！");
Console.WriteLine($"   监听地址: http://0.0.0.0:{serverConfig.Port}");
Console.WriteLine($"   隧道路径: {serverConfig.Path} 及 /api/v1/stream (HTTP-RPC 伪装)");
Console.WriteLine($"   加密套件: {serverConfig.Algorithm} (支持客户端自适应协商)");
Console.WriteLine($"   认证密码: {serverConfig.Password}");
Console.WriteLine($"   防主动探测: 已就绪 (非授权访问自动伪装为 Nginx 静态站点)");
Console.WriteLine("==================================================");

await app.RunAsync();
