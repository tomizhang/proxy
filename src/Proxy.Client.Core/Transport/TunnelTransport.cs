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

/// <summary>
/// 高性能多路复用连接池传输层 (Multi-Channel Connection Pool)
/// 支持针对单 IP 开启多条并发物理 WebSocket 隧道，或跨多 IP 负载均衡
/// 成倍突破单 TCP 拥塞窗口与 Cloudflare 单连接限速，消除队头阻塞并大幅提升并发小请求及大文件传输吞吐
/// </summary>
public sealed class TunnelTransport : IAsyncDisposable
{
    private readonly ProxyClientConfig _config;
    private readonly ICryptoProvider _crypto;
    private readonly ConcurrentDictionary<ulong, Action<ProxyFrame>> _sessionSubscriptions = new();
    private readonly ConcurrentDictionary<ulong, int> _sessionChannelMap = new();
    private readonly CancellationTokenSource _cts = new();

    private TunnelChannel[] _channels = Array.Empty<TunnelChannel>();
    private ulong _nextSessionId = 1;
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

    /// <summary>
    /// 当前活跃连接数
    /// </summary>
    public int ActiveChannelCount
    {
        get
        {
            int count = 0;
            foreach (var ch in _channels)
            {
                if (ch.IsConnected) count++;
            }
            return count;
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
        _sessionChannelMap.TryRemove(sessionId, out _);
    }

    public async Task StartAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TunnelTransport));

        State = TransportState.Connecting;

        int channelCount = Math.Clamp(_config.ConcurrencyChannels, 1, 16);
        string[] hosts = ParseHosts(_config.ServerHost);

        _channels = new TunnelChannel[channelCount];
        var connectTasks = new Task[channelCount];

        for (int i = 0; i < channelCount; i++)
        {
            string host = hosts[i % hosts.Length];
            var channel = new TunnelChannel(i, this, host, _config.ServerPort, _config, _cts.Token);
            _channels[i] = channel;
            connectTasks[i] = channel.StartAsync();
        }

        // 等待至少一个通道成功建立连接即可开始提供服务，其余通道后台继续重试
        var firstSuccess = await Task.WhenAny(connectTasks);
        try
        {
            await firstSuccess;
            State = TransportState.Connected;
        }
        catch
        {
            // 若第一个返回的任务抛出异常，检查是否有其他通道已连上
            if (ActiveChannelCount > 0)
            {
                State = TransportState.Connected;
            }
        }

        _heartbeatLoopTask = Task.Run(HeartbeatLoopAsync);
    }

    private static string[] ParseHosts(string rawHost)
    {
        if (string.IsNullOrWhiteSpace(rawHost)) return new[] { "127.0.0.1" };
        var parts = rawHost.Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts : new[] { rawHost.Trim() };
    }

    private TunnelChannel GetChannelForSession(ulong sessionId)
    {
        var channels = _channels;
        if (channels.Length == 0) throw new InvalidOperationException("连接池未初始化");

        if (channels.Length == 1) return channels[0];

        // 1. 若会话已绑定特定通道，优先选用该通道以保持单会话内完全保序
        if (_sessionChannelMap.TryGetValue(sessionId, out int boundIndex))
        {
            var boundChannel = channels[boundIndex % channels.Length];
            if (boundChannel.IsConnected) return boundChannel;
        }

        // 2. 新会话采用 SessionId 散列哈希分发通道，实现真正多路并行调度
        int targetIdx = (int)(sessionId % (ulong)channels.Length);
        var targetChannel = channels[targetIdx];
        if (targetChannel.IsConnected)
        {
            _sessionChannelMap[sessionId] = targetIdx;
            return targetChannel;
        }

        // 3. 容灾兜底：若目标通道正在重连，动态故障转移至当前任意可用的健康通道
        for (int i = 0; i < channels.Length; i++)
        {
            if (channels[i].IsConnected)
            {
                _sessionChannelMap[sessionId] = i;
                return channels[i];
            }
        }

        // 4. 若所有通道暂时均未 Open，返回目标通道等待其就绪
        return targetChannel;
    }

    public async Task SendFrameAsync(ProxyFrame frame)
    {
        if (_disposed) return;

        var channel = GetChannelForSession(frame.SessionId);
        byte[] serialized = frame.Serialize();
        byte[] encrypted = _crypto.Encrypt(serialized);

        await channel.SendAsync(encrypted);
    }

    internal void OnTraffic(int uploadBytes, int downloadBytes)
    {
        if (uploadBytes > 0) Interlocked.Add(ref _totalUploadBytes, uploadBytes);
        if (downloadBytes > 0) Interlocked.Add(ref _totalDownloadBytes, downloadBytes);
        TrafficUpdated?.Invoke(_totalUploadBytes, _totalDownloadBytes);
    }

    internal void OnFrameReceived(ProxyFrame frame)
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

    internal void OnChannelStateChanged()
    {
        int active = ActiveChannelCount;
        if (active > 0)
        {
            State = TransportState.Connected;
        }
        else
        {
            State = TransportState.Reconnecting;
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        // 45 秒在所有活跃通道发送心跳 Ping，对抗 Cloudflare 100 秒无活动超时断开
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(45), _cts.Token);

                if (State == TransportState.Connected)
                {
                    _lastPingTimestamp = Stopwatch.GetTimestamp();
                    var pingFrame = ProxyFrame.CreatePing();
                    byte[] serialized = pingFrame.Serialize();
                    byte[] encrypted = _crypto.Encrypt(serialized);

                    foreach (var channel in _channels)
                    {
                        if (channel.IsConnected)
                        {
                            _ = channel.SendAsync(encrypted);
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }

        _sessionSubscriptions.Clear();
        _sessionChannelMap.Clear();

        foreach (var ch in _channels)
        {
            await ch.DisposeAsync();
        }

        _crypto.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// 连接池中的单一物理 WebSocket 通道
    /// </summary>
    private sealed class TunnelChannel : IAsyncDisposable
    {
        private readonly int _channelIndex;
        private readonly TunnelTransport _parent;
        private readonly string _targetHost;
        private readonly int _targetPort;
        private readonly ProxyClientConfig _config;
        private readonly CancellationToken _parentToken;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly CancellationTokenSource _channelCts = new();

        private ClientWebSocket? _webSocket;
        private Task? _receiveLoopTask;
        private bool _disposed;

        public bool IsConnected => _webSocket?.State == WebSocketState.Open;

        public TunnelChannel(
            int channelIndex,
            TunnelTransport parent,
            string targetHost,
            int targetPort,
            ProxyClientConfig config,
            CancellationToken parentToken)
        {
            _channelIndex = channelIndex;
            _parent = parent;
            _targetHost = targetHost;
            _targetPort = targetPort;
            _config = config;
            _parentToken = parentToken;
        }

        public async Task StartAsync()
        {
            await ConnectWithRetryAsync();
            _receiveLoopTask = Task.Run(ReceiveLoopAsync);
        }

        private async Task ConnectWithRetryAsync()
        {
            while (!_parentToken.IsCancellationRequested && !_channelCts.IsCancellationRequested)
            {
                try
                {
                    _webSocket?.Dispose();
                    _webSocket = new ClientWebSocket();
                    _webSocket.Options.SetBuffer(64 * 1024, 64 * 1024);
                    _webSocket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);

                    string scheme = _config.UseTls ? "wss" : "ws";
                    string cleanPath = _config.Path.StartsWith('/') ? _config.Path : "/" + _config.Path;

                    string targetDomain = !string.IsNullOrWhiteSpace(_config.SniHost) ? _config.SniHost.Trim() : _targetHost.Trim();
                    var uri = new Uri($"{scheme}://{targetDomain}:{_targetPort}{cleanPath}?token={Uri.EscapeDataString(_config.Password)}");

                    using var connectTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_parentToken, connectTimeoutCts.Token);

                    // 若配置了优选 IP/优选域名，物理 Socket 直连优选 IP，TLS 握手保持 SNI 域名
                    if (!string.IsNullOrWhiteSpace(_config.SniHost) && !string.Equals(_config.SniHost.Trim(), _targetHost.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        var handler = new SocketsHttpHandler
                        {
                            ConnectCallback = async (context, ct) =>
                            {
                                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp)
                                {
                                    NoDelay = true,
                                    ReceiveBufferSize = 256 * 1024,
                                    SendBufferSize = 256 * 1024
                                };
                                await socket.ConnectAsync(_targetHost.Trim(), _targetPort, ct);
                                return new NetworkStream(socket, ownsSocket: true);
                            }
                        };
                        var invoker = new HttpMessageInvoker(handler);
                        await _webSocket.ConnectAsync(uri, invoker, linkedCts.Token);
                    }
                    else
                    {
                        await _webSocket.ConnectAsync(uri, linkedCts.Token);
                    }

                    _parent.OnChannelStateChanged();
                    return;
                }
                catch
                {
                    _parent.OnChannelStateChanged();
                    await Task.Delay(3000, _parentToken);
                }
            }
        }

        private async Task ReceiveLoopAsync()
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            using var messageStream = new MemoryStream();

            try
            {
                while (!_parentToken.IsCancellationRequested && !_channelCts.IsCancellationRequested)
                {
                    if (_webSocket == null || _webSocket.State != WebSocketState.Open)
                    {
                        _parent.OnChannelStateChanged();
                        await ConnectWithRetryAsync();
                        continue;
                    }

                    messageStream.SetLength(0);
                    ValueWebSocketReceiveResult result;

                    do
                    {
                        result = await _webSocket.ReceiveAsync(buffer.AsMemory(), _parentToken);
                        if (result.MessageType == WebSocketMessageType.Close) break;
                        messageStream.Write(buffer, 0, result.Count);
                    }
                    while (!result.EndOfMessage);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _parent.OnChannelStateChanged();
                        await ConnectWithRetryAsync();
                        continue;
                    }

                    if (result.MessageType == WebSocketMessageType.Binary && messageStream.Length > 0)
                    {
                        byte[] encryptedData = messageStream.ToArray();
                        _parent.OnTraffic(0, encryptedData.Length);

                        try
                        {
                            byte[] decrypted = _parent._crypto.Decrypt(encryptedData);
                            if (ProxyFrame.TryDeserialize(decrypted, out var frame) && frame != null)
                            {
                                _parent.OnFrameReceived(frame);
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                _parent.OnChannelStateChanged();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        public async Task SendAsync(byte[] encryptedData)
        {
            if (_disposed || _webSocket == null || _webSocket.State != WebSocketState.Open) return;

            await _sendLock.WaitAsync(_parentToken);
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                {
                    await _webSocket.SendAsync(
                        encryptedData.AsMemory(),
                        WebSocketMessageType.Binary,
                        endOfMessage: true,
                        _parentToken);

                    _parent.OnTraffic(encryptedData.Length, 0);
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

            try { _channelCts.Cancel(); } catch { }

            try
            {
                if (_webSocket?.State == WebSocketState.Open)
                {
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Channel Dispose", CancellationToken.None);
                }
            }
            catch { }

            _webSocket?.Dispose();
            _sendLock.Dispose();
            _channelCts.Dispose();
        }
    }
}
