using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Proxy.Client.Core.Outbound;
using Proxy.Client.Core.Socks5;
using Proxy.Client.Core.Sys;
using Proxy.Client.Core.Transport;
using Proxy.Client.Core.Tun;
using Proxy.Common.Models;

namespace Proxy.Client.Core;

public sealed class ProxyClientEngine : IAsyncDisposable
{
    private readonly ProxyClientConfig _config;
    private TunnelTransport? _transport;
    private SingBoxProcessManager? _singBox;
    private LocalSocks5Server? _socks5Server;
    private TunManager? _tunManager;
    private CancellationTokenSource? _ssPingCts;
    private bool _isRunning;

    public event Action<TransportState, string?>? StatusChanged;
    public event Action<long>? LatencyUpdated;
    public event Action<long, long>? TrafficUpdated;
    public event Action<string>? LogMessage;

    public bool IsRunning => _isRunning;
    public int ActiveChannelCount => _config.ProtocolType == ProxyProtocolType.Shadowsocks
        ? (_isRunning ? 1 : 0)
        : (_transport?.ActiveChannelCount ?? 0);

    public int TotalChannelCount => _config.ProtocolType == ProxyProtocolType.Shadowsocks
        ? 1
        : (_transport?.TotalChannelCount ?? 0);

    public long TotalUploadBytes => _config.ProtocolType == ProxyProtocolType.Shadowsocks
        ? (_socks5Server?.TotalUploadBytes ?? 0)
        : (_transport?.TotalUploadBytes ?? 0);

    public long TotalDownloadBytes => _config.ProtocolType == ProxyProtocolType.Shadowsocks
        ? (_socks5Server?.TotalDownloadBytes ?? 0)
        : (_transport?.TotalDownloadBytes ?? 0);

    public ProxyClientEngine(ProxyClientConfig config)
    {
        _config = config;
    }

    public async Task StartAsync()
    {
        if (_isRunning) return;

        // 1. 初始化出站通道 (支持自研 Cloudflare WSS 隧道与标准 Shadowsocks 协议)
        if (_config.ProtocolType == ProxyProtocolType.Shadowsocks)
        {
            string nodeDesc = !string.IsNullOrEmpty(_config.NodeName) ? $"【{_config.NodeName}】" : "";
            LogMessage?.Invoke($"正在连接 Shadowsocks 节点{nodeDesc}: {_config.ServerHost}:{_config.ServerPort} (协议: SS-2022 / 算法: {_config.Algorithm})...");

            _singBox = new SingBoxProcessManager();
            _singBox.LogMessage += msg => LogMessage?.Invoke(msg);
            _singBox.Start(_config);

            _socks5Server = new LocalSocks5Server(_config, null, _singBox.InternalPort);
            _socks5Server.LogMessage += msg => LogMessage?.Invoke(msg);
            _socks5Server.TrafficUpdated += (up, down) => TrafficUpdated?.Invoke(up, down);
            _socks5Server.Start();

            StatusChanged?.Invoke(TransportState.Connected, null);

            // 启动定时延迟测速
            _ssPingCts = new CancellationTokenSource();
            _ = Task.Run(() => ShadowsocksPingLoopAsync(_ssPingCts.Token));
        }
        else
        {
            LogMessage?.Invoke($"正在连接代理服务器: {_config.ServerHost}:{_config.ServerPort} (并发连接池: {_config.ConcurrencyChannels} 条通道, 路径: {_config.Path}, 加密: {_config.Algorithm})...");

            _transport = new TunnelTransport(_config);
            _transport.StatusChanged += (state, err) => StatusChanged?.Invoke(state, err);
            _transport.LatencyUpdated += ms => LatencyUpdated?.Invoke(ms);
            _transport.TrafficUpdated += (up, down) => TrafficUpdated?.Invoke(up, down);

            await _transport.StartAsync();

            _socks5Server = new LocalSocks5Server(_config, _transport);
            _socks5Server.LogMessage += msg => LogMessage?.Invoke(msg);
            _socks5Server.Start();
        }

        // 2. 自动设置 Windows 系统代理
        if (_config.EnableSystemProxy && OperatingSystem.IsWindows())
        {
            try
            {
                SystemProxyManager.SetSystemProxy($"127.0.0.1:{_config.LocalSocks5Port}", _config.BypassRules);
                LogMessage?.Invoke($"已自动启用 Windows 系统代理 -> 127.0.0.1:{_config.LocalSocks5Port}");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"设置系统代理失败: {ex.Message}");
            }
        }

