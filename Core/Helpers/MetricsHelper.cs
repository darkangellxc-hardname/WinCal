using System.Collections.ObjectModel;
using System.Windows;

namespace WinCal.Core.Helpers;

/// <summary>
/// 响应式规格档位（P1-4）。按面板宽度切换字号 / 尺寸资源字典，<b>不做逐像素连续缩放</b>
/// （会造成文字发虚，见 PRD §6.4「分档实现」）。
/// </summary>
public enum MetricsTier
{
    /// <summary>紧凑档（宽度 &lt; 380）。</summary>
    Compact = 0,

    /// <summary>标准档（380–479，默认 420）。</summary>
    Standard = 1,

    /// <summary>宽松档（宽度 ≥ 480）。</summary>
    Wide = 2
}

/// <summary>
/// 响应式字号/尺寸切换帮助类 —— <see cref="ThemeHelper"/> 的镜像实现（架构 §8⑤）。
///
/// <para>监听面板实际宽度，按 <see cref="MetricsTier"/> 阈值切换
/// <c>Views/Themes/Metrics.{Compact,Standard,Wide}.xaml</c> 资源字典；
/// XAML 侧以 <c>{DynamicResource CellDayFontSize}</c> 等键消费，键不变、值随档位变。</para>
/// </summary>
public static class MetricsHelper
{
    /// <summary>紧凑档与标准档的宽度分界（DIP）。</summary>
    public const double CompactMaxWidth = 380.0;

    /// <summary>标准档与宽松档的宽度分界（DIP）。</summary>
    public const double WideMinWidth = 480.0;

    private static readonly string CompactUri = "Views/Themes/Metrics.Compact.xaml";
    private static readonly string StandardUri = "Views/Themes/Metrics.Standard.xaml";
    private static readonly string WideUri = "Views/Themes/Metrics.Wide.xaml";

    /// <summary>当前生效的档位。</summary>
    public static MetricsTier CurrentTier { get; private set; } = MetricsTier.Standard;

    /// <summary>
    /// 依据宽度计算档位（纯函数，便于单测）。
    /// </summary>
    /// <param name="width">面板实际宽度（DIP）。</param>
    public static MetricsTier TierForWidth(double width)
    {
        if (width < CompactMaxWidth)
        {
            return MetricsTier.Compact;
        }

        return width >= WideMinWidth ? MetricsTier.Wide : MetricsTier.Standard;
    }

    /// <summary>
    /// 应用给定宽度对应的档位。宽度无效（≤0）时不切换，保留当前档。
    /// </summary>
    /// <param name="width">面板实际宽度（DIP）。</param>
    public static void ApplyTierForWidth(double width)
    {
        if (width <= 0)
        {
            return;
        }

        ApplyTier(TierForWidth(width));
    }

    /// <summary>
    /// 切换档位资源字典。若目标档与当前档相同，则不改动（避免频繁替换字典）。
    /// </summary>
    /// <param name="tier">目标档位。</param>
    public static void ApplyTier(MetricsTier tier)
    {
        var app = Application.Current;
        if (app == null)
        {
            return;
        }

        var dicts = app.Resources.MergedDictionaries;

        // 已生效且档位一致 → 跳过（幂等，避免每次 SizeChanged 都替换字典）
        if (CurrentTier == tier && HasTierDictionary(dicts, tier))
        {
            return;
        }

        // 移除旧档（Source 会变成 pack:// URI，用 Contains 匹配）
        for (var i = dicts.Count - 1; i >= 0; i--)
        {
            var source = dicts[i].Source?.OriginalString;
            if (IsMetricsSource(source))
            {
                dicts.RemoveAt(i);
            }
        }

        var uri = tier switch
        {
            MetricsTier.Compact => CompactUri,
            MetricsTier.Wide => WideUri,
            _ => StandardUri
        };

        dicts.Add(new ResourceDictionary
        {
            Source = new Uri(uri, UriKind.Relative)
        });

        CurrentTier = tier;
    }

    private static bool IsMetricsSource(string? source)
        => source != null && source.Contains("Metrics.");

    private static bool HasTierDictionary(
        System.Collections.ObjectModel.Collection<ResourceDictionary> dicts, MetricsTier tier)
    {
        var token = tier switch
        {
            MetricsTier.Compact => "Metrics.Compact.xaml",
            MetricsTier.Wide => "Metrics.Wide.xaml",
            _ => "Metrics.Standard.xaml"
        };

        return dicts.Any(d => d.Source?.OriginalString?.Contains(token) == true);
    }
}
