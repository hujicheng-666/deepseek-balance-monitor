using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeekMonitor.Models;

/// <summary>服务事件时间线上的一行。</summary>
public class ServiceEvent
{
    public string Time { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>status.deepseek.com 摘要（只用到 status + incidents）。</summary>
public class ServiceSummary
{
    [JsonPropertyName("status")]
    public ServiceStatus? Status { get; set; }

    [JsonPropertyName("incidents")]
    public List<JsonElement>? Incidents { get; set; }
}

public class ServiceStatus
{
    [JsonPropertyName("indicator")]
    public string Indicator { get; set; } = "unknown";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";
}
