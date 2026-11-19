using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
using Proxy.Common.Models;

namespace Proxy.Client.Core.Outbound;

/// <summary>
/// sing-box 出站核心进程管理器 (支持 Shadowsocks 2022 及各种现代出站代理协议)
/// </summary>
public sealed class SingBoxProcessManager : IDisposable
{
    private Process? _process;
    private string? _tempConfigFile;
    private bool _isStarted;
    private int _internalPort;

    public event Action<string>? LogMessage;
    public int InternalPort => _internalPort;
    public bool IsRunning => _isStarted && _process != null && !_process.HasExited;

    /// <summary>
    /// 寻找程序自带的 sing-box.exe 可执行文件
    /// </summary>
    public static string? FindSingBoxExecutable()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string p1 = Path.Combine(baseDir, "sing-box.exe");
        if (File.Exists(p1)) return p1;

        string p2 = Path.Combine(Directory.GetCurrentDirectory(), "sing-box.exe");
        if (File.Exists(p2)) return p2;

        string p3 = Path.Combine(baseDir, "..", "..", "..", "sing-box.exe");
        if (File.Exists(p3)) return Path.GetFullPath(p3);

        string p4 = Path.Combine(baseDir, "src", "Proxy.Client.WinForms", "sing-box.exe");
        if (File.Exists(p4)) return Path.GetFullPath(p4);

        return null;
    }

    /// <summary>
    /// 获取一个本地空闲的 TCP 端口
    /// </summary>
    private static int GetAvailablePort(int preferPort = 20810)
    {
        try
        {
            using var l = new TcpListener(IPAddress.Loopback, preferPort);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
        catch
        {
            using var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }

    /// <summary>
    /// 启动 sing-box 进程以建立 Shadowsocks 代理上游通道
    /// </summary>
    public bool Start(ProxyClientConfig config)
    {
        if (IsRunning) return true;

        string? singBoxPath = FindSingBoxExecutable();
        if (string.IsNullOrEmpty(singBoxPath))
        {
            throw new FileNotFoundException("未找到 sing-box.exe 核心组件，请确认程序目录下存在 sing-box.exe。");
        }

        _internalPort = GetAvailablePort();

        // 构造 sing-box 配置文件
        var configObj = new
        {
            log = new { level = "warn" },
            inbounds = new object[]
            {
                new
                {
                    type = "mixed",
                    tag = "mixed-in",
                    listen = "127.0.0.1",
                    listen_port = _internalPort
                }
            },
            outbounds = new object[]
            {
                new
                {
                    type = "shadowsocks",
                    tag = "ss-out",
                    server = config.ServerHost,
                    server_port = config.ServerPort,
                    method = config.Algorithm,
                    password = config.Password
                }
            }
        };

        _tempConfigFile = Path.Combine(Path.GetTempPath(), $"singbox_{Guid.NewGuid():N}.json");
        string json = JsonSerializer.Serialize(configObj, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_tempConfigFile, json);

        LogMessage?.Invoke($"正在拉起 sing-box 核心 (SS-2022 出站: {config.ServerHost}:{config.ServerPort}, 本地桥接端口: {_internalPort})...");

        var psi = new ProcessStartInfo
        {
            FileName = singBoxPath,
            Arguments = $"run -c \"{_tempConfigFile}\"",
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(singBoxPath) ?? ""
        };

        try
        {
            _process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            CleanupTempFile();
            throw new InvalidOperationException($"启动 sing-box 核心失败: {ex.Message}", ex);
        }

        if (_process == null)
        {
            CleanupTempFile();
            throw new InvalidOperationException("创建 sing-box 进程句柄失败。");
        }

        _process.ErrorDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                LogMessage?.Invoke($"[sing-box] {e.Data}");
            }
        };
        _process.OutputDataReceived += (s, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                LogMessage?.Invoke($"[sing-box] {e.Data}");
            }
        };

        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();

        // 等待内部桥接端口就绪
        int retries = 20; // 20 * 100ms = 2s
        bool ready = false;
        while (retries-- > 0)
        {
            if (_process.HasExited)
            {
                int exitCode = _process.ExitCode;
                CleanupTempFile();
                throw new InvalidOperationException($"sing-box 核心异常退出 (ExitCode: {exitCode})。请检查 SS 节点密码或加密算法配置是否正确。");
            }

            try
            {
                using var tcp = new TcpClient();
                var connectResult = tcp.BeginConnect("127.0.0.1", _internalPort, null, null);
                var success = connectResult.AsyncWaitHandle.WaitOne(100);
                if (success && tcp.Connected)
                {
                    ready = true;
                    break;
                }
            }
            catch { }

            Thread.Sleep(100);
        }

        if (!ready)
        {
            Stop();
            throw new TimeoutException("等待 sing-box 内部服务监听超时。");
        }

        _isStarted = true;
        LogMessage?.Invoke($"sing-box 核心就绪，已成功对接 Shadowsocks 节点 (127.0.0.1:{_internalPort})！");
        return true;
    }

    public void Stop()
    {
        if (_process != null)
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
                _process.Dispose();
            }
            catch { }
            _process = null;
        }

        _isStarted = false;
        CleanupTempFile();
    }

    private void CleanupTempFile()
    {
        if (!string.IsNullOrEmpty(_tempConfigFile) && File.Exists(_tempConfigFile))
        {
            try { File.Delete(_tempConfigFile); } catch { }
            _tempConfigFile = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
