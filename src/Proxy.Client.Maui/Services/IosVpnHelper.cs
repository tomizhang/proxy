using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;
using Proxy.Common.Utils;

namespace Proxy.Client.Maui.Services;

/// <summary>
/// iOS 蜂窝移动网络流量与 VPN 配置服务
/// 提供 iOS 系统 VPN 配置引导与 APN 描述文件生成
/// </summary>
public static class IosVpnHelper
{
    /// <summary>
    /// 为当前代理端口生成系统专属 VPN / 蜂窝 APN 配置文件
    /// </summary>
    public static async Task<(bool Success, string Message, string? FilePath)> PrepareVpnProfileAsync(int localPort)
    {
        try
        {
            string xml = MobileConfigHelper.GenerateCellularMobileConfigXml(localPort);
            string cachePath = Path.Combine(FileSystem.CacheDirectory, $"CloudflareProxy_Cellular_{localPort}.mobileconfig");
            await File.WriteAllTextAsync(cachePath, xml, System.Text.Encoding.UTF8);

            return (true, "已成功生成适用于当前代理端口的系统 VPN / 蜂窝描述文件！", cachePath);
        }
        catch (Exception ex)
        {
            return (false, $"生成配置失败: {ex.Message}", null);
        }
    }
}
