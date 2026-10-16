using System;
using System.Threading.Tasks;

namespace Proxy.Client.Maui.Services;

public static class IosVpnHelper
{
    public static async Task<(bool Success, string Message)> SetupSystemVpnAsync(int localPort)
    {
#if IOS
        return await Platforms.iOS.VpnService.ConfigureVpnAsync(localPort);
#else
        await Task.CompletedTask;
        return (false, "当前非 iOS 平台，无需配置 iOS 系统 VPN");
#endif
    }
}
