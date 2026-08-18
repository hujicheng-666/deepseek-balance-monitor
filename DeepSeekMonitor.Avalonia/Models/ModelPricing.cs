using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DeepSeekMonitor.Models;

/// <summary>单个模型的价格（美元 / 百万 tokens），分高峰与非高峰两档。</summary>
public sealed class PricingModel
{
    public PricingModel(string apiName, string baseModel,
        double offPeakCacheHit, double offPeakCacheMiss, double offPeakOutput,
        double peakCacheHit, double peakCacheMiss, double peakOutput,
        string concurrency)
    {
        ApiName = apiName;
        BaseModel = baseModel;
        OffPeakCacheHit = offPeakCacheHit;
        OffPeakCacheMiss = offPeakCacheMiss;
        OffPeakOutput = offPeakOutput;
        PeakCacheHit = peakCacheHit;
        PeakCacheMiss = peakCacheMiss;
        PeakOutput = peakOutput;
        Concurrency = concurrency;
    }

    public string ApiName { get; }
    public string BaseModel { get; }
    public double OffPeakCacheHit { get; }
    public double OffPeakCacheMiss { get; }
    public double OffPeakOutput { get; }
    public double PeakCacheHit { get; }
    public double PeakCacheMiss { get; }
    public double PeakOutput { get; }

    /// <summary>官方页展示的并发上限，可能为空。</summary>
    public string Concurrency { get; }
}

/// <summary>官方定价页实时快照：模型价格 + 峰谷时段 + 当前是否处于高峰。</summary>
public sealed class PricingSnapshot
{
    public PricingSnapshot(IReadOnlyList<PricingModel> models, string peakHoursNote, DateTimeOffset fetchedAt)
    {
        Models = models;
        PeakHoursNote = peakHoursNote;
        FetchedAt = fetchedAt;

        // 从 "01:00 - 04:00 and 06:00 - 10:00 UTC" 这类描述中解析高峰时段窗口。
        var windows = new List<(int Start, int End)>();
        foreach (Match m in Regex.Matches(peakHoursNote ?? "", @"(\d{1,2}):\d{2}\s*-\s*(\d{1,2}):\d{2}"))
        {
            if (int.TryParse(m.Groups[1].Value, out var start) && int.TryParse(m.Groups[2].Value, out var end))
                windows.Add((start, end));
        }

        if (windows.Count == 0)
        {
            IsPeakNow = null;
            PeakHoursDisplay = peakHoursNote ?? "";
        }
        else
        {
            var hour = fetchedAt.UtcDateTime.Hour;
            var peak = false;
            foreach (var w in windows)
            {
                if (hour >= w.Start && hour < w.End) { peak = true; break; }
            }
            IsPeakNow = peak;

            var parts = new List<string>();
            foreach (var w in windows)
                parts.Add($"{w.Start:00}:00–{w.End:00}:00");
            PeakHoursDisplay = string.Join(" / ", parts) + " UTC";
        }
    }

    public IReadOnlyList<PricingModel> Models { get; }
    public string PeakHoursNote { get; }
    public DateTimeOffset FetchedAt { get; }

    /// <summary>当前是否处于高峰；时段解析失败时为 null。</summary>
    public bool? IsPeakNow { get; }

    /// <summary>峰谷时段展示文本，如 "01:00–04:00 / 06:00–10:00 UTC"。</summary>
    public string PeakHoursDisplay { get; }
}

/// <summary>
/// DeepSeek 官方模型定价。优先实时抓取官方定价页；抓取失败时使用内置快照兜底。
/// </summary>
public static class ModelPricing
{
    /// <summary>DeepSeek 官方定价页。</summary>
    public const string OfficialPage = "https://api-docs.deepseek.com/quick_start/pricing";

    /// <summary>离线兜底快照（2026-08-17 生效的峰谷价，美元 / 百万 tokens）。</summary>
    public static readonly PricingSnapshot Fallback = new(
        new List<PricingModel>
        {
            new("deepseek-v4-flash", "DeepSeek-V4-Flash-0731",
                0.007, 0.22, 0.66,      // 非高峰：输入(命中/未命中) / 输出
                0.014, 0.44, 1.32,      // 高峰：输入(命中/未命中) / 输出
                "2500"),
            new("deepseek-v4-pro", "DeepSeek-V4-Pro-0813",
                0.022, 0.66, 1.98,
                0.044, 1.32, 3.96,
                "500"),
        },
        "01:00 - 04:00 and 06:00 - 10:00 UTC",
        new DateTimeOffset(2026, 8, 17, 0, 0, 0, TimeSpan.Zero));
}
