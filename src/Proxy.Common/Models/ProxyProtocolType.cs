namespace Proxy.Common.Models;

/// <summary>
/// 代理协议类型
/// </summary>
public enum ProxyProtocolType
{
    /// <summary>
    /// 自研多路复用 WebSocket 隧道 (Cloudflare CDN 加速)
    /// </summary>
    V2RayWss = 0,

    /// <summary>
    /// 标准 Shadowsocks 协议 (支持 SS-2022 及传统加密)
    /// </summary>
    Shadowsocks = 1
}
