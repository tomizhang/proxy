namespace Proxy.Common.Models;

public class ProxyClientConfig
{
    /// <summary>
    /// 服务器主机名或 Cloudflare 域名 (如 tunnel.example.com 或 优选 IP)
    /// </summary>
    public string ServerHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// 服务器端口 (如 5000，走 Cloudflare 443 时填 443)
    /// </summary>
    public int ServerPort { get; set; } = 5000;

    /// <summary>
    /// 伪装路径 (如 /tunnel)
    /// </summary>
    public string Path { get; set; } = "/tunnel";

    /// <summary>
    /// SNI 域名 / Host 标头 (用于 Cloudflare CDN 路由识别)
    /// </summary>
    public string? SniHost { get; set; }

    /// <summary>
    /// 是否启用 TLS (WSS)
    /// </summary>
    public bool UseTls { get; set; } = false;

    /// <summary>
    /// 连接密码 / Token
    /// </summary>
    public string Password { get; set; } = "DefaultSecurePassword123!";

    /// <summary>
    /// 加密算法 (aes-256-gcm / chacha20-poly1305 / custom-obfs / plain)
    /// </summary>
    public string Algorithm { get; set; } = "aes-256-gcm";

    /// <summary>
    /// 本地 SOCKS5 监听端口
    /// </summary>
    public int LocalSocks5Port { get; set; } = 20808;

    /// <summary>
    /// 并发连接通道数 (多路复用并发连接池，默认 4，可设置 1~16)
    /// 无论单 IP 还是多 IP，均并发建立多条独立物理 WebSocket 隧道，突破单连接 TCP 窗口与 Cloudflare 单连接限速，彻底消除队头阻塞
    /// </summary>
    public int ConcurrencyChannels { get; set; } = 4;

    /// <summary>
    /// 是否开启日志显示 (默认开启)
    /// </summary>
    public bool EnableLogging { get; set; } = true;

    /// <summary>
    /// 是否开启直连绕过 (跳过 baidu.com、国内网站和内网)
    /// </summary>
    public bool EnableBypass { get; set; } = true;

    /// <summary>
    /// 直连绕过规则列表 (分号或逗号分隔，支持通配符如 *.baidu.com, *.cn)
    /// </summary>
    public string BypassRules { get; set; } = "*.baidu.com;*.qq.com;*.taobao.com;*.jd.com;*.alipay.com;*.bilibili.com;*.163.com;*.zhihu.com;*.weibo.com;*.sina.com.cn;*.sohu.com;*.cn;localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>";

    /// <summary>
    /// 是否开启程序名单分流过滤 (可选择“仅直连选中的应用”或“只为选中的应用进行代理”)
    /// </summary>
    public bool EnableProcessBypass { get; set; } = true;

    /// <summary>
    /// 程序名单分流过滤模式 (BypassSelected: 仅直连选中的应用; ProxySelected: 只为选中的应用进行代理)
    /// </summary>
    public ProcessProxyMode ProcessFilterMode { get; set; } = ProcessProxyMode.BypassSelected;

    /// <summary>
    /// 选中的程序名单 (分号或逗号分隔，如 rustdesk.exe;wechat.exe;qq.exe;steam.exe)
    /// </summary>
    public string BypassProcessList { get; set; } = "rustdesk.exe;wechat.exe;qq.exe;dingtalk.exe;feishu.exe;baidunetdisk.exe;steam.exe";

    /// <summary>
    /// 是否自动设置 Windows 系统代理 (默认 true，免插件全局浏览)
    /// </summary>
    public bool EnableSystemProxy { get; set; } = true;

    /// <summary>
    /// 是否启用 TUN 虚拟网卡模式
    /// </summary>
    public bool EnableTun { get; set; } = false;

    /// <summary>
    /// Wintun 虚拟网卡 IP
    /// </summary>
    public string TunIp { get; set; } = "10.0.0.2";

    /// <summary>
    /// Wintun 掩码
    /// </summary>
    public string TunMask { get; set; } = "255.255.255.0";

    /// <summary>
    /// Wintun 网关
    /// </summary>
    public string TunGateway { get; set; } = "10.0.0.1";
}
