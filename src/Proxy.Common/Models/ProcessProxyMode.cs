namespace Proxy.Common.Models;

/// <summary>
/// 应用程序分流过滤模式
/// </summary>
public enum ProcessProxyMode
{
    /// <summary>
    /// 仅直连选中的应用 (黑名单/排除模式)
    /// 名单中选中的程序直接使用本地网络直连，其余所有程序走代理
    /// </summary>
    BypassSelected = 0,

    /// <summary>
    /// 只为选中的应用进行代理 (白名单/仅代理模式)
    /// 仅名单中选中的程序走代理网络，其余所有程序全部直接使用本地网络直连
    /// </summary>
    ProxySelected = 1
}
