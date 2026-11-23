using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using Proxy.Common.Models;

namespace Proxy.Client.Core.Routing;

/// <summary>
/// 应用程序进程分流管理器 (支持“仅直连选中的应用”或“只为选中的应用进行代理”)
/// </summary>
public sealed class ProcessBypassManager
{
    private readonly HashSet<string> _bypassedProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, (string Name, DateTime Expiry)> _pidNameCache = new();
    private readonly ConcurrentDictionary<int, (int Pid, string Name, DateTime Expiry)> _portCache = new();

    public ProcessBypassManager(string processList)
    {
        ParseProcessList(processList);
    }

    /// <summary>
    /// 即时清空所有端口与 PID 识别缓存，确保更新名单或模式后新请求立即重新探测
    /// </summary>
    public void ClearCaches()
    {
        _portCache.Clear();
        _pidNameCache.Clear();
    }

    /// <summary>
    /// 解析分流程序名单 (分号、逗号或换行分隔，支持路径、带.exe或不带.exe)
    /// </summary>
    public void ParseProcessList(string processList)
    {
        _bypassedProcesses.Clear();
        ClearCaches();
        if (string.IsNullOrWhiteSpace(processList)) return;

        var tokens = processList.Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawToken in tokens)
        {
            string token = rawToken.Trim();
            if (string.IsNullOrEmpty(token) || token.StartsWith('!') || token.StartsWith('#')) continue;

            string fileName = Path.GetFileName(token).ToLowerInvariant();
            if (string.IsNullOrEmpty(fileName)) continue;

            _bypassedProcesses.Add(fileName);

            if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                _bypassedProcesses.Add(fileName[..^4]);
            }
            else
            {
                _bypassedProcesses.Add(fileName + ".exe");
            }
        }
    }

    /// <summary>
    /// 检查指定进程名是否命中名单
    /// </summary>
    public bool IsProcessMatched(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string name = Path.GetFileName(processName).Trim().ToLowerInvariant();
        if (_bypassedProcesses.Contains(name)) return true;

        // 智能关联：若名单中包含 antigravity，自动覆盖其附属服务 language_server
        if (name.Contains("language_server"))
        {
            if (_bypassedProcesses.Contains("antigravity") || _bypassedProcesses.Contains("antigravity.exe"))
            {
                return true;
            }
        }

        // 智能关联：若名单中包含 code 或 cursor，自动覆盖其相关辅助进程 (如 code-tunnel, cursor-helper)
        if ((name.StartsWith("code-") || name.StartsWith("cursor-")) &&
            (_bypassedProcesses.Contains("code") || _bypassedProcesses.Contains("code.exe") ||
             _bypassedProcesses.Contains("cursor") || _bypassedProcesses.Contains("cursor.exe")))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 解析请求的发起源进程名称
    /// </summary>
    public string? ResolveOriginProcess(int clientPort, string targetHost, int targetPort, string? rawTargetHost = null)
    {
        var clientProc = GetProcessInfoByPort(clientPort);
        if (!string.IsNullOrEmpty(clientProc))
        {
            if (clientProc.Equals("tun2socks", StringComparison.OrdinalIgnoreCase) ||
                clientProc.Equals("tun2socks.exe", StringComparison.OrdinalIgnoreCase))
            {
                var tunOriginProc = FindOriginProcessForTun(rawTargetHost, targetHost, targetPort);
                return !string.IsNullOrEmpty(tunOriginProc) ? tunOriginProc : clientProc;
            }
            return clientProc;
        }

        return FindOriginProcessForTun(rawTargetHost, targetHost, targetPort);
    }

    /// <summary>
    /// 判断给定的客户端端口及目标地址是否应当直连（根据所选模式：仅直连选中应用 或 只为选中应用代理）
    /// </summary>
    public bool ShouldBypass(
        int clientPort,
        string targetHost,
        int targetPort,
        ProcessProxyMode mode,
        out string matchedProcess,
        out string logReason,
        string? rawTargetHost = null)
    {
        matchedProcess = string.Empty;
        logReason = string.Empty;

        string? proc = ResolveOriginProcess(clientPort, targetHost, targetPort, rawTargetHost);
        bool hasProc = !string.IsNullOrEmpty(proc);
        bool isMatched = hasProc && IsProcessMatched(proc!);

        if (mode == ProcessProxyMode.ProxySelected)
        {
            // 模式：只为选中的应用进行代理 (白名单模式)
            if (isMatched)
            {
                // 该程序在选中名单中 -> 走代理！(返回 false，即不直连绕过)
                matchedProcess = proc!;
                return false;
            }
            else
            {
                // 该程序不在选中名单中 -> 直连！(返回 true，直连绕过)
                matchedProcess = hasProc ? proc! : "未在代理名单应用";
                logReason = $"[仅代理模式] {matchedProcess} -> {targetHost}:{targetPort} (非选中代理应用，直接连接)";
                return true;
            }
        }
        else
        {
            // 模式：仅直连选中的应用 (黑名单模式，默认)
            if (isMatched)
            {
                matchedProcess = proc!;
                logReason = $"[程序直连] {proc} -> {targetHost}:{targetPort} (命中排除名单，跳过代理)";
                return true;
            }

            matchedProcess = hasProc ? proc! : string.Empty;
            return false;
        }
    }

    /// <summary>
    /// 兼容旧版调用的重载，默认模式为直连排除模式
    /// </summary>
    public bool ShouldBypass(int clientPort, string targetHost, int targetPort, out string matchedProcess)
    {
        return ShouldBypass(clientPort, targetHost, targetPort, ProcessProxyMode.BypassSelected, out matchedProcess, out _);
    }

    /// <summary>
    /// 用于快速判断指定进程名在当前模式下是否应直连（主要用于单元测试与策略校验）
    /// </summary>
    public bool ShouldBypassProcessName(string processName, ProcessProxyMode mode, out string logReason)
    {
        bool isMatched = !string.IsNullOrWhiteSpace(processName) && IsProcessMatched(processName);
        if (mode == ProcessProxyMode.ProxySelected)
        {
            if (isMatched)
            {
                logReason = $"[仅代理模式] {processName} (命中选中代理名单 -> 走代理)";
                return false;
            }
            else
            {
                logReason = $"[仅代理模式] {processName} (非选中代理应用 -> 直连)";
                return true;
            }
        }
        else
        {
            if (isMatched)
            {
                logReason = $"[程序直连] {processName} (命中排除名单 -> 直连)";
                return true;
            }
            logReason = $"[常规代理] {processName} (未在排除名单 -> 走代理)";
            return false;
        }
    }

    /// <summary>
    /// 根据本地端口查询拥有该端口的进程名称
    /// </summary>
    public string? GetProcessInfoByPort(int localPort)
    {
        if (localPort <= 0) return null;

        var now = DateTime.UtcNow;
        if (_portCache.TryGetValue(localPort, out var cached) && cached.Expiry > now)
        {
            // 校验缓存的 PID 是否仍然存活且名称未变，避免端口复用导致的归属错误
            try
            {
                using var p = Process.GetProcessById(cached.Pid);
                if (!p.HasExited && string.Equals(p.ProcessName, cached.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return cached.Name;
                }
            }
            catch
            {
                _portCache.TryRemove(localPort, out _);
            }
        }

        // 定期惰性修剪过期缓存
        if (_portCache.Count > 300)
        {
            foreach (var kvp in _portCache)
            {
                if (kvp.Value.Expiry < now)
                {
                    _portCache.TryRemove(kvp.Key, out _);
                }
            }
        }

        int pid = GetPidByLocalPort(localPort);
        if (pid <= 0)
        {
            // Windows 内核 TCP 表偶尔存在毫秒级微小延迟，快速微等待后重试一次
            Thread.Sleep(5);
            pid = GetPidByLocalPort(localPort);
        }
        if (pid <= 0) return null;

        string? name = GetProcessNameByPid(pid);
        if (!string.IsNullOrEmpty(name))
        {
            // 端口分配频繁轮转，使用 1 秒短缓存降低端口复用误判风险
            _portCache[localPort] = (pid, name, now.AddSeconds(1));
        }

        return name;
    }

    /// <summary>
    /// TUN 模式下，查找发往 targetHost:targetPort 的原始应用程序进程名 (精确匹配远端 IP 与端口)
    /// </summary>
    private string? FindOriginProcessForTun(string? rawTargetHost, string? targetHost, int targetPort)
    {
        if (targetPort <= 0) return null;

        // 1. 优先尝试从原始目标 IP 解析 (TUN 模式下 rawTargetHost 通常为真实的远端 IP)
        if (!string.IsNullOrWhiteSpace(rawTargetHost) && IPAddress.TryParse(rawTargetHost, out var ip1))
        {
            int pid = GetPidByRemoteEndpoint(ip1, targetPort);
            if (pid > 0) return GetProcessNameByPid(pid);
        }

        // 2. 尝试从 targetHost 解析目标 IP
        if (!string.IsNullOrWhiteSpace(targetHost) && IPAddress.TryParse(targetHost, out var ip2))
        {
            int pid = GetPidByRemoteEndpoint(ip2, targetPort);
            if (pid > 0) return GetProcessNameByPid(pid);
        }

        // 3. 若均为域名，尝试通过 DNS 获取候选 IP 列表并在 TCP 表中精确查找
        string? domain = !string.IsNullOrWhiteSpace(targetHost) && !IPAddress.TryParse(targetHost, out _)
            ? targetHost
            : (!string.IsNullOrWhiteSpace(rawTargetHost) && !IPAddress.TryParse(rawTargetHost, out _) ? rawTargetHost : null);

        if (!string.IsNullOrWhiteSpace(domain))
        {
            try
            {
                var ips = Dns.GetHostAddresses(domain);
                foreach (var addr in ips)
                {
                    int pid = GetPidByRemoteEndpoint(addr, targetPort);
                    if (pid > 0) return GetProcessNameByPid(pid);
                }
            }
            catch
            {
                // 忽略 DNS 探测异常
            }
        }

        return null;
    }

    /// <summary>
    /// 根据 PID 获取进程名（含 10 秒缓存）
    /// </summary>
    public string? GetProcessNameByPid(int pid)
    {
        if (pid <= 0) return null;
        if (pid == 4) return "System";

        var now = DateTime.UtcNow;
        if (_pidNameCache.TryGetValue(pid, out var cached) && cached.Expiry > now)
        {
            return cached.Name;
        }

        if (_pidNameCache.Count > 200)
        {
            foreach (var kvp in _pidNameCache)
            {
                if (kvp.Value.Expiry < now)
                {
                    _pidNameCache.TryRemove(kvp.Key, out _);
                }
            }
        }

        try
        {
            using var proc = Process.GetProcessById(pid);
            string name = proc.ProcessName;
            _pidNameCache[pid] = (name, now.AddSeconds(10));
            return name;
        }
        catch
        {
            return null;
        }
    }

    #region Windows Native IP Helper API (iphlpapi.dll)

    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        bool bOrder,
        int ulAf,
        int TableClass,
        uint reserved = 0);

    /// <summary>
    /// 通过 Windows TCP 表查询占用 localPort 的进程 PID
    /// </summary>
    public static int GetPidByLocalPort(int localPort)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return -1;

        // 优先在 IPv4 表查找
        int pid = QueryPidByLocalPortInternal(AF_INET, localPort);
        if (pid > 0) return pid;

        // 次选在 IPv6 表查找
        return QueryPidByLocalPortInternal(AF_INET6, localPort);
    }

    private static int QueryPidByLocalPortInternal(int af, int targetPort)
    {
        int size = 0;
        uint res = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return -1;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            res = GetExtendedTcpTable(buffer, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
            if (res != 0) return -1;

            int numEntries = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);
            int fallbackPid = -1;

            if (af == AF_INET)
            {
                // MIB_TCPROW_OWNER_PID 结构体大小: 24 字节
                // dwState(4), dwLocalAddr(4), dwLocalPort(4), dwRemoteAddr(4), dwRemotePort(4), dwOwningPid(4)
                for (int i = 0; i < numEntries; i++)
                {
                    IntPtr current = IntPtr.Add(rowPtr, i * 24);
                    int rawLocalPort = Marshal.ReadInt32(current, 8);
                    ushort port = (ushort)(((rawLocalPort & 0xFF) << 8) | ((rawLocalPort >> 8) & 0xFF));

                    if (port == targetPort)
                    {
                        int state = Marshal.ReadInt32(current, 0);
                        int pid = Marshal.ReadInt32(current, 20);
                        if (pid > 4)
                        {
                            // 优先匹配活跃连接 (ESTABLISHED = 5, SYN_SENT = 3, SYN_RCVD = 4)
                            if (state == 5 || state == 3 || state == 4)
                            {
                                return pid;
                            }
                            // 过滤已关闭/TIME_WAIT (11, 12, 1) 的陈旧条目
                            if (fallbackPid == -1 && state != 11 && state != 12 && state != 1)
                            {
                                fallbackPid = pid;
                            }
                        }
                    }
                }
            }
            else if (af == AF_INET6)
            {
                // MIB_TCP6ROW_OWNER_PID 结构体大小: 56 字节
                // ucLocalAddr(16), dwLocalScopeId(4), dwLocalPort(4), ucRemoteAddr(16), dwRemoteScopeId(4), dwRemotePort(4), dwState(4), dwOwningPid(4)
                for (int i = 0; i < numEntries; i++)
                {
                    IntPtr current = IntPtr.Add(rowPtr, i * 56);
                    int rawLocalPort = Marshal.ReadInt32(current, 20);
                    ushort port = (ushort)(((rawLocalPort & 0xFF) << 8) | ((rawLocalPort >> 8) & 0xFF));

                    if (port == targetPort)
                    {
                        int state = Marshal.ReadInt32(current, 48);
                        int pid = Marshal.ReadInt32(current, 52);
                        if (pid > 4)
                        {
                            if (state == 5 || state == 3 || state == 4)
                            {
                                return pid;
                            }
                            if (fallbackPid == -1 && state != 11 && state != 12 && state != 1)
                            {
                                fallbackPid = pid;
                            }
                        }
                    }
                }
            }

            return fallbackPid;
        }
        catch
        {
            return -1;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 根据远端 IP 和远端端口在 Windows TCP 表中查找对应的拥有进程 PID (用于 TUN 模式精准溯源)
    /// </summary>
    public static int GetPidByRemoteEndpoint(IPAddress? remoteIp, int remotePort)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || remoteIp == null || remotePort <= 0)
        {
            return -1;
        }

        if (remoteIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return QueryPidByRemoteEndpointV4(remoteIp, remotePort);
        }
        else if (remoteIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return QueryPidByRemoteEndpointV6(remoteIp, remotePort);
        }

        return -1;
    }

    private static int QueryPidByRemoteEndpointV4(IPAddress remoteIp, int remotePort)
    {
        int size = 0;
        uint res = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return -1;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            res = GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
            if (res != 0) return -1;

            int numEntries = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);

            byte[] bytes = remoteIp.GetAddressBytes();
            uint targetAddrUint = BitConverter.ToUInt32(bytes, 0);
            int fallbackPid = -1;

            for (int i = 0; i < numEntries; i++)
            {
                IntPtr current = IntPtr.Add(rowPtr, i * 24);
                int rawRemotePort = Marshal.ReadInt32(current, 16);
                ushort port = (ushort)(((rawRemotePort & 0xFF) << 8) | ((rawRemotePort >> 8) & 0xFF));

                // 必须严格同时匹配远端端口与远端 IP，绝不可仅匹配端口以避免串流误判
                if (port == remotePort)
                {
                    uint remoteAddr = (uint)Marshal.ReadInt32(current, 12);
                    if (remoteAddr == targetAddrUint)
                    {
                        int state = Marshal.ReadInt32(current, 0);
                        int pid = Marshal.ReadInt32(current, 20);

                        if (pid > 4)
                        {
                            // 优先返回活跃建立的连接
                            if (state == 5 || state == 3 || state == 4)
                            {
                                return pid;
                            }
                            if (fallbackPid == -1 && state != 11 && state != 12 && state != 1)
                            {
                                fallbackPid = pid;
                            }
                        }
                    }
                }
            }

            return fallbackPid;
        }
        catch
        {
            return -1;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static int QueryPidByRemoteEndpointV6(IPAddress remoteIp, int remotePort)
    {
        int size = 0;
        uint res = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return -1;

        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            res = GetExtendedTcpTable(buffer, ref size, false, AF_INET6, TCP_TABLE_OWNER_PID_ALL, 0);
            if (res != 0) return -1;

            int numEntries = Marshal.ReadInt32(buffer);
            IntPtr rowPtr = IntPtr.Add(buffer, 4);

            byte[] targetBytes = remoteIp.GetAddressBytes();
            byte[] rowRemoteBytes = new byte[16];
            int fallbackPid = -1;

            for (int i = 0; i < numEntries; i++)
            {
                IntPtr current = IntPtr.Add(rowPtr, i * 56);
                int rawRemotePort = Marshal.ReadInt32(current, 44);
                ushort port = (ushort)(((rawRemotePort & 0xFF) << 8) | ((rawRemotePort >> 8) & 0xFF));

                if (port == remotePort)
                {
                    Marshal.Copy(IntPtr.Add(current, 24), rowRemoteBytes, 0, 16);
                    if (rowRemoteBytes.AsSpan().SequenceEqual(targetBytes))
                    {
                        int state = Marshal.ReadInt32(current, 48);
                        int pid = Marshal.ReadInt32(current, 52);

                        if (pid > 4)
                        {
                            if (state == 5 || state == 3 || state == 4)
                            {
                                return pid;
                            }
                            if (fallbackPid == -1 && state != 11 && state != 12 && state != 1)
                            {
                                fallbackPid = pid;
                            }
                        }
                    }
                }
            }

            return fallbackPid;
        }
        catch
        {
            return -1;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    #endregion
}
