using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Proxy.Common.Crypto;
using Proxy.Common.Models;
using Proxy.Common.Protocol;

namespace Proxy.Client.Core.Transport;

public enum TransportState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

public sealed class TunnelTransport : IAsyncDisposable
{
    private readonly ProxyClientConfig _config;
    private readonly ICryptoProvider _crypto;
    private readonly ConcurrentDictionary<ulong, Action<ProxyFrame>> _sessionSubscriptions = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();

    private ClientWebSocket? _webSocket;
    private ulong _nextSessionId = 1;
    private Task? _receiveLoopTask;
    private Task? _heartbeatLoopTask;
    private bool _disposed;
    private TransportState _state = TransportState.Disconnected;

    public event Action<TransportState, string?>? StatusChanged;
    public event Action<long>? LatencyUpdated; // 往返延迟 (ms)
    public event Action<long, long>? TrafficUpdated; // 上传, 下载字节数累加

    private long _totalUploadBytes;
    private long _totalDownloadBytes;
    private long _lastPingTimestamp;

    public TransportState State
    {
        get => _state;
        private set
        {
            if (_state != value)
            {
                _state = value;
                StatusChanged?.Invoke(_state, null);
            }
        }
    }

    public TunnelTransport(ProxyClientConfig config)
    {
        _config = config;
        _crypto = CryptoFactory.Create(_config.Algorithm, _config.Password);
    }

    public ulong NextSessionId()
    {
        return Interlocked.Increment(ref _nextSessionId);
    }

    public void SubscribeSession(ulong sessionId, Action<ProxyFrame> handler)
    {
        _sessionSubscriptions[sessionId] = handler;
    }

    public void UnsubscribeSession(ulong sessionId)
    {
        _sessionSubscriptions.TryRemove(sessionId, out _);
    }

    public async Task StartAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TunnelTransport));

        State = TransportState.Connecting;
        await ConnectWithRetryAsync();

        _receiveLoopTask = Task.Run(ReceiveLoopAsync);
        _heartbeatLoopTask = Task.Run(HeartbeatLoopAsync);
    }

    private async Task ConnectWithRetryAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _webSocket?.Dispose();
                _webSocket = new ClientWebSocket();

                string scheme = _config.UseTls ? "wss" : "ws";
                string cleanPath = _config.Path.StartsWith('/') ? _config.Path : "/" + _config.Path;

                // 若配置了 SNI 真实域名，URI Host 使用 SNI 确保 TLS 证书与 Host 标头匹配
                string targetDomain = !string.IsNullOrWhiteSpace(_config.SniHost) ? _config.SniHost.Trim() : _config.ServerHost.Trim();
                var uri = new Uri($"{scheme}://{targetDomain}:{_config.ServerPort}{cleanPath}?token={Uri.EscapeDataString(_config.Password)}");

                using var connectTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, connectTimeoutCts.Token);

                // 若配置了优选 IP/优选域名，物理 Socket 重定向到优选节点，TLS/HTTP 保持真实域名
                if (!string.IsNullOrWhiteSpace(_config.SniHost) && !string.Equals(_config.SniHost.Trim(), _config.ServerHost.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    var handler = new SocketsHttpHandler
                    {
                        ConnectCallback = async (context, ct) =>
                        {
                            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                            await socket.ConnectAsync(_config.ServerHost.Trim(), _config.ServerPort, ct);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                    };
                    var invoker = new HttpMessageInvoker(handler);
                    await _webSocket.ConnectAsync(uri, invoker, linkedCts.Token);
                }
                else
                {
                    _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                    await _webSocket.ConnectAsync(uri, linkedCts.Token);
                }

                State = TransportState.Connected;
                return;
            }
            catch (Exception ex)
            {
                State = TransportState.Reconnecting;
                StatusChanged?.Invoke(State, $"连接失败: {ex.Message}，正在重试...");
                await Task.Delay(3000, _cts.Token);
            }
        }
    }

    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using var messageStream = new MemoryStream();

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                {
                    State = TransportState.Reconnecting;
                    await ConnectWithRetryAsync();
                    continue;
                }

                messageStream.SetLength(0);
                ValueWebSocketReceiveResult result;

                do
                {
                    result = await _webSocket.ReceiveAsync(buffer.AsMemory(), _cts.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }
                    messageStream.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    State = TransportState.Reconnecting;
                    await ConnectWithRetryAsync();
                    continue;
                }

                if (result.MessageType == WebSocketMessageType.Binary && messageStream.Length > 0)
                {
                    byte[] encryptedData = messageStream.ToArray();
                    Interlocked.Add(ref _totalDownloadBytes, encryptedData.Length);
                    TrafficUpdated?.Invoke(_totalUploadBytes, _totalDownloadBytes);

                    HandleIncomingEncryptedMessage(encryptedData);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(State, $"接收异常: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void HandleIncomingEncryptedMessage(byte[] encryptedPackage)
    {
        try
        {
            byte[] decrypted = _crypto.Decrypt(encryptedPackage);
            if (ProxyFrame.TryDeserialize(decrypted, out var frame) && frame != null)
            {
                if (frame.Command == ProxyCommand.Pong)
                {
                    if (_lastPingTimestamp > 0)
                    {
                        long rtt = Stopwatch.GetElapsedTime(_lastPingTimestamp).Milliseconds;
                        LatencyUpdated?.Invoke(rtt);
                    }
                    return;
                }

                if (_sessionSubscriptions.TryGetValue(frame.SessionId, out var handler))
                {
                    handler(frame);
                }
            }
        }
        catch { }
    }

    private async Task HeartbeatLoopAsync()
    {
        // 45 秒发送一次心跳 Ping，对抗 Cloudflare 100 秒无活动超时断开
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(45), _cts.Token);

                if (State == TransportState.Connected)
                {
                    _lastPingTimestamp = Stopwatch.GetTimestamp();
                    await SendFrameAsync(ProxyFrame.CreatePing());
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    public async Task SendFrameAsync(ProxyFrame frame)
    {
        if (_disposed || _webSocket == null || _webSocket.State != WebSocketState.Open) return;

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

                Interlocked.Add(ref _totalUploadBytes, encrypted.Length);
                TrafficUpdated?.Invoke(_totalUploadBytes, _totalDownloadBytes);
            }
        }
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

        _sessionSubscriptions.Clear();

        try
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client Dispose", CancellationToken.None);
            }
        }
        catch { }

        _webSocket?.Dispose();
        _crypto.Dispose();
        _sendLock.Dispose();
        _cts.Dispose();
    }
}
