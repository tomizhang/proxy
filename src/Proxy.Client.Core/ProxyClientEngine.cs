using System;
using System.Threading.Tasks;
using Proxy.Client.Core.Socks5;
using Proxy.Client.Core.Transport;
using Proxy.Client.Core.Sys;
using Proxy.Client.Core.Tun;
using Proxy.Common.Models;

namespace Proxy.Client.Core;

public sealed class ProxyClientEngine : IAsyncDisposable
{
    private readonly ProxyClientConfig _config;
    private TunnelTransport? _transport;
    private LocalSocks5Server? _socks5Server;
    private TunManager? _tunManager;
    private bool _isRunning;

    public event Action<TransportState, string?>? StatusChanged;
    public event Action<long>? LatencyUpdated;
    public event Action<long, long>? TrafficUpdated;
    public event Action<string>? LogMessage;

    public bool IsRunning => _isRunning;
    public int ActiveChannelCount => _transport?.ActiveChannelCount ?? 0;
    public int TotalChannelCount => _transport?.TotalChannelCount ?? 0;
    public long TotalUploadBytes => _transport?.TotalUploadBytes ?? 0;
    public long TotalDownloadBytes => _transport?.TotalDownloadBytes ?? 0;

    public ProxyClientEngine(ProxyClientConfig config)
    {
        _config = config;
    }

    public async Task StartAsync()
    {
        if (_isRunning) return;

        LogMessage?.Invoke($"正在连接代理服务器: {_config.ServerHost}:{_config.ServerPort} (并发连接池: {_config.ConcurrencyChannels} 条通道, 路径: {_config.Path}, 加密: {_config.Algorithm})...");

        // 1. 初始化传输层
        _transport = new TunnelTransport(_config);
        _transport.StatusChanged += (state, err) => StatusChanged?.Invoke(state, err);
        _transport.LatencyUpdated += ms => LatencyUpdated?.Invoke(ms);
        _transport.TrafficUpdated += (up, down) => TrafficUpdated?.Invoke(up, down);

        await _transport.StartAsync();

        // 2. 启动本地代理 (含 SOCKS5、HTTP 代理及直连分流)
        _socks5Server = new LocalSocks5Server(_config, _transport);
        _socks5Server.LogMessage += msg => LogMessage?.Invoke(msg);
        _socks5Server.Start();

        // 3. 自动设置 Windows 系统代理
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

        // 4. 按需启动 TUN 模式 (Windows)
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

    public async Task StopAsync()
    {
        if (!_isRunning) return;

        LogMessage?.Invoke("正在停止代理服务...");

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
