using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Proxy.Client.Core.Routing;
using Proxy.Client.Core.Transport;
using Proxy.Client.Core.Tun;
using Proxy.Common.Models;
using Proxy.Common.Protocol;

namespace Proxy.Client.Core.Socks5;

/// <summary>
/// 本地混合代理服务器 (自适应支持 SOCKS5 与 HTTP/HTTPS CONNECT，并支持国内网站直连分流)
/// </summary>
public sealed class LocalSocks5Server : IAsyncDisposable
{
    private readonly ProxyClientConfig _config;
    private readonly TunnelTransport _transport;
    private readonly BypassManager _bypassManager;
    private readonly ProcessBypassManager _processBypassManager;
    private readonly IPAddress? _physicalIp;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _listenTask;
    private bool _disposed;

    public event Action<string>? LogMessage;

    public LocalSocks5Server(ProxyClientConfig config, TunnelTransport transport)
    {
        _config = config;
        _transport = transport;
        _bypassManager = new BypassManager(config.BypassRules);
        _processBypassManager = new ProcessBypassManager(config.GetActiveProcessList());

        try
        {
            var gw = RouteManager.GetDefaultGateway();
            if (gw != null) _physicalIp = gw.Value.LocalIp;
        }
        catch { }
    }

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _config.LocalSocks5Port);
            _listener.ExclusiveAddressUse = false;
            _listener.Start(200);
            _listenTask = Task.Run(AcceptLoopAsync);
            LogMessage?.Invoke($"本地双模代理服务 (SOCKS5/HTTP) 已启动: 127.0.0.1:{_config.LocalSocks5Port}");
        }
        catch (SocketException ex)
        {
            throw new InvalidOperationException($"本地代理端口 {_config.LocalSocks5Port} 绑定失败: {ex.Message}\n💡 提示：该端口已被其他软件占用，请在界面设置中修改 SOCKS5 端口！", ex);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener != null)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                client.NoDelay = true;
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _ = Task.Run(() => HandleClientAsync(client));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"本地客户端接入异常: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using var tcpClient = client;
        using var stream = tcpClient.GetStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);

        ulong sessionId = 0;
        var incomingChannel = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        try
        {
            // 预读取首字节以区分是 SOCKS5 还是 HTTP 代理
            int read = await stream.ReadAsync(buffer.AsMemory(0, 1), _cts.Token);
            if (read <= 0) return;

            byte firstByte = buffer[0];

            string targetHost;
            ushort targetPort;
            AddressType protoAddrType = AddressType.Domain;
            ReadOnlyMemory<byte> initialData = ReadOnlyMemory<byte>.Empty;

            if (firstByte == 0x05)
            {
                // ==================== 1. SOCKS5 协议处理 ====================
                await stream.ReadExactlyAsync(buffer.AsMemory(1, 1), _cts.Token);
                int methodCount = buffer[1];
                await stream.ReadExactlyAsync(buffer.AsMemory(2, methodCount), _cts.Token);

                // 回复无需认证: [0x05, 0x00]
                await stream.WriteAsync(new byte[] { 0x05, 0x00 }, _cts.Token);

                // 读取 SOCKS5 请求头 [Ver(1), Cmd(1), Rsv(1), AType(1)]
                await stream.ReadExactlyAsync(buffer.AsMemory(0, 4), _cts.Token);
                if (buffer[0] != 0x05) return;

                if (buffer[1] == 0x03) // UDP ASSOCIATE
                {
                    await HandleUdpAssociateAsync(stream, buffer);
                    return;
                }

                if (buffer[1] != 0x01) // 仅支持 CONNECT 与 UDP ASSOCIATE
                {
                    await stream.WriteAsync(new byte[] { 0x05, 0x07, 0x00, 0x01, 0, 0, 0, 0, 0, 0 }, _cts.Token);
                    return;
                }

                byte addressType = buffer[3];
                switch (addressType)
                {
                    case 0x01: // IPv4
                        await stream.ReadExactlyAsync(buffer.AsMemory(0, 4), _cts.Token);
                        targetHost = new IPAddress(buffer.AsSpan(0, 4)).ToString();
                        protoAddrType = AddressType.IPv4;
                        break;

                    case 0x03: // 域名
                        await stream.ReadExactlyAsync(buffer.AsMemory(0, 1), _cts.Token);
                        int domainLen = buffer[0];
                        await stream.ReadExactlyAsync(buffer.AsMemory(0, domainLen), _cts.Token);
                        targetHost = Encoding.UTF8.GetString(buffer, 0, domainLen);
                        protoAddrType = AddressType.Domain;
                        break;

                    case 0x04: // IPv6
                        await stream.ReadExactlyAsync(buffer.AsMemory(0, 16), _cts.Token);
                        targetHost = new IPAddress(buffer.AsSpan(0, 16)).ToString();
                        protoAddrType = AddressType.IPv6;
                        break;

                    default:
                        return;
                }

                await stream.ReadExactlyAsync(buffer.AsMemory(0, 2), _cts.Token);
                targetPort = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2));

                // 回复成功建立
                await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0, 0 }, _cts.Token);
            }
            else
            {
                // ==================== 2. HTTP / HTTPS CONNECT 代理处理 ====================
                using var headerMs = new MemoryStream();
                headerMs.WriteByte(firstByte);

                byte[] lineBuf = new byte[1];
                int matchCount = 0;
                while (!_cts.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(lineBuf.AsMemory(0, 1), _cts.Token);
                    if (n <= 0) return;
                    headerMs.WriteByte(lineBuf[0]);

                    if ((matchCount == 0 || matchCount == 2) && lineBuf[0] == '\r') matchCount++;
                    else if ((matchCount == 1 || matchCount == 3) && lineBuf[0] == '\n')
                    {
                        matchCount++;
                        if (matchCount == 4) break; // 匹配到 \r\n\r\n
                    }
                    else matchCount = 0;

                    if (headerMs.Length > 8192) return;
                }

                string headerText = Encoding.ASCII.GetString(headerMs.ToArray());
                string firstLine = headerText.Split("\r\n")[0];
                var parts = firstLine.Split(' ');
                if (parts.Length < 2) return;

                string method = parts[0].ToUpperInvariant();
                string target = parts[1];

                if (method == "CONNECT")
                {
                    var hp = target.Split(':');
                    targetHost = hp[0];
                    targetPort = hp.Length > 1 && ushort.TryParse(hp[1], out var p) ? p : (ushort)443;

                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, _cts.Token);
                }
                else
                {
                    // 检查是否为 iOS 描述文件下载请求
                    if (method == "GET" && (target.Contains("mobileconfig") || target.EndsWith(".mobileconfig")))
                    {
                        string xml = Proxy.Common.Utils.MobileConfigHelper.GenerateCellularMobileConfigXml(_config.LocalSocks5Port);
                        byte[] xmlBytes = Encoding.UTF8.GetBytes(xml);
                        string httpResp = "HTTP/1.1 200 OK\r\n" +
                                          "Content-Type: application/x-apple-aspen-config\r\n" +
                                          $"Content-Disposition: attachment; filename=\"CloudflareProxy_{_config.LocalSocks5Port}.mobileconfig\"\r\n" +
                                          $"Content-Length: {xmlBytes.Length}\r\n" +
                                          "Connection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(httpResp), _cts.Token);
                        await stream.WriteAsync(xmlBytes, _cts.Token);
                        LogMessage?.Invoke($"[iOS 配置分发] 成功向客户端提供蜂窝 APN 代理描述文件 (.mobileconfig)");
                        return;
                    }

                    if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
                    {
                        targetHost = uri.Host;
                        targetPort = (ushort)(uri.Port > 0 ? uri.Port : 80);
                    }
                    else
                    {
                        var hp = target.Split(':');
                        targetHost = hp[0];
                        targetPort = hp.Length > 1 && ushort.TryParse(hp[1], out var p) ? p : (ushort)80;
                    }

                    initialData = headerMs.ToArray();
                }
            }

            int clientPort = (tcpClient.Client.RemoteEndPoint as IPEndPoint)?.Port ?? 0;

            // ==================== 3. 直连分流判断 ====================

            // 3.0 本地回环地址永远直连 (杜绝将 127.0.0.1 / localhost / ::1 错误发往远端隧道导致本地服务超时或 OAuth 回调失败)
            bool isLoopback = targetHost.Equals("127.0.0.1") ||
                              targetHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                              targetHost.Equals("::1") ||
                              (IPAddress.TryParse(targetHost, out var tip) && IPAddress.IsLoopback(tip));
            if (isLoopback)
            {
                await HandleDirectConnectAsync(stream, targetHost, targetPort, initialData);
                return;
            }

            // 3.1 域名与 IP 直连绕过 (跳过国内域名如 baidu.com, *.cn, 局域网 192.168.* 等，直接出网)
            if (_config.EnableBypass && _bypassManager.ShouldBypass(targetHost))
            {
                LogMessage?.Invoke($"[直连绕过] {targetHost}:{targetPort} (跳过代理)");
                await HandleDirectConnectAsync(stream, targetHost, targetPort, initialData);
                return;
            }

            // 3.2 应用程序名单分流判断 (支持“仅直连选中应用”或“只为选中应用代理”)
            string matchedProc = string.Empty;
            if (_config.EnableProcessBypass &&
                _processBypassManager.ShouldBypass(clientPort, targetHost, targetPort, _config.ProcessFilterMode, out matchedProc, out var bypassReason))
            {
                if (!string.IsNullOrEmpty(bypassReason))
                {
                    LogMessage?.Invoke(bypassReason);
                }
                await HandleDirectConnectAsync(stream, targetHost, targetPort, initialData);
                return;
            }

            if (_config.EnableProcessBypass && _config.ProcessFilterMode == ProcessProxyMode.ProxySelected && !string.IsNullOrEmpty(matchedProc))
            {
                LogMessage?.Invoke($"[应用代理] {matchedProc} -> {targetHost}:{targetPort} (命中选中代理名单 -> 隧道)");
            }
            else
            {
                LogMessage?.Invoke($"[隧道代理] {targetHost}:{targetPort} -> Cloudflare Tunnel");
            }

            // ==================== 4. 远端加密隧道转发 ====================
            sessionId = _transport.NextSessionId();

            _transport.SubscribeSession(sessionId, frame =>
            {
                if (frame.Command == ProxyCommand.DataTcp)
                {
                    incomingChannel.Writer.TryWrite(frame.Payload);
                }
                else if (frame.Command == ProxyCommand.CloseTcp)
                {
                    incomingChannel.Writer.TryComplete();
                }
            });

            await _transport.SendFrameAsync(ProxyFrame.CreateConnect(sessionId, targetHost, targetPort, protoAddrType));

            if (!initialData.IsEmpty)
            {
                await _transport.SendFrameAsync(ProxyFrame.CreateData(sessionId, initialData));
            }

            // 双向转发泵
            var pumpToTunnelTask = Task.Run(async () =>
            {
                byte[] localBuf = ArrayPool<byte>.Shared.Rent(64 * 1024);
                try
                {
                    while (!_cts.IsCancellationRequested)
                    {
                        int n = await stream.ReadAsync(localBuf, _cts.Token);
                        if (n <= 0) break;

                        var dataFrame = ProxyFrame.CreateData(sessionId, localBuf.AsMemory(0, n));
                        await _transport.SendFrameAsync(dataFrame);
                    }
                }
                catch { }
                finally
                {
                    ArrayPool<byte>.Shared.Return(localBuf);
                    await _transport.SendFrameAsync(ProxyFrame.CreateClose(sessionId));
                }
            });

            var pumpToLocalTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var mem in incomingChannel.Reader.ReadAllAsync(_cts.Token))
                    {
                        await stream.WriteAsync(mem, _cts.Token);
                    }
                }
                catch { }
            });

            await Task.WhenAny(pumpToTunnelTask, pumpToLocalTask);
        }
        catch { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            if (sessionId > 0)
            {
                _transport.UnsubscribeSession(sessionId);
            }
        }
    }

    private async Task HandleDirectConnectAsync(NetworkStream clientStream, string targetHost, ushort targetPort, ReadOnlyMemory<byte> initialData)
    {
        try
        {
            using var targetSocket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            bool isLoopback = targetHost.Equals("127.0.0.1") ||
                              targetHost.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                              (IPAddress.TryParse(targetHost, out var tip) && IPAddress.IsLoopback(tip));

            if (_physicalIp != null && !isLoopback)
            {
                try { targetSocket.Bind(new IPEndPoint(_physicalIp, 0)); } catch { }
            }

            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, connectCts.Token);

            await targetSocket.ConnectAsync(targetHost, targetPort, linkedCts.Token);
            using var targetStream = new NetworkStream(targetSocket, ownsSocket: false);

            if (!initialData.IsEmpty)
            {
                await targetStream.WriteAsync(initialData, _cts.Token);
            }

            var t1 = clientStream.CopyToAsync(targetStream, _cts.Token);
            var t2 = targetStream.CopyToAsync(clientStream, _cts.Token);
            await Task.WhenAny(t1, t2);
        }
        catch { }
    }

    private async Task HandleUdpAssociateAsync(NetworkStream stream, byte[] buffer)
    {
        try
        {
            byte atyp = buffer[3];
            switch (atyp)
            {
                case 0x01: // IPv4
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, 4 + 2), _cts.Token);
                    break;
                case 0x03: // 域名
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, 1), _cts.Token);
                    int dLen = buffer[0];
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, dLen + 2), _cts.Token);
                    break;
                case 0x04: // IPv6
                    await stream.ReadExactlyAsync(buffer.AsMemory(0, 16 + 2), _cts.Token);
                    break;
                default:
                    return;
            }

            using var udpRelay = new UdpClient();
            udpRelay.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udpRelay.Client.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var relayEp = (IPEndPoint)udpRelay.Client.LocalEndPoint!;
            ushort relayPort = (ushort)relayEp.Port;

            // 创建复用的上游 UDP Socket (全局共用单个 Socket，彻底杜绝短时间内新建大量 Socket 导致 10055 端口耗尽)
            using var upstreamUdp = new UdpClient();
            upstreamUdp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (_physicalIp != null)
            {
                try { upstreamUdp.Client.Bind(new IPEndPoint(_physicalIp, 0)); }
                catch { upstreamUdp.Client.Bind(new IPEndPoint(IPAddress.Any, 0)); }
            }
            else
            {
                upstreamUdp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            }

            // 回复成功绑定本地 UDP 中继端口
            byte[] response = new byte[]
            {
                0x05, 0x00, 0x00, 0x01,
                127, 0, 0, 1,
                (byte)(relayPort >> 8),
                (byte)(relayPort & 0xFF)
            };
            await stream.WriteAsync(response, _cts.Token);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            IPEndPoint? lastClientEp = null;

            // 1. 监听 TCP 连接：TCP 断开则 UDP 关联自动终止 (RFC 1928)
            var tcpMonitorTask = Task.Run(async () =>
            {
                byte[] probeBuf = new byte[1];
                try
                {
                    while (!linkedCts.Token.IsCancellationRequested)
                    {
                        int r = await stream.ReadAsync(probeBuf.AsMemory(0, 1), linkedCts.Token);
                        if (r <= 0) break;
                    }
                }
                catch { }
                finally
                {
                    linkedCts.Cancel();
                }
            });

            // 2. 本地上游接收泵：监听上游目标/DNS 回复并包装 SOCKS5 头回传给 tun2socks
            var upstreamReceiveTask = Task.Run(async () =>
            {
                while (!linkedCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var recv = await upstreamUdp.ReceiveAsync(linkedCts.Token);
                        var clientEp = lastClientEp;
                        if (clientEp == null) continue;

                        byte[] replyHeader = new byte[10];
                        replyHeader[0] = 0; replyHeader[1] = 0; replyHeader[2] = 0; replyHeader[3] = 0x01;
                        var ipBytes = recv.RemoteEndPoint.Address.GetAddressBytes();
                        if (ipBytes.Length == 4)
                        {
                            Buffer.BlockCopy(ipBytes, 0, replyHeader, 4, 4);
                            BinaryPrimitives.WriteUInt16BigEndian(replyHeader.AsSpan(8, 2), (ushort)recv.RemoteEndPoint.Port);

                            byte[] fullReply = new byte[replyHeader.Length + recv.Buffer.Length];
                            Buffer.BlockCopy(replyHeader, 0, fullReply, 0, replyHeader.Length);
                            Buffer.BlockCopy(recv.Buffer, 0, fullReply, replyHeader.Length, recv.Buffer.Length);

                            await udpRelay.SendAsync(fullReply, fullReply.Length, clientEp);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            });

            // 3. UDP 请求分发泵：读取 tun2socks 发来的数据，过滤局域网广播风暴后通过单 Socket 转发
            var udpPumpTask = Task.Run(async () =>
            {
                while (!linkedCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var udpResult = await udpRelay.ReceiveAsync(linkedCts.Token);
                        lastClientEp = udpResult.RemoteEndPoint;

                        var packet = udpResult.Buffer;
                        if (packet.Length < 10 || packet[2] != 0) continue; // 不支持分片

                        byte packetAtyp = packet[3];
                        string targetHost = "";
                        ushort targetPort = 0;
                        int headerLen = 0;

                        if (packetAtyp == 0x01) // IPv4
                        {
                            targetHost = new IPAddress(packet.AsSpan(4, 4)).ToString();
                            targetPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8, 2));
                            headerLen = 10;
                        }
                        else if (packetAtyp == 0x03) // 域名
                        {
                            int hostLen = packet[4];
                            targetHost = Encoding.UTF8.GetString(packet, 5, hostLen);
                            targetPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(5 + hostLen, 2));
                            headerLen = 5 + hostLen + 2;
                        }
                        else if (packetAtyp == 0x04) // IPv6
                        {
                            targetHost = new IPAddress(packet.AsSpan(4, 16)).ToString();
                            targetPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(20, 2));
                            headerLen = 22;
                        }
                        else continue;

                        // 关键过滤：拦截局域网广播、组播与 Windows 广播风暴 (NetBIOS 137-139, SSDP 1900, LLMNR 5355, mDNS 5353)
                        if (targetPort == 137 || targetPort == 138 || targetPort == 139 ||
                            targetPort == 1900 || targetPort == 5353 || targetPort == 5355)
                        {
                            continue;
                        }

                        IPAddress? targetIp;
                        if (!IPAddress.TryParse(targetHost, out targetIp))
                        {
                            var addrs = await Dns.GetHostAddressesAsync(targetHost);
                            targetIp = addrs.Length > 0 ? addrs[0] : null;
                        }

                        if (targetIp == null) continue;

                        var ipBytes = targetIp.GetAddressBytes();
                        if (ipBytes.Length == 4)
                        {
                            // 过滤 169.254.* (Link-Local), 224-239.* (组播), 255.255.255.255 (全网广播), *.255 (子网广播)
                            if (ipBytes[0] == 169 && ipBytes[1] == 254) continue;
                            if (ipBytes[0] >= 224 && ipBytes[0] <= 239) continue;
                            if (ipBytes[0] == 255 || ipBytes[3] == 255) continue;
                            if (ipBytes[0] == 127 || ipBytes[0] == 0) continue;
                        }

                        var data = packet.AsMemory(headerLen);
                        var targetEp = new IPEndPoint(targetIp, targetPort);

                        await upstreamUdp.SendAsync(data.ToArray(), data.Length, targetEp);
                    }
                    catch (OperationCanceledException) { break; }
                    catch { }
                }
            });

            await Task.WhenAny(tcpMonitorTask, upstreamReceiveTask, udpPumpTask);
        }
        catch { }
    }

    /// <summary>
    /// 即时动态更新程序分流名单、过滤模式与开关状态，无需重启代理服务
    /// </summary>
    public void UpdateProcessBypass(string bypassProcessList, bool? enabled = null, ProcessProxyMode? mode = null)
    {
        if (enabled.HasValue)
        {
            _config.EnableProcessBypass = enabled.Value;
        }
        if (mode.HasValue)
        {
            _config.ProcessFilterMode = mode.Value;
        }
        _config.SetActiveProcessList(bypassProcessList);
        _processBypassManager.ParseProcessList(bypassProcessList);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        if (_listenTask != null)
        {
            try { await _listenTask; } catch { }
        }

        _cts.Dispose();
    }
}
