using System;
using System.Threading.Tasks;

#if IOS
using Foundation;
using NetworkExtension;
#endif

namespace Proxy.Client.Maui.Services;

public static class IosVpnHelper
{
    public static async Task<(bool Success, string Message)> SetupSystemVpnAsync(int localPort)
    {
#if IOS
        try
        {
            var managers = await NETunnelProviderManager.LoadAllFromPreferencesAsync();
            var manager = (managers != null && managers.Length > 0)
                ? managers[0]
                : new NETunnelProviderManager();

            var protocol = new NETunnelProviderProtocol
            {
                ServerAddress = $"127.0.0.1:{localPort}",
                ProviderBundleIdentifier = "com.proxy.client.ios",
            };

            manager.ProtocolConfiguration = protocol;
            manager.LocalizedDescription = "Cloudflare 代理 VPN";
            manager.Enabled = true;

            await manager.SaveToPreferencesAsync();
            return (true, "✅ 系统 VPN 配置已成功写入！\n\n请在 iOS 系统弹窗中点击【允许】，随后前往【系统设置 -> VPN】开启 VPN 开关即可在蜂窝流量下使用代理！");
        }
        catch (Exception ex)
        {
            return (false, $"配置系统 VPN 提示: {ex.Message}\n\n💡 提示：若您的签名证书未包含 NetworkExtension 特殊权限，系统可能限制写入，请使用下方【方案 2 描述文件】或【方案 3 配合规则客户端】。");
        }
#else
        await Task.CompletedTask;
        return (false, "当前非 iOS 平台，无需配置 iOS 系统 VPN");
#endif
    }
}
