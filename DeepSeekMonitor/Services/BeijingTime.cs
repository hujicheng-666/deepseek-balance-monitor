using System;

namespace DeepSeekMonitor.Services;

/// <summary>北京时间（UTC+8，无夏令时）转换助手。</summary>
public static class BeijingTime
{
    /// <summary>北京时间偏移量：UTC+8。</summary>
    public static readonly TimeSpan Offset = TimeSpan.FromHours(8);

    /// <summary>当前北京时间。</summary>
    public static DateTime Now => DateTime.UtcNow + Offset;

    /// <summary>把 UTC 时间转换为北京时间。</summary>
    public static DateTime FromUtc(DateTime utc) => utc + Offset;

    /// <summary>把任意时刻显示为北京时间（返回 +8 时区的 DateTimeOffset）。</summary>
    public static DateTimeOffset ToBeijing(DateTimeOffset value) => value.ToOffset(Offset);
}
