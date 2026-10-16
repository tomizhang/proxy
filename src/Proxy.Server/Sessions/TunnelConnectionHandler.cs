using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Proxy.Common.Crypto;
using Proxy.Common.Protocol;

namespace Proxy.Server.Sessions;

/// <summary>
/// 处理单条 WebSocket 隧道连接的高可靠性生命周期
/// </summary>
public sealed class TunnelConnectionHandler : IAsyncDisposable
{
    private readonly WebSocket _webSocket;
    private readonly ICryptoProvider _crypto;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ulong, TcpSession> _sessions = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public TunnelConnectionHandler(WebSocket webSocket, ICryptoProvider crypto, ILogger logger)
    {
        _webSocket = webSocket;
        _crypto = crypto;
        _logger = logger;
    }

    public async Task ProcessAsync()
    {
        byte[] receiveBuffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var messageStream = new MemoryStream();

        try
        {
            while (!_cts.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
            {
                messageStream.SetLength(0);
                ValueWebSocketReceiveResult result;

                do
                {
                    result = await _webSocket.ReceiveAsync(receiveBuffer.AsMemory(), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                        return;
                    }

                    messageStream.Write(receiveBuffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Binary && messageStream.Length > 0)
                {
                    HandleIncomingMessage(messageStream.ToArray());
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            _logger.LogDebug(ex, "客户端 WebSocket 异常中断");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "隧道连接处理异常");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(receiveBuffer);
            await DisposeAsync();
        }
    }

    private void HandleIncomingMessage(byte[] encryptedPackage)
    {
        try
        {
            // 1. 解密
            byte[] decrypted;
            try
            {
                decrypted = _crypto.Decrypt(encryptedPackage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("收到非法密文包或验签失败: {Message}", ex.Message);
                return;
            }

            // 2. 解析帧
            if (!ProxyFrame.TryDeserialize(decrypted, out var frame) || frame == null)
            {
                _logger.LogWarning("无法解析的协议帧");
                return;
            }

            // 3. 处理命令
            DispatchFrame(frame);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "解密或分发消息异常");
        }
    }

    private void DispatchFrame(ProxyFrame frame)
    {
        try
        {
            switch (frame.Command)
            {
                case ProxyCommand.Ping:
                    _ = Task.Run(async () => await SendFrameAsync(ProxyFrame.CreatePong()));
                    break;

                case ProxyCommand.Pong:
                    break;

                case ProxyCommand.ConnectTcp:
                    if (!string.IsNullOrWhiteSpace(frame.TargetHost) && frame.TargetPort > 0)
                    {
                        var session = new TcpSession(
                            frame.SessionId,
                            frame.TargetHost,
                            frame.TargetPort,
                            SendFrameAsync,
                            _logger);

                        if (_sessions.TryAdd(frame.SessionId, session))
                        {
                            session.Start();
                        }
                        else
                        {
                            _ = session.DisposeAsync();
                        }
                    }
                    break;

                case ProxyCommand.DataTcp:
                    if (_sessions.TryGetValue(frame.SessionId, out var existingSession))
                    {
                        existingSession.EnqueueData(frame.Payload);
                    }
                    break;

                case ProxyCommand.CloseTcp:
                    if (_sessions.TryRemove(frame.SessionId, out var closingSession))
                    {
                        _ = closingSession.DisposeAsync();
                    }
                    break;

                case ProxyCommand.UdpPacket:
                    _ = Task.Run(async () => await HandleUdpPacketAsync(frame));
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "分发帧命令 {Command} 异常 (Session: {SessionId})", frame.Command, frame.SessionId);
        }
    }

    private async Task HandleUdpPacketAsync(ProxyFrame frame)
    {
        if (string.IsNullOrWhiteSpace(frame.TargetHost) || frame.TargetPort == 0 || frame.Payload.IsEmpty)
        {
            return;
        }

        try
        {
            using var udpClient = new UdpClient();
            var targetEp = new IPEndPoint(IPAddress.Parse(frame.TargetHost), frame.TargetPort);
            await udpClient.SendAsync(frame.Payload.ToArray(), frame.Payload.Length, targetEp);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var receiveTask = udpClient.ReceiveAsync(timeoutCts.Token);
            var result = await receiveTask;

            var replyFrame = ProxyFrame.CreateUdp(
                frame.SessionId,
                result.RemoteEndPoint.Address.ToString(),
                (ushort)result.RemoteEndPoint.Port,
                AddressType.IPv4,
                result.Buffer);

            await SendFrameAsync(replyFrame);
        }
        catch { }
    }

    public async Task SendFrameAsync(ProxyFrame frame)
    {
        if (_disposed || _webSocket.State != WebSocketState.Open) return;

        byte[] serialized = frame.Serialize();
        byte[] encrypted = _crypto.Encrypt(serialized);

        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.SendAsync(
                    encrypted.AsMemory(),
                    WebSocketMessageType.Binary,
                    endOfMessage: true,
                    _cts.Token);
            }
        }
        catch { }
        finally
        {
            _sendLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }

        foreach (var session in _sessions.Values)
        {
            try { await session.DisposeAsync(); } catch { }
        }
        _sessions.Clear();

        try
        {
            if (_webSocket.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Tunnel Closed", CancellationToken.None);
            }
        }
        catch { }

        _sendLock.Dispose();
        _cts.Dispose();
    }
}
