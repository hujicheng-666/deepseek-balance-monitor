using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeepSeekMonitor.Models;

namespace DeepSeekMonitor;

/// <summary>
/// 模型定价页(partial,与 MainWindow.xaml.cs 共享同一类)。
/// 数据来自 Models/ModelPricing.cs 里的官方定价快照,底部可打开官网核对。
/// </summary>
public partial class MainWindow
{
    /// <summary>卡片当前展示的页面：余额 / 服务事件 / 模型定价。</summary>
    private enum CardView { Balance, Service, Pricing }

    private CardView _view;

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

    private void RenderPricing()
    {
        SpPricing.Children.Clear();
        // ScrollViewer 内容在首次布局前宽度未约束，TextBlock 的 Wrap 可能不生效导致横向溢出；
        // 显式限宽（基于视口实际宽度），保证任何情况下都不左右截断。
        var maxW = SvPricing.ViewportWidth > 0 ? SvPricing.ViewportWidth - 7 : 296;
        foreach (var p in ModelPricing.All)
        {
            // 与服务状态页一致的纯文本列表：无卡片框、可滚动、不截断
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
            SpPricing.Children.Add(new TextBlock
            {
                Text = $"现行：输入 ${Price(p.InputCacheHit)}/${Price(p.InputCacheMiss)}（命中/未命中）· 输出 ${Price(p.Output)}\n" +
                       $"{ModelPricing.PeakOffPeakEffective} 起峰谷：高峰 输入 ${Price(p.PeakInputCacheHit)}/${Price(p.PeakInputCacheMiss)} 输出 ${Price(p.PeakOutput)} · 非高峰 输入 ${Price(p.OffPeakInputCacheHit)}/${Price(p.OffPeakInputCacheMiss)} 输出 ${Price(p.OffPeakOutput)}",
                FontSize = 10,
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                Foreground = SubBrush,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = maxW,
                Margin = new Thickness(0, 0, 4, 12),
            });
        }

        PricingMeta.Text = $"$ / 百万 tokens · 收录于 {ModelPricing.UpdatedAt}";
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
