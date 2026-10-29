using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using WinCal.Core.Models;

namespace WinCal.Views.Controls;

/// <summary>
/// 底部黄历详情面板（点哪天显示哪天）。
///
/// <para><b>数据来源</b>：<see cref="Almanac"/> 绑定 ViewModel 的 <c>SelectedDayAlmanac</c>，
/// 由黄历 ICS 源的 <c>DESCRIPTION</c> 解析而来；<see cref="TitleText"/> 绑定
/// <c>SelectedAlmanacTitle</c>（农历 + 干支生肖）。</para>
///
/// <para><b>绝不混入日程流</b>：本面板只消费黄历层数据，与日程开关完全无关（契约 C10）。</para>
/// </summary>
public partial class AlmanacDetailPanel : UserControl
{
    /// <summary>默认构造。</summary>
    public AlmanacDetailPanel()
    {
        InitializeComponent();
    }

    /// <summary>标题文本 DP（农历 + 干支生肖）。</summary>
    public static readonly DependencyProperty TitleTextProperty =
        DependencyProperty.Register(
            nameof(TitleText), typeof(string), typeof(AlmanacDetailPanel),
            new PropertyMetadata(string.Empty, OnTitleTextChanged));

    /// <summary>标题文本（渲染写入 <c>TitleTextBlock</c>）。</summary>
    public string TitleText
    {
        get => (string)GetValue(TitleTextProperty);
        set => SetValue(TitleTextProperty, value);
    }

    /// <summary>黄历数据 DP。</summary>
    public static readonly DependencyProperty AlmanacProperty =
        DependencyProperty.Register(
            nameof(Almanac), typeof(AlmanacInfo), typeof(AlmanacDetailPanel),
            new PropertyMetadata(null, OnAlmanacChanged));

    /// <summary>黄历数据（为 null 或 <c>HasAny==false</c> 时显示"当日无黄历数据"占位）。</summary>
    public AlmanacInfo? Almanac
    {
        get => (AlmanacInfo?)GetValue(AlmanacProperty);
        set => SetValue(AlmanacProperty, value);
    }

    private static void OnTitleTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var panel = (AlmanacDetailPanel)d;
        panel.TitleTextBlock.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnAlmanacChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((AlmanacDetailPanel)d).RenderAlmanac(e.NewValue as AlmanacInfo);
    }

    /// <summary>渲染黄历字段；无任一字段时切换为占位提示。</summary>
    private void RenderAlmanac(AlmanacInfo? info)
    {
        bool hasData = info?.HasAny == true;

        EmptyText.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;
        Separator.Visibility = hasData ? Visibility.Visible : Visibility.Collapsed;
        YiRow.Visibility = hasData && !string.IsNullOrWhiteSpace(info!.Yi)
            ? Visibility.Visible : Visibility.Collapsed;
        JiRow.Visibility = hasData && !string.IsNullOrWhiteSpace(info!.Ji)
            ? Visibility.Visible : Visibility.Collapsed;

        YiText.Text = info?.Yi ?? string.Empty;
        JiText.Text = info?.Ji ?? string.Empty;

        RenderOptionalLine(JiShenText, "🙏 吉神：", info?.JiShen);
        RenderOptionalLine(XiongShaText, "👹 凶煞：", info?.XiongSha);

        MetaText.Text = BuildMeta(info);
        MetaText.Visibility = string.IsNullOrWhiteSpace(MetaText.Text)
            ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>渲染「吉神 / 凶煞」这类可选整行（值为空则整行折叠，不留空行）。</summary>
    private static void RenderOptionalLine(TextBlock target, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            target.Visibility = Visibility.Collapsed;
            target.Text = string.Empty;
            return;
        }

        target.Text = label + value;
        target.Visibility = Visibility.Visible;
    }

    /// <summary>拼装元信息行（冲煞 / 建除 / 二十八宿 / 月相），空字段自动跳过。</summary>
    private static string BuildMeta(AlmanacInfo? info)
    {
        if (info is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(info.ChongSha)) parts.Add($"冲煞：{info.ChongSha}");
        if (!string.IsNullOrWhiteSpace(info.JianChu)) parts.Add($"建除：{info.JianChu}");
        if (!string.IsNullOrWhiteSpace(info.Xiu)) parts.Add($"宿：{info.Xiu}");
        if (!string.IsNullOrWhiteSpace(info.MoonPhase)) parts.Add($"月相：{info.MoonPhase}");
        return string.Join(" · ", parts);
    }
}
