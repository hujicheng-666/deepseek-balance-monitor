using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
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

public class DeepSeekApi
{
    private const string BalanceUrl = "https://api.deepseek.com/user/balance";

    // status.deepseek.com 已从 Statuspage.io 迁移到 Flashduty(闪嘟) 状态页平台，
    // 旧 /api/v2/summary.json 与 /api/v2/incidents.json 均已下线(404)。
    // 此外其阿里云 NLB 节点会丢弃本机 SChannel 的 TLS 握手（OpenSSL/浏览器可通），
    // 导致 WinINet/HttpClient 都无法直连该域名。因此优先请求 schannel 可直连的
    // Flashcat 镜像域名 api.flashcat.cloud；status.deepseek.com 原域名仅作兜底。
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

        // 优先走 schannel 可直连的 Flashcat 镜像域名；失败再试 status.deepseek.com 原域名。
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

        // 最后兜底：旧 Statuspage RSS（部分网络/机器上仍可达）。
        try
        {
            return ParseRss(await GetStatusPageAsync(HistoryRssUrl));
        }
        catch (Exception rssError)
        {
            throw new ApiException($"无法连接 DeepSeek 官方状态页（{rssError.Message}）");
        }
    }

    /// <summary>解析 Flashduty change/list 接口，返回按时间倒序的服务事件。</summary>
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

            // 优先拼接各阶段更新说明作为详情，信息量比总描述更丰富。
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

    private static async Task<string> GetStatusPageAsync(string url)
    {
        Exception? lastError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // 先走 WinINet（跟随系统代理/PAC，与浏览器同路径）。
                return await Task.Run(() => DownloadWithWinInet(url));
            }
            catch (Exception ex)
            {
                lastError = ex;
            }

            try
            {
                // Some TLS middleboxes behave differently for WinINet. The
                // SocketsHttpHandler route gives the same URL a second, truly
                // independent connection path.
                return await DownloadWithHttpClientAsync(url);
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

    private static async Task<string> DownloadWithHttpClientAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36");
        request.Headers.Accept.ParseAdd("application/json, application/rss+xml, text/xml, */*");
        request.Headers.AcceptEncoding.ParseAdd("identity");
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static string DownloadWithWinInet(string url)
    {
        const int InternetOpenTypePreconfig = 0;
        const int InternetFlagReload = unchecked((int)0x80000000);
        const int InternetFlagNoCacheWrite = 0x04000000;
        const int InternetFlagSecure = 0x00800000;
        const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/131.0 Safari/537.36";
        // Qt transparently handles compressed replies. Prefer a plain reply
        // and still decode gzip below for proxies that ignore this header.
        const string Headers = "Accept: application/json, application/rss+xml, text/xml, */*\r\nAccept-Encoding: identity\r\n";

        var session = NativeWinInet.InternetOpen(UserAgent, InternetOpenTypePreconfig, null, null, 0);
        if (session == IntPtr.Zero)
            throw new ApiException($"无法打开系统网络会话 ({Marshal.GetLastWin32Error()})");

        try
        {
            var request = NativeWinInet.InternetOpenUrl(session, url, Headers, Headers.Length,
                InternetFlagReload | InternetFlagNoCacheWrite | InternetFlagSecure, IntPtr.Zero);
            if (request == IntPtr.Zero)
                throw new ApiException($"系统网络无法访问状态页 ({Marshal.GetLastWin32Error()})");

            try
            {
                using var stream = new MemoryStream();
                var buffer = new byte[8192];
                while (NativeWinInet.InternetReadFile(request, buffer, buffer.Length, out var read) && read > 0)
                    stream.Write(buffer, 0, read);
                if (Marshal.GetLastWin32Error() != 0 && stream.Length == 0)
                    throw new ApiException($"读取状态页失败 ({Marshal.GetLastWin32Error()})");
                return DecodeResponse(stream.ToArray());
            }
            finally
            {
                NativeWinInet.InternetCloseHandle(request);
            }
        }
        finally
        {
            NativeWinInet.InternetCloseHandle(session);
        }
    }

    private static string DecodeResponse(byte[] bytes)
    {
        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var source = new MemoryStream(bytes);
            using var gzip = new GZipStream(source, CompressionMode.Decompress);
            using var decoded = new MemoryStream();
            gzip.CopyTo(decoded);
            bytes = decoded.ToArray();
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static class NativeWinInet
    {
        [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr InternetOpen(string agent, int accessType, string? proxy, string? proxyBypass, int flags);

        [DllImport("wininet.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr InternetOpenUrl(IntPtr internet, string url, string headers, int headersLength, int flags, IntPtr context);

        [DllImport("wininet.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InternetReadFile(IntPtr file, byte[] buffer, int bytesToRead, out int bytesRead);

        [DllImport("wininet.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool InternetCloseHandle(IntPtr handle);
    }

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
