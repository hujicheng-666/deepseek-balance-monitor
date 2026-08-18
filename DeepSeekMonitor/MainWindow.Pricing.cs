using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeepSeekMonitor.Models;

namespace DeepSeekMonitor;

/// <summary>
/// 模型定价页(partial,与 MainWindow.xaml.cs 共享同一类)。
/// 优先实时抓取官方定价页,抓取失败时回退到 Models/ModelPricing.cs 里的内置快照。
/// </summary>
public partial class MainWindow
{
    /// <summary>卡片当前展示的页面：余额 / 服务事件 / 模型定价。</summary>
    private enum CardView { Balance, Service, Pricing }

    private CardView _view;
    private bool _loadingPricing;                // 防止定价页刷新重入
    private PricingSnapshot? _lastPricing;       // 最近一次成功抓取的官方定价

    // ---------------- 卡片页切换（余额 / 服务 / 定价） ----------------

    /// <summary>切换卡片页面；再次点击当前页的按钮则回到余额页。</summary>
    private void ShowView(CardView view)
    {
        if (_view == view)
        {
            ShowView(CardView.Balance);
            return;
        }

        _view = view;
        var balance = view == CardView.Balance;
        var service = view == CardView.Service;
        BalanceText.Visibility = balance ? Visibility.Visible : Visibility.Collapsed;
        BalanceNote.Visibility = balance ? Visibility.Visible : Visibility.Collapsed;
        BottomRow.Visibility = balance ? Visibility.Visible : Visibility.Collapsed;
        ServicePanel.Visibility = service ? Visibility.Visible : Visibility.Collapsed;
        PricingPanel.Visibility = !balance && !service ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = view switch
        {
            CardView.Service => "DeepSeek 服务",
            CardView.Pricing => "模型定价",
            _ => "DeepSeek",
        };
        BtnService.Content = service ? "‹" : "◉";
        BtnService.ToolTip = service ? "返回余额" : "查看 DeepSeek 服务状态";
        BtnPricing.Content = view == CardView.Pricing ? "‹" : "$";
        BtnPricing.ToolTip = view == CardView.Pricing ? "返回余额" : "查看模型定价";

        if (service)
            CheckServiceStatus();
        else if (view == CardView.Pricing)
            RenderPricing();
        else
            SetStatus(string.IsNullOrWhiteSpace(_config.ApiKey) ? "no_key" : "ok");
    }

    // ---------------- 模型定价视图 ----------------

    /// <summary>先展示已有数据（上次抓取或内置快照），再异步刷新官方价格。</summary>
    private void RenderPricing()
    {
        RenderPricingSnapshot(_lastPricing ?? ModelPricing.Fallback, live: _lastPricing != null);
        if (_loadingPricing) return;
        _loadingPricing = true;
        _ = LoadPricingAsync();
    }

    private async Task LoadPricingAsync()
    {
        try
        {
            var snapshot = await _api.FetchPricingAsync();
            _lastPricing = snapshot;
            if (_view == CardView.Pricing)
                RenderPricingSnapshot(snapshot, live: true);
        }
        catch
        {
            // 抓取失败：保留上次成功数据或内置快照，并标注离线。
            if (_view == CardView.Pricing)
            {
                RenderPricingSnapshot(_lastPricing ?? ModelPricing.Fallback, live: _lastPricing != null);
                PricingMeta.Text = _lastPricing != null
                    ? $"$ / 百万 tokens · 官方页暂不可达，显示上次更新 {_lastPricing.FetchedAt:MM-dd HH:mm}"
                    : $"$ / 百万 tokens · 官方页暂不可达，离线快照 {ModelPricing.Fallback.FetchedAt:yyyy-MM-dd}";
            }
        }
        finally
        {
            _loadingPricing = false;
        }
    }

    private void RenderPricingSnapshot(PricingSnapshot snapshot, bool live)
    {
        SpPricing.Children.Clear();
        // ScrollViewer 内容在首次布局前宽度未约束，TextBlock 的 Wrap 可能不生效导致横向溢出；
        // 显式限宽（基于视口实际宽度），保证任何情况下都不左右截断。
        var maxW = SvPricing.ViewportWidth > 0 ? SvPricing.ViewportWidth - 7 : 296;

        foreach (var p in snapshot.Models)
        {
            SpPricing.Children.Add(new TextBlock
            {
                Text = $"{p.ApiName} · {p.BaseModel}",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                Foreground = Brush("#E9EBF0"), // 柔和白，避免纯白刺眼
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = maxW,
                Margin = new Thickness(0, 0, 4, 2),
            });

            var peakIn = $"{Price(p.PeakCacheHit)}/{Price(p.PeakCacheMiss)}";
            var offIn = $"{Price(p.OffPeakCacheHit)}/{Price(p.OffPeakCacheMiss)}";
            string body;
            if (snapshot.IsPeakNow == true)
                body = $"● 当前高峰 输入 ${peakIn}（命中/未命中）· 输出 ${Price(p.PeakOutput)}\n非高峰 输入 ${offIn} · 输出 ${Price(p.OffPeakOutput)}";
            else if (snapshot.IsPeakNow == false)
                body = $"● 当前非高峰 输入 ${offIn}（命中/未命中）· 输出 ${Price(p.OffPeakOutput)}\n高峰 输入 ${peakIn} · 输出 ${Price(p.PeakOutput)}";
            else
                body = $"高峰 输入 ${peakIn}（命中/未命中）· 输出 ${Price(p.PeakOutput)}\n非高峰 输入 ${offIn} · 输出 ${Price(p.OffPeakOutput)}";
            if (!string.IsNullOrWhiteSpace(p.Concurrency))
                body += $"\n并发上限 {p.Concurrency}";

            SpPricing.Children.Add(new TextBlock
            {
                Text = body,
                FontSize = 10,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                Foreground = SubBrush,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = maxW,
                Margin = new Thickness(0, 0, 4, 12),
            });
        }

        var when = live ? $"更新于 {DateTime.Now:HH:mm}" : $"收录于 {snapshot.FetchedAt:yyyy-MM-dd}";
        var peak = string.IsNullOrEmpty(snapshot.PeakHoursDisplay) ? "" : $" · 高峰 {snapshot.PeakHoursDisplay}";
        PricingMeta.Text = $"$ / 百万 tokens · {when}{peak}";
    }

    private static string Price(double value)
        => value.ToString("0.######");

    private void BtnPricing_Click(object sender, RoutedEventArgs e) => ShowView(CardView.Pricing);

    private void MenuPricing_Click(object sender, RoutedEventArgs e) => ShowView(CardView.Pricing);

    private void PricingLink_Click(object sender, MouseButtonEventArgs e) => OpenPricingPage();

    private void OpenPricingPage()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ModelPricing.OfficialPage) { UseShellExecute = true });
        }
        catch { /* 打不开浏览器时静默忽略 */ }
    }
}
