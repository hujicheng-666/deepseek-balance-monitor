namespace DeepSeekMonitor.Models;

/// <summary>服务事件时间线上的一行。</summary>
public class ServiceEvent
{
    public string Time { get; set; } = "";
    public string Title { get; set; } = "";
    public string Detail { get; set; } = "";
}
