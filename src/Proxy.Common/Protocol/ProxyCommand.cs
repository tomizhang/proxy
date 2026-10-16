namespace Proxy.Common.Protocol;

public enum ProxyCommand : byte
{
    /// <summary>
    /// 请求建立目标 TCP 连接
    /// </summary>
    ConnectTcp = 0x01,

    /// <summary>
    /// 传输 TCP 数据负载
    /// </summary>
    DataTcp = 0x02,

    /// <summary>
    /// 关闭指定会话的 TCP 连接
    /// </summary>
    CloseTcp = 0x03,

    /// <summary>
    /// 传输单个 UDP 数据报
    /// </summary>
    UdpPacket = 0x04,

    /// <summary>
    /// 心跳 Ping (用于抵御 Cloudflare 100秒空闲超时)
    /// </summary>
    Ping = 0x05,

    /// <summary>
    /// 心跳 Pong 响应
    /// </summary>
    Pong = 0x06
}