        // 3. 按需启动 TUN 模式 (Windows)
        if (_config.EnableTun && OperatingSystem.IsWindows())
        {
            try
            {
                _tunManager = new TunManager();
                _tunManager.LogMessage += msg => LogMessage?.Invoke(msg);
                _tunManager.Start(_config.ServerHost, _config.LocalSocks5Port, _config.TunIp, _config.TunMask, _config.TunGateway);
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"TUN 模式启动失败: {ex.Message}");
            }
        }

        _isRunning = true;
        LogMessage?.Invoke("代理客户端引擎运行就绪。");
    }

    private async Task ShadowsocksPingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                long latency = await MeasureShadowsocksLatencyAsync(ct);
                if (latency > 0)
                {
                    LatencyUpdated?.Invoke(latency);
                }
            }
            catch { }

            try
            {
                await Task.Delay(10000, ct);
            }
            catch { break; }
        }
    }

    /// <summary>
    /// 测量 Shadowsocks 节点端到端或 TCP Ping 延迟
    /// </summary>
    public async Task<long> MeasureShadowsocksLatencyAsync(CancellationToken ct = default)
    {
        if (_singBox == null || !_singBox.IsRunning)
        {
            return await MeasureTcpPingAsync(_config.ServerHost, _config.ServerPort, ct);
        }

        try
        {
            var sw = Stopwatch.StartNew();
            using var tcp = new TcpClient { NoDelay = true };
            using var lcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lcts.CancelAfter(3500);

            await tcp.ConnectAsync("127.0.0.1", _singBox.InternalPort, lcts.Token);
            using var s = tcp.GetStream();

            // SOCKS5 握手
            await s.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, lcts.Token);
            byte[] auth = new byte[2];
            await s.ReadExactlyAsync(auth.AsMemory(0, 2), lcts.Token);
            if (auth[1] != 0) return -1;

            // 建立测试连接到 www.google.com:80
            byte[] req = new byte[] { 0x05, 0x01, 0x00, 0x03, 14, 119, 119, 119, 46, 103, 111, 111, 103, 108, 101, 46, 99, 111, 109, 0, 80 };
            await s.WriteAsync(req, lcts.Token);
            byte[] resp = new byte[10];
            await s.ReadExactlyAsync(resp.AsMemory(0, 10), lcts.Token);
            sw.Stop();

            if (resp[1] == 0)
            {
                return sw.ElapsedMilliseconds;
            }
        }
        catch { }

        return -1;
    }

    public static async Task<long> MeasureTcpPingAsync(string host, int port, CancellationToken ct = default)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var tcp = new TcpClient { NoDelay = true };
            using var lcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lcts.CancelAfter(3500);

            await tcp.ConnectAsync(host, port, lcts.Token);
            sw.Stop();
            return sw.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// 即时动态更新程序名单分流设置与模式，正在运行的代理会立即无缝采用新配置
    /// </summary>
    public void UpdateProcessBypass(string bypassProcessList, bool? enabled = null, ProcessProxyMode? mode = null)
    {
        if (mode.HasValue)
        {
            _config.ProcessFilterMode = mode.Value;
        }
        _config.SetActiveProcessList(bypassProcessList);
        if (enabled.HasValue)
        {
            _config.EnableProcessBypass = enabled.Value;
        }
        _socks5Server?.UpdateProcessBypass(bypassProcessList, enabled, mode);
    }

    /// <summary>
    /// 即时动态更新程序名单分流模式
    /// </summary>
    public void UpdateProcessBypass(string bypassProcessList, ProcessProxyMode mode)
    {
        UpdateProcessBypass(bypassProcessList, null, mode);
    }

    /// <summary>
    /// 即时动态更新跳过规则与开关状态，正在运行的代理会立即无缝采用新配置
    /// </summary>
    public void UpdateBypassRules(string bypassRules, bool? enabled = null)
    {
        _config.BypassRules = bypassRules;
        if (enabled.HasValue)
        {
            _config.EnableBypass = enabled.Value;
        }
        _socks5Server?.UpdateBypassRules(bypassRules, enabled);
    }

    public async Task StopAsync()
    {
        if (!_isRunning) return;

        LogMessage?.Invoke("正在停止代理服务...");

        if (_ssPingCts != null)
        {
            try { _ssPingCts.Cancel(); _ssPingCts.Dispose(); } catch { }
            _ssPingCts = null;
        }

        // 清除系统代理
        if (_config.EnableSystemProxy && OperatingSystem.IsWindows())
        {
            try
            {
                SystemProxyManager.ClearSystemProxy();
                LogMessage?.Invoke("已自动清除并还原 Windows 系统代理。");
            }
            catch { }
        }

        if (_tunManager != null && OperatingSystem.IsWindows())
        {
            _tunManager.Stop();
            _tunManager.Dispose();
            _tunManager = null;
        }

        if (_socks5Server != null)
        {
            await _socks5Server.DisposeAsync();
            _socks5Server = null;
        }

        if (_singBox != null)
        {
            _singBox.Stop();
            _singBox.Dispose();
            _singBox = null;
        }

        if (_transport != null)
        {
            await _transport.DisposeAsync();
            _transport = null;
        }

        _isRunning = false;
        StatusChanged?.Invoke(TransportState.Disconnected, null);
        LogMessage?.Invoke("代理服务已安全停止。");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
