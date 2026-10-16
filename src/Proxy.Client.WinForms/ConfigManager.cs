using System;
using System.IO;
using System.Text.Json;
using Proxy.Common.Models;

namespace Proxy.Client.WinForms;

public static class ConfigManager
{
    private static readonly string ConfigPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "client_config.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static ProxyClientConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                string json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<ProxyClientConfig>(json, JsonOptions);
                if (config != null) return config;
            }
        }
        catch { }

        // 返回默认配置
        return new ProxyClientConfig
        {
            ServerHost = "127.0.0.1",
            ServerPort = 5000,
            Path = "/tunnel",
            Password = "DefaultSecurePassword123!",
            Algorithm = "aes-256-gcm",
            UseTls = false,
            LocalSocks5Port = 20808,
            EnableTun = false
        };
    }

    public static void Save(ProxyClientConfig config)
    {
        try
        {
            string json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(ConfigPath, json);
        }
        catch { }
    }
}
