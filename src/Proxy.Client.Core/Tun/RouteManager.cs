using System;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace Proxy.Client.Core.Tun;

/// <summary>
/// Windows 网络路由表与网关管理 (防回环与全局流量接管)
/// </summary>
public static class RouteManager
{
    /// <summary>
    /// 获取当前活动的主物理网卡默认网关、接口索引、本地物理 IP 与网卡名称
    /// </summary>
    public static (IPAddress Gateway, int InterfaceIndex, IPAddress LocalIp, string InterfaceName)? GetDefaultGateway()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                ni.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                ni.Description.Contains("tun2socks", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var ipProps = ni.GetIPProperties();
            foreach (var gw in ipProps.GatewayAddresses)
            {
                if (gw.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !gw.Address.Equals(IPAddress.Any) &&
                    !gw.Address.Equals(IPAddress.None))
                {
                    var ipv4Props = ipProps.GetIPv4Properties();
                    int index = ipv4Props != null ? ipv4Props.Index : 0;

                    IPAddress? localIp = null;
                    foreach (var u in ipProps.UnicastAddresses)
                    {
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            localIp = u.Address;
                            break;
                        }
                    }

                    if (localIp != null)
                    {
                        return (gw.Address, index, localIp, ni.Name);
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// 解析远程服务器的 IP 地址 (如果是域名则执行 DNS 解析)
    /// </summary>
    public static IPAddress? ResolveServerIp(string serverHost)
    {
        if (IPAddress.TryParse(serverHost, out var ip))
        {
            return ip;
        }

        try
        {
            var addresses = Dns.GetHostAddresses(serverHost);
            foreach (var addr in addresses)
            {
                if (addr.AddressFamily == AddressFamily.InterNetwork)
                {
                    return addr;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 添加防回环明细路由：确保发往代理节点/Cloudflare 的流量直连物理网关
    /// </summary>
    public static bool AddBypassRoute(IPAddress serverIp, IPAddress defaultGateway, int interfaceIndex)
    {
        string cmd = $"route add {serverIp} mask 255.255.255.255 {defaultGateway} metric 5 if {interfaceIndex}";
        return RunNetCommand(cmd);
    }

    /// <summary>
    /// 删除防回环明细路由
    /// </summary>
    public static bool DeleteBypassRoute(IPAddress serverIp)
    {
        string cmd = $"route delete {serverIp}";
        return RunNetCommand(cmd);
    }

    /// <summary>
    /// 启用全局 TUN 模式接管路由 (使用 0.0.0.0/1 和 128.0.0.0/1 双段路由无痕覆盖)
    /// </summary>
    public static bool EnableTunGlobalRoutes(IPAddress tunGateway, int tunInterfaceIndex)
    {
        // 优先于 0.0.0.0/0 默认路由
        bool r1 = RunNetCommand($"route add 0.0.0.0 mask 128.0.0.0 {tunGateway} metric 6 if {tunInterfaceIndex}");
        bool r2 = RunNetCommand($"route add 128.0.0.0 mask 128.0.0.0 {tunGateway} metric 6 if {tunInterfaceIndex}");
        return r1 && r2;
    }

    /// <summary>
    /// 还原全局 TUN 路由
    /// </summary>
    public static void DisableTunGlobalRoutes(IPAddress tunGateway)
    {
        RunNetCommand($"route delete 0.0.0.0 mask 128.0.0.0 {tunGateway}");
        RunNetCommand($"route delete 128.0.0.0 mask 128.0.0.0 {tunGateway}");
    }

    /// <summary>
    /// 配置网络适配器 IP、掩码与网关
    /// </summary>
    public static bool ConfigureInterfaceIp(string adapterName, string ip, string mask, string gateway)
    {
        string cmd = $"netsh interface ip set address name=\"{adapterName}\" static {ip} {mask} {gateway} 1";
        return RunNetCommand(cmd);
    }

    /// <summary>
    /// 配置网络适配器 DNS
    /// </summary>
    public static bool ConfigureInterfaceDns(string adapterName, string primaryDns = "1.1.1.1")
    {
        string cmd = $"netsh interface ip set dns name=\"{adapterName}\" static {primaryDns}";
        return RunNetCommand(cmd);
    }

    private static bool RunNetCommand(string commandLine)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c {commandLine}")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(3000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
