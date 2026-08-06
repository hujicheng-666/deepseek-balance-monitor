using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Serialization;

namespace DeepSeekMonitor.Models;

public class BalanceInfo
{
    [JsonPropertyName("is_available")]
    public bool IsAvailable { get; set; }

    [JsonPropertyName("balance_infos")]
    public List<BalanceInfoItem>? BalanceInfos { get; set; }
}

public class BalanceInfoItem
{
    [JsonPropertyName("currency")]
    public string Currency { get; set; } = "CNY";

    // DeepSeek 接口把余额返回为字符串（如 "110.00"）
    [JsonPropertyName("total_balance")]
    public string TotalBalanceStr { get; set; } = "0";

    [JsonPropertyName("granted_balance")]
    public string GrantedBalanceStr { get; set; } = "0";

    [JsonPropertyName("topped_up_balance")]
    public string ToppedUpBalanceStr { get; set; } = "0";

    // API values always use a dot decimal separator, independent of the
    // Windows display language.
    public double TotalBalance => ParseAmount(TotalBalanceStr);
    public double GrantedBalance => ParseAmount(GrantedBalanceStr);
    public double ToppedUpBalance => ParseAmount(ToppedUpBalanceStr);

    private static double ParseAmount(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : 0;
}
