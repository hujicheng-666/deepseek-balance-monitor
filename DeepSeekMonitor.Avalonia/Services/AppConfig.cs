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

    // 优先写入用户目录(Windows: %APPDATA%,Linux: ~/.config),避免安装目录只读导致保存失败;
    // 旧版写在程序目录的 config.json 会在 Load 时自动迁移过来。
    private static string UserConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeepSeekMonitor", "config.json");

    private static string LocalConfigPath => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(UserConfigPath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(UserConfigPath)) ?? new AppConfig();

            // 迁移旧版程序目录配置
            if (File.Exists(LocalConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(LocalConfigPath)) ?? new AppConfig();
                Save(cfg);
                return cfg;
            }
        }
        catch { /* 配置损坏就用默认值 */ }
        return new AppConfig();
    }

    /// <summary>保存配置到用户目录,成功返回 true;失败(磁盘/权限问题)返回 false。</summary>
    public static bool Save(AppConfig cfg)
    {
        try
        {
            var dir = Path.GetDirectoryName(UserConfigPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(UserConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch { return false; }
    }
}
