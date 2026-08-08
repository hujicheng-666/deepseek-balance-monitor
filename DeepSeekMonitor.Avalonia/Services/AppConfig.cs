using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeekMonitor.Services;

public class AppConfig
{
    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = "";

    [JsonPropertyName("refresh_interval")]
    public int RefreshInterval { get; set; } = 60;

    [JsonPropertyName("low_threshold")]
    public double LowThreshold { get; set; } = 10.0;

    [JsonPropertyName("low_warn")]
    public bool LowWarn { get; set; } = true;

    // 配置跟随程序所在目录；打包/安装后同样写在可执行文件旁边。
    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

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
