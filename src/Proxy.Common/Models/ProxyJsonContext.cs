using System.Text.Json.Serialization;

namespace Proxy.Common.Models;

/// <summary>
/// AOT 友好的 System.Text.Json 源生成器上下文，防止在 iOS / Android 移动端 Trimming 裁剪导致反序列化闪退
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(ProxyClientConfig))]
[JsonSerializable(typeof(ProcessProxyMode))]
[JsonSerializable(typeof(ProxyServerConfig))]
public partial class ProxyJsonContext : JsonSerializerContext
{
}
