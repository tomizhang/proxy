using System;
using System.Buffers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Proxy.Common.Protocol;

namespace Proxy.Server.Sessions;

/// <summary>
/// 管理单个目标 TCP 连接的生命周期与数据泵
/// 支持数据包预缓冲，消除 Connect 与首包并发竞态条件
/// </summary>
public sealed class TcpSession : IAsyncDisposable
{
    private readonly ulong _sessionId;
    private readonly string _targetHost;
    private readonly ushort _targetPort;
    private readonly Func<ProxyFrame, Task> _sendFrameAsync;
    private readonly ILogger _logger;
    private readonly Channel<byte[]> _outgoingChannel = Channel.CreateUnbounded<byte[]>();
    private readonly CancellationTokenSource _cts = new();

    private Socket? _targetSocket;
    private Task? _targetPumpTask;
    private bool _disposed;

    public ulong SessionId => _sessionId;

    public TcpSession(
        ulong sessionId,
        string targetHost,
        ushort targetPort,
        Func<ProxyFrame, Task> sendFrameAsync,
        ILogger logger)
    {
        _sessionId = sessionId;
        _targetHost = targetHost;
        _targetPort = targetPort;
        _sendFrameAsync = sendFrameAsync;
        _logger = logger;
    }

    public void Start()
    {
        _targetPumpTask = Task.Run(TargetSessionLifecycleAsync);
    }

    public void EnqueueData(ReadOnlyMemory<byte> data)
    {
        if (_disposed) return;
        _outgoingChannel.Writer.TryWrite(data.ToArray());
    }

    private async Task TargetSessionLifecycleAsync()
    {
        try
        {
            _targetSocket = new Socket(SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
                ReceiveBufferSize = 512 * 1024,
                SendBufferSize = 512 * 1024
            };

            using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, connectCts.Token);

            await _targetSocket.ConnectAsync(_targetHost, _targetPort, linkedCts.Token);
            _logger.LogInformation("Session {SessionId} 成功建立到目标 {Host}:{Port}", _sessionId, _targetHost, _targetPort);

            // 启动读泵和写泵
            var readTask = Task.Run(ReadFromTargetLoopAsync);
            var writeTask = Task.Run(WriteToTargetLoopAsync);

            await Task.WhenAny(readTask, writeTask);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Session {SessionId} 连接或通信异常: {Message}", _sessionId, ex.Message);
        }
        finally
        {
            try
            {
                await _sendFrameAsync(ProxyFrame.CreateClose(_sessionId));
            }
            catch { }

            await DisposeAsync();
        }
    }

    private async Task ReadFromTargetLoopAsync()
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!_cts.IsCancellationRequested && _targetSocket != null && _targetSocket.Connected)
            {
                int bytesRead = await _targetSocket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, _cts.Token);
                if (bytesRead <= 0) break;

                var frame = ProxyFrame.CreateData(_sessionId, buffer.AsMemory(0, bytesRead).ToArray());
                await _sendFrameAsync(frame);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task WriteToTargetLoopAsync()
    {
        try
        {
            await foreach (var data in _outgoingChannel.Reader.ReadAllAsync(_cts.Token))
            {
                if (_targetSocket == null || !_targetSocket.Connected) break;
                await _targetSocket.SendAsync(data.AsMemory(), SocketFlags.None, _cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { }
        _outgoingChannel.Writer.TryComplete();

        try
        {
            if (_targetSocket != null && _targetSocket.Connected)
            {
                _targetSocket.Shutdown(SocketShutdown.Both);
            }
        }
        catch { }

        _targetSocket?.Dispose();
        _cts.Dispose();

        if (_targetPumpTask != null)
        {
            try { await _targetPumpTask; } catch { }
        }
    }
}
