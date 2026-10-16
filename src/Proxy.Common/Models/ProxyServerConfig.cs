namespace Proxy.Common.Models;

public class ProxyServerConfig
{
    /// <summary>
    /// 监听端口 (默认 5000 或 80/443)
    /// </summary>
    public int Port { get; set; } = 5000;

    /// <summary>
    /// WebSocket 路由路径 (伪装路径，如 /api/stream)
    /// </summary>
    public string Path { get; set; } = "/tunnel";

    /// <summary>
    /// 鉴权密码 / Token
    /// </summary>
    public string Password { get; set; } = "DefaultSecurePassword123!";

    /// <summary>
    /// 加密算法 (aes-256-gcm / chacha20-poly1305 / custom-obfs / plain)
    /// </summary>
    public string Algorithm { get; set; } = "aes-256-gcm";

    /// <summary>
    /// 连接空闲超时 (秒)
    /// </summary>
    public int IdleTimeoutSeconds { get; set; } = 60;
}
