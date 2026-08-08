using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using DeepSeekMonitor.Models;

namespace DeepSeekMonitor.Services;

public class ApiException : Exception
{
    public ApiException(string message) : base(message) { }
}

/// <summary>
/// 跨平台版 DeepSeek API：仅依赖 .NET HttpClient（各平台 TLS 一致）。
/// status.deepseek.com 已迁移到 Flashduty 状态页平台，且其阿里云节点会丢弃
/// Windows SChannel 握手，因此统一优先请求 schannel/系统 TLS 都能直连的
/// Flashcat 镜像域名 api.flashcat.cloud。
/// </summary>
public class DeepSeekApi
{
    private const string BalanceUrl = "https://api.deepseek.com/user/balance";

    private const string FlashcatPageId = "6410630422455";
    private const string StatusApiMirror = "https://api.flashcat.cloud/status-page/" + FlashcatPageId;
    private const string StatusApiOrigin = "https://status.deepseek.com/api/status-page/" + FlashcatPageId;
    private const string HistoryRssUrl = "https://status.deepseek.com/history.rss";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<BalanceInfo> FetchBalanceAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ApiException("还没有设置 API Key 哦，右键小窗选「设置 API Key」吧～");

        using var req = new HttpRequestMessage(HttpMethod.Get, BalanceUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new ApiException($"网络好像走丢了（{ex.GetType().Name}），请稍后再试");
        }

        if (resp.StatusCode == HttpStatusCode.Unauthorized)
            throw new ApiException("API Key 好像不对哦，请检查后再试 (401)");
        if (resp.StatusCode == HttpStatusCode.PaymentRequired)
            throw new ApiException("账户余额不足啦，先去充值吧 (402)");
        if (!resp.IsSuccessStatusCode)
            throw new ApiException($"服务器有点小情绪，状态码 {(int)resp.StatusCode}");

        var json = await resp.Content.ReadAsStringAsync();
        try
        {
            var data = JsonSerializer.Deserialize<BalanceInfo>(json);
            if (data?.BalanceInfos == null)
                throw new ApiException("返回的数据里没有余额信息");
            return data;
        }
        catch (JsonException)
        {
            throw new ApiException("返回的数据看不太懂，请稍后再试");
        }
    }

    /// <summary>读取 DeepSeek 官方状态页近期服务事件（Flashduty 事件历史，按时间倒序）。</summary>
    public async Task<List<ServiceEvent>> FetchServiceEventsAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var from = now.AddDays(-90).ToUnixTimeSeconds();
        var to = now.AddDays(1).ToUnixTimeSeconds();

        foreach (var host in new[] { StatusApiMirror, StatusApiOrigin })
        {
            try
            {
                var json = await GetStatusPageAsync($"{host}/change/list?start_at_seconds={from}&end_at_seconds={to}");
                return ParseFlashdutyIncidents(json);
            }
            catch (Exception)
            {
                // 尝试下一个来源
            }
        }

        try
        {
            return ParseRss(await GetStatusPageAsync(HistoryRssUrl));
        }
        catch (Exception rssError)
        {
            throw new ApiException($"无法连接 DeepSeek 官方状态页（{rssError.Message}）");
        }
    }

    private static async Task<string> GetStatusPageAsync(string url)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36");
                request.Headers.Accept.ParseAdd("application/json, application/rss+xml, text/xml, */*");
                request.Headers.AcceptEncoding.ParseAdd("identity");
                using var response = await Http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
            if (attempt < 2)
                await Task.Delay(TimeSpan.FromMilliseconds(650 * (attempt + 1)));
        }
        throw new ApiException(lastError?.Message ?? "状态页连接失败");
    }

    private static List<ServiceEvent> ParseFlashdutyIncidents(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            throw new JsonException("Missing change/list items");

        var events = new List<ServiceEvent>();
        foreach (var item in items.EnumerateArray())
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString() ?? "服务事件" : "服务事件";

            long atSeconds = 0;
            if (item.TryGetProperty("start_at_seconds", out var st)) st.TryGetInt64(out atSeconds);

            var detail = item.TryGetProperty("description", out var d)
                ? CleanFeed(d.GetString() ?? "状态已更新")
                : "状态已更新";

            if (item.TryGetProperty("updates", out var updates) && updates.ValueKind == JsonValueKind.Array)
            {
                var parts = new List<string>();
                foreach (var u in updates.EnumerateArray())
                {
                    if (u.TryGetProperty("description", out var b))
                    {
                        var body = CleanFeed(b.GetString() ?? "");
                        if (!string.IsNullOrWhiteSpace(body)) parts.Add(body);
                    }
                }
                if (parts.Count > 0) detail = string.Join("\n", parts);
            }

            events.Add(new ServiceEvent { Time = FormatTime(atSeconds), Title = title, Detail = detail });
        }
        return events;
    }

    private static string FormatTime(long unixSeconds)
        => unixSeconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "";

    private static List<ServiceEvent> ParseRss(string xml)
    {
        var doc = XDocument.Parse(xml);
        var events = new List<ServiceEvent>();
        foreach (var item in doc.Descendants("item"))
        {
            var title = CleanFeed(item.Element("title")?.Value ?? "服务事件");
            var detail = CleanFeed(item.Element("description")?.Value ?? "状态已更新");
            var pub = item.Element("pubDate")?.Value ?? "";
            var time = DateTimeOffset.TryParse(pub, out var dt) ? dt.ToString("yyyy-MM-dd HH:mm") : "";
            events.Add(new ServiceEvent { Time = time, Title = title, Detail = detail });
        }
        return events;
    }

    private static string CleanFeed(string text)
    {
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }
}
