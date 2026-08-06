using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeekMonitor.Services;

public class AppConfig
{
    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = "";

    // 字段与 config.example.json 保持一致，由本地 config.json 读写。
    [JsonPropertyName("refresh_interval")]
    public int RefreshInterval { get; set; } = 60;

    [JsonPropertyName("low_threshold")]
    public double LowThreshold { get; set; } = 10.0;

    [JsonPropertyName("low_warn")]
    public bool LowWarn { get; set; } = true;

    private static string ConfigPath => System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
        }
        catch { /* 配置损坏就用默认值 */ }
        return new AppConfig();
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 忽略写入失败 */ }
    }
}
