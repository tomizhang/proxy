#if IOS
using System;
using System.Threading.Tasks;
using Foundation;
using NetworkExtension;

namespace Proxy.Client.Maui.Platforms.iOS;

public static class VpnService
{
    public static async Task<(bool Success, string Message)> ConfigureVpnAsync(int localPort)
    {
        var tcs = new TaskCompletionSource<(bool, string)>();

        try
        {
            NETunnelProviderManager.LoadAllFromPreferences((managers, error) =>
            {
                if (error != null)
                {
                    tcs.TrySetResult((false, $"读取系统 VPN 配置失败: {error.LocalizedDescription}"));
                    return;
                }

                NETunnelProviderManager manager = (managers != null && managers.Length > 0)
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

                manager.SaveToPreferences(saveError =>
                {
                    if (saveError != null)
                    {
                        tcs.TrySetResult((false, $"保存系统 VPN 失败: {saveError.LocalizedDescription}\n\n💡 提示：若当前签名证书未包含 NetworkExtension 特殊权限，系统可能限制写入，请使用下方【方案 2 描述文件】或【方案 3 配合规则客户端】。"));
                    }
                    else
                    {
                        tcs.TrySetResult((true, "✅ 系统 VPN 配置已成功写入！\n\n请在 iOS 系统弹窗中点击【允许】，随后前往【系统设置 -> VPN】开启 VPN 开关即可接管移动网络流量！"));
                    }
                });
            });
        }
        catch (Exception ex)
        {
            tcs.TrySetResult((false, $"调用 NetworkExtension 异常: {ex.Message}"));
        }

        return await tcs.Task;
    }
}
#endif
