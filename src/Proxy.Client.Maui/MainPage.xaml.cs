using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;
using Proxy.Client.Core;
using Proxy.Client.Core.Transport;
using Proxy.Common.Crypto;
using Proxy.Common.Models;
using Proxy.Common.Utils;

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

    private readonly Queue<string> _recentLogs = new(100);
    private bool _logUpdateScheduled;

    public MainPage()
    {
        try
        {
            InitializeComponent();

            // 填充加密算法选项
            foreach (var algo in CryptoFactory.SupportedAlgorithms)
            {
                PickerAlgo.Items.Add(algo);
            }

            // 填充并发通道数选项 (多路复用连接池)
            PickerConcurrency.Items.Add("1 (单连接通道 - 经典)");
            PickerConcurrency.Items.Add("2 (2 条并发通道)");
            PickerConcurrency.Items.Add("4 (4 条并发通道 - 推荐流畅)");
            PickerConcurrency.Items.Add("8 (8 条并发通道 - 极速狂飙)");
            PickerConcurrency.Items.Add("16 (16 条并发通道 - 极致吞吐)");

            _config = LoadConfig();
            ApplyConfigToUi();
        }
        catch (Exception ex)
        {
            _config ??= CreateDefaultConfig();
            System.Diagnostics.Debug.WriteLine($"[MainPage Init Error] {ex}");
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            if (_speedTimer == null && Dispatcher != null)
            {
                _speedTimer = Dispatcher.CreateTimer();
                _speedTimer.Interval = TimeSpan.FromSeconds(1);
                _speedTimer.Tick += OnSpeedTimerTick;
                _speedTimer.Start();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Timer Init Warning] {ex}");
        }
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        try
        {
            _speedTimer?.Stop();
            _speedTimer = null;
        }
        catch { }
    }

    private ProxyClientConfig LoadConfig()
    {
        try
        {
            string? json = Preferences.Default.Get<string?>(ConfigStorageKey, null);
            if (!string.IsNullOrWhiteSpace(json))
            {
                var cfg = JsonSerializer.Deserialize(json, ProxyJsonContext.Default.ProxyClientConfig);
                if (cfg != null) return cfg;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LoadConfig Error] {ex.Message}");
        }

        return CreateDefaultConfig();
    }

    private static ProxyClientConfig CreateDefaultConfig() => new()
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

        int channels = _config.ConcurrencyChannels > 0 ? _config.ConcurrencyChannels : 4;
        switch (channels)
        {
            case 1: PickerConcurrency.SelectedIndex = 0; break;
            case 2: PickerConcurrency.SelectedIndex = 1; break;
            case 8: PickerConcurrency.SelectedIndex = 3; break;
            case 16: PickerConcurrency.SelectedIndex = 4; break;
            case 4:
            default: PickerConcurrency.SelectedIndex = 2; break;
        }
        LblConcurrencyDesc.Text = $"{channels} 条并发通道";

        TxtSocks5Port.Text = _config.LocalSocks5Port.ToString();
        SwEnableBypass.IsToggled = _config.EnableBypass;
        SwFilterMode.IsToggled = _config.ProcessFilterMode == ProcessProxyMode.ProxySelected;
        UpdateFilterModeUi(SwFilterMode.IsToggled);
        TxtBypassRules.Text = _config.BypassRules;
    }

    private void OnConcurrencyChanged(object? sender, EventArgs e)
    {
        int ch = PickerConcurrency.SelectedIndex switch
        {
            0 => 1,
            1 => 2,
            3 => 8,
            4 => 16,
            _ => 4
        };
        _config.ConcurrencyChannels = ch;
        LblConcurrencyDesc.Text = $"{ch} 条并发通道";
    }

    private void UpdateFilterModeUi(bool isWhitelist)
    {
        if (isWhitelist)
        {
            LblFilterModeTitle.Text = "🚀 白名单模式 (只代理选中规则/应用)";
            LblFilterModeTitle.TextColor = Color.FromArgb("#0369A1");
            LblFilterModeDesc.Text = "仅名单内的规则/应用走代理，其余所有流量直连本地网络";
        }
        else
        {
            LblFilterModeTitle.Text = "🛡️ 黑名单模式 (直连选中规则/应用)";
            LblFilterModeTitle.TextColor = Color.FromArgb("#0F172A");
            LblFilterModeDesc.Text = "名单内的规则/应用直连本地网络，其余流量全部走代理";
        }
    }

    private void OnFilterModeToggled(object? sender, ToggledEventArgs e)
    {
        UpdateFilterModeUi(e.Value);
        _config.ProcessFilterMode = e.Value ? ProcessProxyMode.ProxySelected : ProcessProxyMode.BypassSelected;

        // 若引擎正在运行，实时无缝热生效
        if (_engine != null && _engine.IsRunning)
        {
            _engine.UpdateProcessBypass(_config.BypassProcessList, _config.ProcessFilterMode);
            AppendLog($"🔄 分流模式已即时热切换为: {(e.Value ? "【白名单模式 (仅代理选中)】" : "【黑名单模式 (仅直连选中)】")}");
        }
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

        _config.ConcurrencyChannels = PickerConcurrency.SelectedIndex switch
        {
            0 => 1,
            1 => 2,
            3 => 8,
            4 => 16,
            _ => 4
        };

        _config.LocalSocks5Port = int.TryParse(TxtSocks5Port.Text, out var sp) ? sp : 20808;
        _config.EnableBypass = SwEnableBypass.IsToggled;
        _config.ProcessFilterMode = SwFilterMode.IsToggled ? ProcessProxyMode.ProxySelected : ProcessProxyMode.BypassSelected;
        _config.BypassRules = TxtBypassRules.Text?.Trim() ?? "";

        _config.EnableSystemProxy = false;
        _config.EnableTun = false;

        string json = JsonSerializer.Serialize(_config, ProxyJsonContext.Default.ProxyClientConfig);
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
                int totalCh = _engine?.TotalChannelCount ?? 1;
                int actCh = _engine?.ActiveChannelCount ?? 1;
                StatusText.Text = totalCh > 1
                    ? $"已安全连接 ({actCh}/{totalCh} 条并发加速通道)"
                    : "已安全连接 (SOCKS5/HTTP 运行中)";
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

    private async void OnTestConnectionClicked(object? sender, EventArgs e)
    {
        SaveUiToConfig();
        BtnTestConnection.IsEnabled = false;
        BtnTestConnection.Text = "⏳ 测试中...";

        try
        {
            if (_engine != null && _engine.IsRunning)
            {
                // 1. 代理运行中：测试端到端真实出海连通性
                AppendLog($"⚡ [测试连接] 代理运行中，正在通过本地代理 (127.0.0.1:{_config.LocalSocks5Port}) 测试出海网络...");
                var sw = Stopwatch.StartNew();

                var proxy = new System.Net.WebProxy($"http://127.0.0.1:{_config.LocalSocks5Port}");
                var handler = new HttpClientHandler
                {
                    Proxy = proxy,
                    UseProxy = true
                };

                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
                string resp = await http.GetStringAsync("https://1.1.1.1/cdn-cgi/trace");
                sw.Stop();

                long ms = sw.ElapsedMilliseconds;
                LblLatency.Text = $"{ms} ms";

                string exitIp = "未知";
                string colo = "未知";
                foreach (var line in resp.Split('\n'))
                {
                    if (line.StartsWith("ip=")) exitIp = line.Substring(3).Trim();
                    else if (line.StartsWith("colo=")) colo = line.Substring(5).Trim();
                }

                AppendLog($"✅ [连接测试成功] 端到端延迟: {ms} ms, 出口节点机房: {colo}, 出口 IP: {exitIp}");
                await DisplayAlert("测试连接成功",
                    $"🎉 代理通道全面通畅！\n\n" +
                    $"• 端到端延迟: {ms} ms\n" +
                    $"• Cloudflare 机房: {colo}\n" +
                    $"• 当前出口 IP: {exitIp}\n" +
                    $"• 本地代理: 127.0.0.1:{_config.LocalSocks5Port}\n\n" +
                    $"您的流量已成功通过加密隧道出海！", "确定");
            }
            else
            {
                // 2. 代理未启动：测试远端 Cloudflare WebSocket 节点握手与延迟
                string host = _config.ServerHost;
                int port = _config.ServerPort;
                string scheme = _config.UseTls ? "wss" : "ws";
                string path = _config.Path.StartsWith('/') ? _config.Path : "/" + _config.Path;
                var uri = new Uri($"{scheme}://{host}:{port}{path}");

                AppendLog($"⚡ [测试连接] 正在测试远端 Cloudflare 节点握手 -> {uri} ...");
                var sw = Stopwatch.StartNew();

                using var ws = new ClientWebSocket();
                if (!string.IsNullOrWhiteSpace(_config.SniHost))
                {
                    ws.Options.SetRequestHeader("Host", _config.SniHost);
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                await ws.ConnectAsync(uri, cts.Token);
                sw.Stop();

                long ms = sw.ElapsedMilliseconds;
                LblLatency.Text = $"{ms} ms";
                AppendLog($"✅ [节点握手成功] 往返网络延迟: {ms} ms (节点: {host}:{port})");
                await DisplayAlert("测试连接成功",
                    $"✅ 远程节点握手成功！\n\n" +
                    $"• 握手延迟: {ms} ms\n" +
                    $"• 节点地址: {host}:{port}\n" +
                    $"• 协议模式: {scheme.ToUpper()} (TLS: {_config.UseTls})\n" +
                    $"• SNI 域名: {(!string.IsNullOrEmpty(_config.SniHost) ? _config.SniHost : "未指定")}\n\n" +
                    $"节点状态良好，可随时点击【点击连接】启动代理！", "确定");
            }
        }
        catch (Exception ex)
        {
            LblLatency.Text = "超时";
            AppendLog($"❌ [测试连接失败] {ex.Message}");

            string hint = "请检查节点 IP/域名、端口或网络连接。";
            if (ex.Message.Contains("SSL") || ex.Message.Contains("certificate") || ex.Message.Contains("TLS"))
            {
                hint = "TLS/SSL 证书校验失败。若填写的节点为 Cloudflare 优选 IP，请务必在【真实域名 (SNI)】中填写您绑定的域名！";
            }
            else if (ex.Message.Contains("404"))
            {
                hint = "HTTP 404 错误：远程服务器未找到指定的 WebSocket 路径，请检查路径是否正确。";
            }
            else if (ex.Message.Contains("403"))
            {
                hint = "HTTP 403 禁止访问：请检查连接密码或 Token 是否与服务端一致。";
            }

            await DisplayAlert("测试连接失败", $"无法连接到目标：\n{ex.Message}\n\n💡 诊断建议：\n{hint}", "确定");
        }
        finally
        {
            BtnTestConnection.IsEnabled = true;
            BtnTestConnection.Text = "⚡ 测试连接";
        }
    }

    private async void OnConfigureSystemVpnClicked(object? sender, EventArgs e)
    {
        SaveUiToConfig();
        AppendLog("🛡️ 正在准备 iOS 系统 VPN / 蜂窝网络代理配置...");
        try
        {
            var (success, msg, path) = await Services.IosVpnHelper.PrepareVpnProfileAsync(_config.LocalSocks5Port);
            if (!success || string.IsNullOrEmpty(path))
            {
                AppendLog($"❌ {msg}");
                await DisplayAlert("提示", msg, "确定");
                return;
            }

            AppendLog($"✅ {msg}");

            // 若当前代理正在运行，引导用户通过 Safari 一键安装
            if (_engine != null && _engine.IsRunning)
            {
                bool openSafari = await DisplayAlert("iOS 系统 VPN 与流量配置",
                    "💡 由于 iOS 系统底层安全机制，需通过描述文件将移动蜂窝流量接管至本地代理：\n\n" +
                    "• 方案 A (推荐)：点击【在 Safari 中安装】，系统将弹出配置描述文件下载提示，随后进入【系统设置 -> 已下载的描述文件】安装即可；\n" +
                    "• 方案 B：点击【导出/分享文件】保存至本地文件或通过隔空投送安装。",
                    "在 Safari 中安装", "导出/分享文件");

                if (openSafari)
                {
                    await Browser.Default.OpenAsync($"http://127.0.0.1:{_config.LocalSocks5Port}/mobileconfig", BrowserLaunchMode.External);
                    return;
                }
            }
            else
            {
                await DisplayAlert("iOS 系统 VPN 与流量配置",
                    "💡 提示：建议先在主界面点击【点击连接】启动本地代理，随后再点击此按钮，Safari 将自动弹出描述文件安装确认！\n\n您也可以直接点击【确定】导出描述文件手动安装。",
                    "确定");
            }

            // 导出/分享文件
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "安装 iOS 系统 VPN / 蜂窝代理描述文件",
                File = new ShareFile(path, "application/x-apple-aspen-config")
            });
        }
        catch (Exception ex)
        {
            AppendLog($"❌ 配置 VPN 异常: {ex.Message}");
            await DisplayAlert("错误", $"配置失败: {ex.Message}", "确定");
        }
    }

    private async void OnInstallMobileConfigClicked(object? sender, EventArgs e)
    {
        SaveUiToConfig();
        try
        {
            string xml = MobileConfigHelper.GenerateCellularMobileConfigXml(_config.LocalSocks5Port);
            string cachePath = Path.Combine(FileSystem.CacheDirectory, $"CloudflareProxy_{_config.LocalSocks5Port}.mobileconfig");
            await File.WriteAllTextAsync(cachePath, xml, System.Text.Encoding.UTF8);

            AppendLog($"📥 已生成蜂窝流量描述文件: {cachePath}");

            // 若代理正在运行，引导用户通过 Safari 一键下载安装
            if (_engine != null && _engine.IsRunning)
            {
                bool openSafari = await DisplayAlert("安装蜂窝流量描述文件",
                    "已生成针对当前代理端口的 iOS 描述文件！\n\n" +
                    "点击【在 Safari 中安装】即可直接触发系统安装弹窗，前往【系统设置 -> 已下载的描述文件】确认即可。\n\n" +
                    "或者点击【分享/导出文件】通过隔空投送或保存到本地文件。",
                    "在 Safari 中安装", "分享/导出文件");

                if (openSafari)
                {
                    await Browser.Default.OpenAsync($"http://127.0.0.1:{_config.LocalSocks5Port}/mobileconfig", BrowserLaunchMode.External);
                    return;
                }
            }

            // 分享或通过系统应用打开
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "安装 iOS 蜂窝网络代理描述文件",
                File = new ShareFile(cachePath, "application/x-apple-aspen-config")
            });
        }
        catch (Exception ex)
        {
            AppendLog($"❌ 生成描述文件失败: {ex.Message}");
            await DisplayAlert("生成失败", ex.Message, "确定");
        }
    }

    private async void OnCopySocks5UrlClicked(object? sender, EventArgs e)
    {
        SaveUiToConfig();
        string url = $"socks5://127.0.0.1:{_config.LocalSocks5Port}#CloudflareProxy";
        await Clipboard.Default.SetTextAsync(url);
        AppendLog($"📋 已复制 SOCKS5 节点链接: {url}");
        await DisplayAlert("已复制节点", 
            $"SOCKS5 节点链接已成功复制到剪贴板：\n\n{url}\n\n💡 提示：在 Shadowrocket、Loon、Surge 或 Stash 中添加此 SOCKS5 节点，即可直接由其接管所有蜂窝移动流量！", 
            "确定");
    }

    private async void OnOpenShadowrocketClicked(object? sender, EventArgs e)
    {
        SaveUiToConfig();
        string srUrl = $"shadowrocket://add/socks5://127.0.0.1:{_config.LocalSocks5Port}?remarks=CloudflareProxy";
        try
        {
            bool launched = await Launcher.Default.OpenAsync(new Uri(srUrl));
            if (!launched)
            {
                await Clipboard.Default.SetTextAsync($"socks5://127.0.0.1:{_config.LocalSocks5Port}#CloudflareProxy");
                await DisplayAlert("未检测到客户端", 
                    "未自动唤起 Shadowrocket。已将 SOCKS5 节点链接复制到剪贴板，您可以打开任意规则代理软件粘贴添加！", 
                    "确定");
            }
        }
        catch
        {
            await Clipboard.Default.SetTextAsync($"socks5://127.0.0.1:{_config.LocalSocks5Port}#CloudflareProxy");
            await DisplayAlert("节点已复制", 
                "已将 SOCKS5 节点链接复制到剪贴板，您可以在已安装的规则客户端中添加使用！", 
                "确定");
        }
    }

    private void AppendLog(string message)
    {
        string timestamp = DateTime.Now.ToString("HH:mm:ss");
        string line = $"[{timestamp}] {message}";

        lock (_recentLogs)
        {
            if (_recentLogs.Count >= 80)
            {
                _recentLogs.Dequeue();
            }
            _recentLogs.Enqueue(line);

            if (_logUpdateScheduled) return;
            _logUpdateScheduled = true;
        }

        // 节流刷新：以 150ms 频率批量刷新 UI，杜绝单条高频更新导致的 XAML 重新排版与内存碎片
        Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(150), () =>
        {
            lock (_recentLogs)
            {
                _logUpdateScheduled = false;
                LblLogs.Text = string.Join('\n', _recentLogs);
            }
        });
    }

    private void OnClearLogClicked(object? sender, EventArgs e)
    {
        lock (_recentLogs)
        {
            _recentLogs.Clear();
        }
        LblLogs.Text = $"[{DateTime.Now:HH:mm:ss}] 日志已清空。";
    }
}
