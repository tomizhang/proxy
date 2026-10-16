using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Threading;
using System.Runtime.Versioning;

namespace Proxy.Client.Core.Tun;

/// <summary>
/// TUN 虚拟网卡与全局流量接管管理器 (基于 Wintun 与 tun2socks 工业级 TCP/IP 栈)
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TunManager : IDisposable
{
    private const string AdapterName = "ProxyTun";

    private Process? _tun2socksProcess;
    private IPAddress? _serverIp;
    private IPAddress? _defaultGateway;
    private int _gatewayIfIndex;
    private int _tunIfIndex;
    private bool _isStarted;

    public event Action<string>? LogMessage;

    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static string? FindTun2SocksExecutable()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string p1 = Path.Combine(baseDir, "tun2socks.exe");
        if (File.Exists(p1)) return p1;

        string p2 = Path.Combine(Directory.GetCurrentDirectory(), "tun2socks.exe");
        if (File.Exists(p2)) return p2;

        string p3 = Path.Combine(baseDir, "..", "..", "..", "tun2socks.exe");
        if (File.Exists(p3)) return Path.GetFullPath(p3);

        return null;
    }

    public bool Start(string serverHost, int socks5Port = 20808, string tunIp = "10.0.0.2", string tunMask = "255.255.255.0", string tunGateway = "10.0.0.1")
    {
        if (_isStarted) return true;

        if (!IsAdministrator())
        {
            throw new UnauthorizedAccessException("TUN 全局虚拟网卡模式需要管理员权限运行，请以管理员身份重新启动程序。");
        }

        string? tun2socksPath = FindTun2SocksExecutable();
        if (string.IsNullOrEmpty(tun2socksPath))
        {
            throw new FileNotFoundException("未找到 tun2socks.exe 转发核心。请确保程序目录下存在 tun2socks.exe。");
        }

        if (!Wintun.IsWintunAvailable())
        {
            throw new FileNotFoundException("未找到 wintun.dll 驱动库。请确保程序目录下存在 64 位 wintun.dll。");
        }

        LogMessage?.Invoke("正在初始化 TUN 虚拟网卡与网络路由防回环机制...");

        // 1. 清理可能残留的历史 tun2socks 进程
        try
        {
            foreach (var p in Process.GetProcessesByName("tun2socks"))
            {
                p.Kill();
            }
        }
        catch { }

        // 2. 获取当前物理网关与网卡索引
        var gwInfo = RouteManager.GetDefaultGateway();
        if (gwInfo == null)
        {
            throw new InvalidOperationException("未找到当前活动的物理网络网关，无法启用 TUN 全局路由模式。");
        }
        _defaultGateway = gwInfo.Value.Gateway;
        _gatewayIfIndex = gwInfo.Value.InterfaceIndex;

        // 3. 解析远程服务器/CDN 真实 IP
        _serverIp = RouteManager.ResolveServerIp(serverHost);
        if (_serverIp == null)
        {
            throw new InvalidOperationException($"无法解析代理服务器/CDN 节点 IP: {serverHost}");
        }

        // 4. 建立防回环明细路由 (节点直连物理网关，防止无限循环)
        LogMessage?.Invoke($"添加节点直连防回环路由: {_serverIp} -> {_defaultGateway}");
        RouteManager.AddBypassRoute(_serverIp, _defaultGateway, _gatewayIfIndex);

        // 常用公共 DNS 直连防回环 (确保 DNS 解析极速直达物理网关)
        RouteManager.AddBypassRoute(IPAddress.Parse("1.1.1.1"), _defaultGateway, _gatewayIfIndex);
        RouteManager.AddBypassRoute(IPAddress.Parse("8.8.8.8"), _defaultGateway, _gatewayIfIndex);

        // 5. 启动 tun2socks 进程
        LogMessage?.Invoke($"正在拉起 tun2socks 核心 (对接本地代理 127.0.0.1:{socks5Port})...");
        var psi = new ProcessStartInfo
        {
            FileName = tun2socksPath,
            Arguments = $"--device tun://{AdapterName} --proxy socks5://127.0.0.1:{socks5Port} --loglevel warn --tcp-auto-tuning --tcp-rcvbuf 4m --tcp-sndbuf 4m --udp-timeout 10s",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(tun2socksPath) ?? ""
        };

        try
        {
            _tun2socksProcess = Process.Start(psi);
        }
        catch (Exception ex)
        {
            CleanupRoutes();
            throw new InvalidOperationException($"启动 tun2socks 进程失败: {ex.Message}", ex);
        }

        if (_tun2socksProcess == null)
        {
            CleanupRoutes();
            throw new InvalidOperationException("创建 tun2socks 进程句柄失败。");
        }

        _tun2socksProcess.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                LogMessage?.Invoke($"[tun2socks] {e.Data}");
            }
        };
        _tun2socksProcess.BeginErrorReadLine();

        // 6. 等待 Wintun 虚拟网卡在操作系统中注册就绪
        LogMessage?.Invoke("等待 Wintun 虚拟网络适配器就绪...");
        int retries = 25; // 25 * 200ms = 5s
        while (retries-- > 0)
        {
            if (_tun2socksProcess.HasExited)
            {
                CleanupRoutes();
                throw new InvalidOperationException($"tun2socks 核心异常退出 (ExitCode: {_tun2socksProcess.ExitCode})。");
            }

            _tunIfIndex = GetTunInterfaceIndex(AdapterName);
            if (_tunIfIndex > 0) break;
            Thread.Sleep(200);
        }

        if (_tunIfIndex <= 0)
        {
            Stop();
            throw new TimeoutException("等待 Wintun 虚拟网卡创建超时。请确认当前运行环境具有管理员权限。");
        }

        // 7. 配置虚拟网卡 IP 与 DNS
        LogMessage?.Invoke($"配置网卡 IP: {tunIp}, 掩码: {tunMask}, 网关: {tunGateway}");
        bool ipOk = false;
        for (int i = 0; i < 6; i++)
        {
            Thread.Sleep(500);
            if (RouteManager.ConfigureInterfaceIp(AdapterName, tunIp, tunMask, tunGateway))
            {
                ipOk = true;
                break;
            }
        }

        if (!ipOk)
        {
            LogMessage?.Invoke("提示：网卡 IP 静态配置未生效，尝试使用无网关静态分配...");
            RouteManager.ConfigureInterfaceIp(AdapterName, tunIp, tunMask, "none");
        }

        RouteManager.ConfigureInterfaceDns(AdapterName, "1.1.1.1");
        Thread.Sleep(300);

        // 8. 启用全局无痕双段覆盖路由 (0.0.0.0/1, 128.0.0.0/1)
        LogMessage?.Invoke("无痕接管全系统网络流量 (0.0.0.0/1, 128.0.0.0/1)...");
        RouteManager.EnableTunGlobalRoutes(IPAddress.Parse(tunGateway), _tunIfIndex);

        _isStarted = true;
        LogMessage?.Invoke("TUN 模式启动成功！所有软件、游戏与命令行的网络流量均已由虚拟网卡全面接管！");
        return true;
    }

    public void Stop()
    {
        if (!_isStarted && _tun2socksProcess == null) return;

        LogMessage?.Invoke("正在关闭 TUN 模式并还原网络路由...");

        CleanupRoutes();

        if (_tun2socksProcess != null)
        {
            try
            {
                if (!_tun2socksProcess.HasExited)
                {
                    _tun2socksProcess.Kill(entireProcessTree: true);
                }
                _tun2socksProcess.Dispose();
            }
            catch { }
            _tun2socksProcess = null;
        }

        _isStarted = false;
        LogMessage?.Invoke("TUN 模式已关闭，网络路由已完全还原。");
    }

    private void CleanupRoutes()
    {
        try
        {
            RouteManager.DisableTunGlobalRoutes(IPAddress.Parse("10.0.0.1"));
        }
        catch { }

        if (_serverIp != null)
        {
            try { RouteManager.DeleteBypassRoute(_serverIp); } catch { }
            _serverIp = null;
        }

        try { RouteManager.DeleteBypassRoute(IPAddress.Parse("1.1.1.1")); } catch { }
        try { RouteManager.DeleteBypassRoute(IPAddress.Parse("8.8.8.8")); } catch { }
    }

    private static int GetTunInterfaceIndex(string adapterName)
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase) ||
                ni.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                ni.Description.Contains("tun2socks", StringComparison.OrdinalIgnoreCase))
            {
                var ipv4 = ni.GetIPProperties().GetIPv4Properties();
                if (ipv4 != null) return ipv4.Index;
            }
        }
        return 0;
    }

    public void Dispose()
    {
        Stop();
    }
}
