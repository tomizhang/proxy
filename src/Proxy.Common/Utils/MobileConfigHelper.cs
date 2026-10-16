using System;
using System.Text;

namespace Proxy.Common.Utils;

/// <summary>
/// iOS 描述文件 (.mobileconfig) 生成助手
/// 用于向 iOS 系统注入蜂窝移动网络 (流量) APN 代理与网络配置
/// </summary>
public static class MobileConfigHelper
{
    /// <summary>
    /// 生成包含蜂窝移动网络 (Cellular APN) 代理与全局网络指引的 iOS .mobileconfig 描述文件
    /// </summary>
    public static string GenerateCellularMobileConfigXml(int proxyPort)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">");
        sb.AppendLine("<plist version=\"1.0\">");
        sb.AppendLine("<dict>");
        sb.AppendLine("    <key>PayloadDisplayName</key>");
        sb.AppendLine("    <string>Cloudflare 代理 (蜂窝网络流量配置)</string>");
        sb.AppendLine("    <key>PayloadDescription</key>");
        sb.AppendLine($"    <string>引导 iOS 蜂窝移动网络流量通过本地代理 127.0.0.1:{proxyPort} 出海加速</string>");
        sb.AppendLine("    <key>PayloadIdentifier</key>");
        sb.AppendLine($"    <string>com.proxy.client.ios.cellular.{proxyPort}</string>");
        sb.AppendLine("    <key>PayloadOrganization</key>");
        sb.AppendLine("    <string>Cloudflare Proxy Client</string>");
        sb.AppendLine("    <key>PayloadRemovalDisallowed</key>");
        sb.AppendLine("    <false/>");
        sb.AppendLine("    <key>PayloadType</key>");
        sb.AppendLine("    <string>Configuration</string>");
        sb.AppendLine("    <key>PayloadUUID</key>");
        sb.AppendLine("    <string>4A2B81C3-7E34-4C3D-9A91-89DF62B413E0</string>");
        sb.AppendLine("    <key>PayloadVersion</key>");
        sb.AppendLine("    <integer>1</integer>");
        sb.AppendLine("    <key>PayloadContent</key>");
        sb.AppendLine("    <array>");
        sb.AppendLine("        <dict>");
        sb.AppendLine("            <key>PayloadType</key>");
        sb.AppendLine("            <string>com.apple.cellular</string>");
        sb.AppendLine("            <key>PayloadIdentifier</key>");
        sb.AppendLine("            <string>com.proxy.client.ios.cellular.apn</string>");
        sb.AppendLine("            <key>PayloadUUID</key>");
        sb.AppendLine("            <string>7B3C92D4-8F45-4D4E-AB02-90EF73C524F1</string>");
        sb.AppendLine("            <key>PayloadVersion</key>");
        sb.AppendLine("            <integer>1</integer>");
        sb.AppendLine("            <key>APNs</key>");
        sb.AppendLine("            <array>");
        sb.AppendLine("                <dict>");
        sb.AppendLine("                    <key>Name</key>");
        sb.AppendLine("                    <string>ProxyAPN</string>");
        sb.AppendLine("                    <key>ProxyServer</key>");
        sb.AppendLine("                    <string>127.0.0.1</string>");
        sb.AppendLine("                    <key>ProxyPort</key>");
        sb.AppendLine($"                    <integer>{proxyPort}</integer>");
        sb.AppendLine("                </dict>");
        sb.AppendLine("            </array>");
        sb.AppendLine("        </dict>");
        sb.AppendLine("    </array>");
        sb.AppendLine("</dict>");
        sb.AppendLine("</plist>");
        return sb.ToString();
    }
}
