using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    private const string PricingUrl = "https://api-docs.deepseek.com/quick_start/pricing";
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
        => await DownloadTextAsync(url, "application/json, application/rss+xml, text/xml, */*");

    /// <summary>通用文本下载：3 轮重试，避免偶发网络抖动直接失败。</summary>
    private static async Task<string> DownloadTextAsync(string url, string accept)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36");
                request.Headers.Accept.ParseAdd(accept);
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

    /// <summary>实时抓取并解析 DeepSeek 官方定价页。</summary>
    public async Task<PricingSnapshot> FetchPricingAsync()
    {
        var html = await DownloadTextAsync(PricingUrl, "text/html, application/xhtml+xml, */*");
        return ParsePricingHtml(html);
    }

    /// <summary>从 Docusaurus 定价页 HTML 中解析各模型的高峰/非高峰价格与峰谷时段。</summary>
    private static PricingSnapshot ParsePricingHtml(string html)
    {
        var table = Regex.Match(html, @"<table[^>]*>.*?</table>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!table.Success)
            throw new ApiException("官方定价页没有找到价格表");

        // 逐行解析成单元格文本（去掉 HTML 标签、HTML 实体并合并空白）。
        var rows = new List<List<string>>();
        foreach (Match rm in Regex.Matches(table.Value, @"<tr[^>]*>(.*?)</tr>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var cells = new List<string>();
            foreach (Match cm in Regex.Matches(rm.Groups[1].Value, @"<td[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var text = CleanCell(cm.Groups[1].Value);
                if (text.Length > 0) cells.Add(text);
            }
            if (cells.Count > 0) rows.Add(cells);
        }

        var names = FindRowValues(rows, c => c == "MODEL");
        var versions = FindRowValues(rows, c => c == "MODEL VERSION");
        var concurrency = TryFindRowValues(rows, c => c.StartsWith("Concurrency Limit", StringComparison.OrdinalIgnoreCase));

        var hit = FindPricingRow(rows, "CACHE HIT");
        var miss = FindPricingRow(rows, "CACHE MISS");
        var output = FindPricingRow(rows, "OUTPUT TOKENS");

        var count = names.Count;
        if (count == 0)
            throw new ApiException("官方定价页没有解析到模型");

        var models = new List<PricingModel>();
        for (var i = 0; i < count; i++)
        {
            // 每个定价行的最后 count 个单元格依次是各模型的价格。
            double PriceCell(List<string> row) => ParsePrice(row[row.Count - count + i]);
            models.Add(new PricingModel(
                names[i],
                i < versions.Count ? versions[i] : "",
                PriceCell(rows[hit]), PriceCell(rows[miss]), PriceCell(rows[output]),
                PriceCell(rows[hit + 1]), PriceCell(rows[miss + 1]), PriceCell(rows[output + 1]),
                i < concurrency.Count ? concurrency[i] : ""));
        }

        return new PricingSnapshot(models, ExtractPeakHoursNote(html), DateTimeOffset.Now);
    }

    /// <summary>返回首单元格满足条件的那一行去掉首单元格后的剩余单元格。</summary>
    private static List<string> FindRowValues(List<List<string>> rows, Func<string, bool> firstCellMatches)
    {
        foreach (var row in rows)
        {
            if (row.Count >= 2 && firstCellMatches(row[0]))
                return row.Skip(1).ToList();
        }
        throw new ApiException("官方定价页表格结构变化，无法解析");
    }

    /// <summary>同上，但找不到时返回空列表（用于可选字段）。</summary>
    private static List<string> TryFindRowValues(List<List<string>> rows, Func<string, bool> firstCellMatches)
    {
        foreach (var row in rows)
        {
            if (row.Count >= 2 && firstCellMatches(row[0]))
                return row.Skip(1).ToList();
        }
        return new List<string>();
    }

    /// <summary>找到包含指定标签的定价行（OFF-PEAK 行，其下一行即 PEAK 行）。</summary>
    private static int FindPricingRow(List<List<string>> rows, string label)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (string.Join(" ", rows[i]).IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0)
                return i;
        }
        throw new ApiException($"官方定价页缺少 {label} 行");
    }

    private static double ParsePrice(string cell)
    {
        var text = cell.Replace("$", "").Replace(",", "").Trim();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>提取 "Peak hours are ..." 那一句，用于展示峰谷时段并判断当前是否高峰。</summary>
    private static string ExtractPeakHoursNote(string html)
    {
        var match = Regex.Match(html, @"Peak hours are[^.<]*\.?", RegexOptions.IgnoreCase);
        return match.Success ? CleanCell(match.Value) : "";
    }

    private static string CleanCell(string fragment)
    {
        var text = WebUtility.HtmlDecode(fragment);
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
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
            ? BeijingTime.ToBeijing(DateTimeOffset.FromUnixTimeSeconds(unixSeconds)).ToString("yyyy-MM-dd HH:mm")
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
            var time = DateTimeOffset.TryParse(pub, out var dt) ? BeijingTime.ToBeijing(dt).ToString("yyyy-MM-dd HH:mm") : "";
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
