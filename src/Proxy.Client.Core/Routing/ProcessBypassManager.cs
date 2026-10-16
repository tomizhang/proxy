using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;

namespace Proxy.Client.Core.Routing;

/// <summary>
/// 应用程序进程直连分流管理器 (根据发起请求的进程名，如 rustdesk.exe，进行白名单拦截直连，不走代理隧道)
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
    /// 解析分流程序名单 (分号、逗号或换行分隔，支持路径、带.exe或不带.exe)
    /// </summary>
    public void ParseProcessList(string processList)
    {
        _bypassedProcesses.Clear();
        _portCache.Clear();
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
    /// 检查指定进程名是否命中白名单
    /// </summary>
    public bool IsProcessMatched(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        string name = Path.GetFileName(processName).Trim().ToLowerInvariant();
        return _bypassedProcesses.Contains(name);
    }

    /// <summary>
    /// 判断给定的客户端端口及目标地址是否属于被放行的直连程序
    /// </summary>
    public bool ShouldBypass(int clientPort, string targetHost, int targetPort, out string matchedProcess)
    {
        matchedProcess = string.Empty;

        // 1. 优先尝试根据接入端口获取发起进程 (SOCKS5 / 系统代理模式直接命中发起程序)
        var clientProc = GetProcessInfoByPort(clientPort);
        if (!string.IsNullOrEmpty(clientProc))
        {
            // 如果连接来自 tun2socks，说明是 TUN 模式，此时真正的源程序在 Windows TCP 表中发往 targetHost:targetPort
            if (clientProc.Equals("tun2socks", StringComparison.OrdinalIgnoreCase) ||
                clientProc.Equals("tun2socks.exe", StringComparison.OrdinalIgnoreCase))
            {
                var tunOriginProc = FindOriginProcessForTun(targetHost, targetPort);
                if (!string.IsNullOrEmpty(tunOriginProc) && IsProcessMatched(tunOriginProc))
                {
                    matchedProcess = tunOriginProc;
                    return true;
                }
            }
            else if (IsProcessMatched(clientProc))
            {
                matchedProcess = clientProc;
                return true;
            }
        }
        else
        {
            // 端口直查未取到时（如 TUN 虚拟网卡模式部分场景），通过目标端口匹配
            var tunOriginProc = FindOriginProcessForTun(targetHost, targetPort);
            if (!string.IsNullOrEmpty(tunOriginProc) && IsProcessMatched(tunOriginProc))
            {
                matchedProcess = tunOriginProc;
                return true;
            }
        }

        return false;
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
            return cached.Name;
        }

        int pid = GetPidByLocalPort(localPort);
        if (pid <= 0) return null;

        string? name = GetProcessNameByPid(pid);
        if (!string.IsNullOrEmpty(name))
        {
            _portCache[localPort] = (pid, name, now.AddSeconds(2));
        }

        return name;
    }

    /// <summary>
    /// TUN 模式下，查找发往 targetHost:targetPort 的原始应用程序进程名
    /// </summary>
    private string? FindOriginProcessForTun(string targetHost, int targetPort)
    {
        if (targetPort <= 0) return null;

        IPAddress? targetIp = null;
        if (IPAddress.TryParse(targetHost, out var parsedIp))
        {
            targetIp = parsedIp;
        }

        int pid = GetPidByRemoteEndpoint(targetIp, targetPort);
        if (pid > 0)
        {
            return GetProcessNameByPid(pid);
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
                        return Marshal.ReadInt32(current, 20);
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
                        return Marshal.ReadInt32(current, 52);
                    }
                }
            }
        }
        catch
        {
            // 忽略读取异常
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return -1;
    }

    /// <summary>
    /// 根据远端 IP 和远端端口在 Windows TCP 表中查找对应的拥有进程 PID (用于 TUN 模式溯源)
    /// </summary>
    public static int GetPidByRemoteEndpoint(IPAddress? remoteIp, int remotePort)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return -1;

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

            uint targetAddrUint = 0;
            if (remoteIp != null && remoteIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                byte[] bytes = remoteIp.GetAddressBytes();
                targetAddrUint = BitConverter.ToUInt32(bytes, 0);
            }

            int bestMatchPid = -1;

            for (int i = 0; i < numEntries; i++)
            {
                IntPtr current = IntPtr.Add(rowPtr, i * 24);
                int rawRemotePort = Marshal.ReadInt32(current, 16);
                ushort port = (ushort)(((rawRemotePort & 0xFF) << 8) | ((rawRemotePort >> 8) & 0xFF));

                if (port == remotePort)
                {
                    uint remoteAddr = (uint)Marshal.ReadInt32(current, 12);
                    int pid = Marshal.ReadInt32(current, 20);

                    if (targetAddrUint != 0 && remoteAddr == targetAddrUint)
                    {
                        // IP 和 端口 完全精确匹配
                        return pid;
                    }

                    // 端口命中备选
                    if (bestMatchPid == -1 && pid > 4)
                    {
                        bestMatchPid = pid;
                    }
                }
            }

            return bestMatchPid;
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
