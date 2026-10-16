using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Proxy.Client.Core.Sys;

/// <summary>
/// Windows 系统代理管理器 (工业级注册表 + WinINet 广播热刷新方案)
/// 彻底规避 64 位 P/Invoke 结构体内存对齐导致的 AccessViolationException
/// </summary>
[SupportedOSPlatform("windows")]
public static class SystemProxyManager
{
    private const string InternetSettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
    private const int INTERNET_OPTION_REFRESH = 37;

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

    /// <summary>
    /// 设置 Windows 系统代理并通知所有浏览器立即热生效
    /// </summary>
    /// <param name="proxyAddress">代理地址，例如 "127.0.0.1:20808"</param>
    /// <param name="bypassList">绕过列表</param>
    public static bool SetSystemProxy(string proxyAddress, string bypassList = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;<local>")
    {
        try
        {
            using var reg = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (reg == null) return false;

            reg.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            reg.SetValue("ProxyServer", proxyAddress, RegistryValueKind.String);
            reg.SetValue("ProxyOverride", bypassList, RegistryValueKind.String);

            // 广播通知系统与所有浏览器刷新网络代理配置
            RefreshSystemSettings();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 清除并还原 Windows 系统代理为直连
    /// </summary>
    public static bool ClearSystemProxy()
    {
        try
        {
            using var reg = Registry.CurrentUser.OpenSubKey(InternetSettingsKey, writable: true);
            if (reg == null) return false;

            reg.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);

            // 广播刷新通知
            RefreshSystemSettings();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void RefreshSystemSettings()
    {
        try
        {
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
        }
        catch { }
    }
}
