using System.Collections.Generic;

namespace DeepSeekMonitor.Models;

/// <summary>单个 API 模型的官方定价（美元 / 百万 tokens）。</summary>
public sealed class ModelPrice
{
    public ModelPrice(string apiName, string baseModel, string description,
        double inputCacheHit, double inputCacheMiss, double output,
        double peakInputCacheHit, double peakInputCacheMiss, double peakOutput,
        double offPeakInputCacheHit, double offPeakInputCacheMiss, double offPeakOutput)
    {
        ApiName = apiName;
        BaseModel = baseModel;
        Description = description;
        InputCacheHit = inputCacheHit;
        InputCacheMiss = inputCacheMiss;
        Output = output;
        PeakInputCacheHit = peakInputCacheHit;
        PeakInputCacheMiss = peakInputCacheMiss;
        PeakOutput = peakOutput;
        OffPeakInputCacheHit = offPeakInputCacheHit;
        OffPeakInputCacheMiss = offPeakInputCacheMiss;
        OffPeakOutput = offPeakOutput;
    }

    /// <summary>API 里的模型名，如 deepseek-v4-flash。</summary>
    public string ApiName { get; }

    /// <summary>当前对应的基础模型版本，如 DeepSeek-V4-Flash-0731。</summary>
    public string BaseModel { get; }

    /// <summary>一句话用途说明。</summary>
    public string Description { get; }

    /// <summary>现行输入价（缓存命中），美元 / 百万 tokens。</summary>
    public double InputCacheHit { get; }

    /// <summary>现行输入价（缓存未命中），美元 / 百万 tokens。</summary>
    public double InputCacheMiss { get; }

    /// <summary>现行输出价，美元 / 百万 tokens。</summary>
    public double Output { get; }

    /// <summary>峰谷计费生效后的高峰输入价（缓存命中），美元 / 百万 tokens。</summary>
    public double PeakInputCacheHit { get; }

    /// <summary>峰谷计费生效后的高峰输入价（缓存未命中），美元 / 百万 tokens。</summary>
    public double PeakInputCacheMiss { get; }

    /// <summary>峰谷计费生效后的高峰输出价，美元 / 百万 tokens。</summary>
    public double PeakOutput { get; }

    /// <summary>峰谷计费生效后的非高峰输入价（缓存命中），美元 / 百万 tokens。</summary>
    public double OffPeakInputCacheHit { get; }

    /// <summary>峰谷计费生效后的非高峰输入价（缓存未命中），美元 / 百万 tokens。</summary>
    public double OffPeakInputCacheMiss { get; }

    /// <summary>峰谷计费生效后的非高峰输出价，美元 / 百万 tokens。</summary>
    public double OffPeakOutput { get; }
}

/// <summary>
/// DeepSeek 官方模型定价快照。官方价格偶有调整，本表仅作参考，
/// 展示页底部可一键打开官方定价页核对。
/// </summary>
public static class ModelPricing
{
    /// <summary>DeepSeek 官方定价页。</summary>
    public const string OfficialPage = "https://api-docs.deepseek.com/quick_start/pricing";

    /// <summary>快照收录日期，展示在定价页底部。</summary>
    public const string UpdatedAt = "2026-08-14";

    /// <summary>峰谷计费生效时间（UTC），展示在定价页上。</summary>
    public const string PeakOffPeakEffective = "2026-08-16 16:00 UTC";

    public static readonly IReadOnlyList<ModelPrice> All = new List<ModelPrice>
    {
        // deepseek-v4-flash / deepseek-v4-pro（官方 2026-08-14 现行价 + 8/16 起峰谷价，美元 / 百万 tokens）
        new("deepseek-v4-flash", "DeepSeek-V4-Flash-0731", "轻量快速",
            0.0028, 0.14, 0.28,      // 现行：输入(命中/未命中) / 输出
            0.014, 0.44, 1.32,       // 高峰：输入(命中/未命中) / 输出
            0.007, 0.22, 0.66),      // 非高峰：输入(命中/未命中) / 输出
        new("deepseek-v4-pro", "DeepSeek-V4-Pro-0813", "高规格推理",
            0.003625, 0.435, 0.87,
            0.044, 1.32, 3.96,
            0.022, 0.66, 1.98),
    };
}
