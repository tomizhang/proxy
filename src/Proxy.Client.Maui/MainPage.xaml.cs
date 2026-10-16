using System;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using Proxy.Client.Core;
using Proxy.Client.Core.Transport;
using Proxy.Common.Crypto;
using Proxy.Common.Models;

namespace Proxy.Client.Maui;

public partial class MainPage : ContentPage
{
    private const string ConfigStorageKey = "cf_proxy_config_v1";

    private ProxyClientConfig _config;
    private ProxyClientEngine? _engine;
    private IDispatcherTimer? _speedTimer;

    private long _lastUploadBytes;
    private long _lastDownloadBytes;
    private long _currentTotalUpload;
    private long _currentTotalDownload;

    public MainPage()
    {
        InitializeComponent();

        // 填充加密算法选项
        foreach (var algo in CryptoFactory.SupportedAlgorithms)
        {
            PickerAlgo.Items.Add(algo);
        }

        _config = LoadConfig();
        ApplyConfigToUi();

        // 开启 1 秒测速定时器
        _speedTimer = Dispatcher.CreateTimer();
        _speedTimer.Interval = TimeSpan.FromSeconds(1);
        _speedTimer.Tick += OnSpeedTimerTick;
        _speedTimer.Start();
    }

    private ProxyClientConfig LoadConfig()
    {
        string? json = Preferences.Default.Get<string?>(ConfigStorageKey, null);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var cfg = JsonSerializer.Deserialize<ProxyClientConfig>(json);
                if (cfg != null) return cfg;
            }
            catch { }
        }

        // 默认初始配置
        return new ProxyClientConfig
        {
            ServerHost = "127.0.0.1",
            ServerPort = 5000,
            Path = "/tunnel",
            Password = "DefaultSecurePassword123!",
            Algorithm = "aes-256-gcm",
            LocalSocks5Port = 20808,
            EnableBypass = true,
            EnableSystemProxy = false, // iOS 不使用 Windows 注册表系统代理
            EnableTun = false          // iOS 通过本地 SOCKS5 或 Wi-Fi 代理接入
        };
    }

    private void ApplyConfigToUi()
    {
        TxtHost.Text = _config.ServerHost;
        TxtPort.Text = _config.ServerPort.ToString();
        TxtPath.Text = _config.Path;
        TxtSni.Text = _config.SniHost ?? "";
        SwUseTls.IsToggled = _config.UseTls;
        TxtPassword.Text = _config.Password;

        int algoIndex = PickerAlgo.Items.IndexOf(_config.Algorithm);
        PickerAlgo.SelectedIndex = algoIndex >= 0 ? algoIndex : 0;

        TxtSocks5Port.Text = _config.LocalSocks5Port.ToString();
        SwEnableBypass.IsToggled = _config.EnableBypass;
        TxtBypassRules.Text = _config.BypassRules;
    }

    private void SaveUiToConfig()
    {
        _config.ServerHost = TxtHost.Text?.Trim() ?? "127.0.0.1";
        _config.ServerPort = int.TryParse(TxtPort.Text, out var port) ? port : 5000;
        _config.Path = string.IsNullOrWhiteSpace(TxtPath.Text) ? "/tunnel" : TxtPath.Text.Trim();
        _config.SniHost = string.IsNullOrWhiteSpace(TxtSni.Text) ? null : TxtSni.Text.Trim();
        _config.UseTls = SwUseTls.IsToggled;
        _config.Password = TxtPassword.Text ?? "";
        _config.Algorithm = PickerAlgo.SelectedItem?.ToString() ?? "aes-256-gcm";

        _config.LocalSocks5Port = int.TryParse(TxtSocks5Port.Text, out var sp) ? sp : 20808;
        _config.EnableBypass = SwEnableBypass.IsToggled;
        _config.BypassRules = TxtBypassRules.Text?.Trim() ?? "";

        _config.EnableSystemProxy = false;
        _config.EnableTun = false;

        string json = JsonSerializer.Serialize(_config);
        Preferences.Default.Set(ConfigStorageKey, json);
    }

    private void OnTlsToggled(object? sender, ToggledEventArgs e)
    {
        if (e.Value && TxtPort.Text == "5000")
        {
            TxtPort.Text = "443";
        }
    }

    private async void OnSaveConfigClicked(object? sender, EventArgs e)
    {
        SaveUiToConfig();
        AppendLog("✅ 配置已成功保存！");
        await DisplayAlert("提示", "代理节点配置已保存到本地！", "确定");
    }

    private async void OnToggleConnectClicked(object? sender, EventArgs e)
    {
        BtnConnect.IsEnabled = false;
        try
        {
            if (_engine != null && _engine.IsRunning)
            {
                await StopEngineAsync();
            }
            else
            {
                SaveUiToConfig();
                await StartEngineAsync();
            }
        }
        catch (Exception ex)
        {
            AppendLog($"❌ 操作失败: {ex.Message}");
            await DisplayAlert("连接错误", ex.Message, "确定");
        }
        finally
        {
            BtnConnect.IsEnabled = true;
        }
    }

    private async Task StartEngineAsync()
    {
        UpdateConnectionUi(TransportState.Connecting, "正在连接 Cloudflare 节点...");
        AppendLog($"🚀 正在连接代理节点: {_config.ServerHost}:{_config.ServerPort} (TLS: {_config.UseTls})...");

        _engine = new ProxyClientEngine(_config);

        _engine.StatusChanged += (state, err) =>
        {
            Dispatcher.Dispatch(() => UpdateConnectionUi(state, err));
        };

        _engine.LatencyUpdated += ms =>
        {
            Dispatcher.Dispatch(() => LblLatency.Text = $"{ms} ms");
        };

        _engine.TrafficUpdated += (up, down) =>
        {
            Interlocked.Exchange(ref _currentTotalUpload, up);
            Interlocked.Exchange(ref _currentTotalDownload, down);
        };

        _engine.LogMessage += msg =>
        {
            Dispatcher.Dispatch(() => AppendLog(msg));
        };

        await _engine.StartAsync();
        UpdateConnectionUi(TransportState.Connected, null);
        AppendLog($"✅ 代理已启动！本地监听: 127.0.0.1:{_config.LocalSocks5Port}");
    }

    private async Task StopEngineAsync()
    {
        BtnConnect.Text = "断开中...";
        BtnConnect.BackgroundColor = Color.FromArgb("#64748B");
        StatusText.Text = "正在断开连接...";
        if (_engine != null)
        {
            await _engine.StopAsync();
            await _engine.DisposeAsync();
            _engine = null;
        }
        UpdateConnectionUi(TransportState.Disconnected, null);
        AppendLog("⏹ 代理已停止。");
    }

    private void UpdateConnectionUi(TransportState state, string? err)
    {
        switch (state)
        {
            case TransportState.Connecting:
                StatusDot.TextColor = Color.FromArgb("#EAB308");
                StatusText.Text = "正在连接中...";
                BtnConnect.Text = "连接中...";
                BtnConnect.BackgroundColor = Color.FromArgb("#CA8A04");
                break;

            case TransportState.Connected:
                StatusDot.TextColor = Color.FromArgb("#16A34A");
                StatusText.Text = "已安全连接 (SOCKS5/HTTP 运行中)";
                BtnConnect.Text = "⏹ 点击断开";
                BtnConnect.BackgroundColor = Color.FromArgb("#DC2626");
                break;

            case TransportState.Reconnecting:
                StatusDot.TextColor = Color.FromArgb("#EAB308");
                StatusText.Text = "正在重新连接...";
                BtnConnect.Text = "重连中...";
                BtnConnect.BackgroundColor = Color.FromArgb("#CA8A04");
                break;

            case TransportState.Disconnected:
            default:
                StatusDot.TextColor = Colors.Gray;
                StatusText.Text = !string.IsNullOrEmpty(err) ? $"连接失败: {err}" : "未连接 (点击下方按钮连接)";
                BtnConnect.Text = "▶ 点击连接";
                BtnConnect.BackgroundColor = Color.FromArgb("#107C41");
                LblLatency.Text = "-- ms";
                break;
        }
    }

    private void OnSpeedTimerTick(object? sender, EventArgs e)
    {
        if (_engine == null || !_engine.IsRunning)
        {
            LblSpeedUp.Text = "↑ 0.0 KB/s";
            LblSpeedDown.Text = "↓ 0.0 KB/s";
            return;
        }

        long curUp = Interlocked.Read(ref _currentTotalUpload);
        long curDown = Interlocked.Read(ref _currentTotalDownload);

        long diffUp = Math.Max(0, curUp - _lastUploadBytes);
        long diffDown = Math.Max(0, curDown - _lastDownloadBytes);

        _lastUploadBytes = curUp;
        _lastDownloadBytes = curDown;

        double upKb = diffUp / 1024.0;
        double downKb = diffDown / 1024.0;

        LblSpeedUp.Text = $"↑ {upKb:F1} KB/s";
        LblSpeedDown.Text = $"↓ {downKb:F1} KB/s";
    }

    private async void OnTestLatencyClicked(object? sender, EventArgs e)
    {
        SaveUiToConfig();
        string host = _config.ServerHost;
        int port = _config.ServerPort;
        string scheme = _config.UseTls ? "wss" : "ws";
        string path = _config.Path.StartsWith('/') ? _config.Path : "/" + _config.Path;
        var uri = new Uri($"{scheme}://{host}:{port}{path}");

        AppendLog($"⚡ 开始测试连接与延迟 -> {uri} ...");
        var sw = Stopwatch.StartNew();

        try
        {
            using var ws = new ClientWebSocket();
            if (!string.IsNullOrWhiteSpace(_config.SniHost))
            {
                ws.Options.SetRequestHeader("Host", _config.SniHost);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ws.ConnectAsync(uri, cts.Token);
            sw.Stop();

            long ms = sw.ElapsedMilliseconds;
            LblLatency.Text = $"{ms} ms";
            AppendLog($"✅ 连接测试成功！往返延迟: {ms} ms");
            await DisplayAlert("测试结果", $"节点握手成功！\n网络延迟: {ms} ms\n节点地址: {host}:{port}", "确定");
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Ping done", CancellationToken.None);
        }
        catch (Exception ex)
        {
            sw.Stop();
            LblLatency.Text = "超时";
            AppendLog($"❌ 连接测试失败: {ex.Message}");
            await DisplayAlert("测试失败", $"无法连接到目标服务器:\n{ex.Message}", "确定");
        }
    }

    private void AppendLog(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string line = $"[{timestamp}] {message}";

        if (string.IsNullOrEmpty(LblLogs.Text) || LblLogs.Text.StartsWith("[就绪]"))
        {
            LblLogs.Text = line;
        }
        else
        {
            // 保持最新 80 行日志
            var lines = LblLogs.Text.Split('\n');
            if (lines.Length > 80)
            {
                LblLogs.Text = string.Join('\n', lines[^70..]) + "\n" + line;
            }
            else
            {
                LblLogs.Text += "\n" + line;
            }
        }
    }

    private void OnClearLogClicked(object? sender, EventArgs e)
    {
        LblLogs.Text = $"[{DateTime.Now:HH:mm:ss}] 日志已清空。";
    }
}
