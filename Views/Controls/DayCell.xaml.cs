using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinCal.Core.Models;

namespace WinCal.Views.Controls;

/// <summary>
/// 日期格子控件（PRD §6.2 重做）。
///
/// <para><b>四态叠加（互不排斥）：</b></para>
/// <list type="number">
///   <item>普通日：透明底 + 农历日名（灰）。</item>
///   <item>法定节假日「休」：整格浅暖底 + 节日名（红）+ 右下「休」胶囊。<b>优先级最高</b>。</item>
///   <item>调休上班「班」：整格浅蓝灰底 + 右下「班」胶囊。</item>
///   <item>今天：日期数字后加 ⌀30 强调色圆底（白字），叠加标记不清除。</item>
/// </list>
///
/// <para><b>次级文字显示优先级（同一行只显示优先级最高的一项）：</b>
/// ① 法定节假日名（红）→ ② 节气（绿）→ ③ 农历节日（橙）→ ④ 农历日名（灰）。</para>
///
/// <para><b>宁缺勿错（P0-5 验收 5）：</b>无法判定休/班时仅显示节日名、不打标记（<see cref="DayKind.None"/>）。</para>
/// </summary>
public partial class DayCell : UserControl
{
    /// <summary>默认构造。</summary>
    public DayCell()
    {
        InitializeComponent();
    }

    /// <summary>绑定的日历日数据。</summary>
    public static readonly DependencyProperty DayDataProperty =
        DependencyProperty.Register(
            nameof(DayData), typeof(CalendarDay), typeof(DayCell),
            new PropertyMetadata(null, OnDayDataChanged));

    /// <summary>日历日聚合数据。</summary>
    public CalendarDay? DayData
    {
        get => (CalendarDay?)GetValue(DayDataProperty);
        set => SetValue(DayDataProperty, value);
    }

    /// <summary>是否被选中。</summary>
    public static readonly DependencyProperty IsSelectedProperty =
        DependencyProperty.Register(
            nameof(IsSelected), typeof(bool), typeof(DayCell),
            new PropertyMetadata(false, OnIsSelectedChanged));

    /// <summary>是否选中。</summary>
    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    /// <summary>日期被点击时触发的事件（保留原有能力）。</summary>
    public event RoutedEventHandler? DayClicked;

    // ==================================================================
    // 渲染
    // ==================================================================

    private static void OnDayDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var cell = (DayCell)d;
        if (e.NewValue is not CalendarDay day)
        {
            return;
        }

        cell.RenderDay(day);
    }

    /// <summary>依据日历日数据渲染整格。</summary>
    private void RenderDay(CalendarDay day)
    {
        // 1) 休 / 班 整格底色（休优先）
        var bg = day.MarkKind switch
        {
            DayKind.Holiday => FindBrush("HolidayBrush"),
            DayKind.Workday => FindBrush("WorkdayBrush"),
            _ => null
        };
        CellBackground.Background = bg ?? Brushes.Transparent;

        // 2) 休 / 班 胶囊（紧凑档隐藏文字：CapsuleOpacity=0 时只留底色块）
        RenderCapsule(day.MarkKind);

        // 3) 日期数字
        DayText.Text = day.Date.Day.ToString();

        // 4) 今日圆底
        TodayCircle.Visibility = day.IsToday ? Visibility.Visible : Visibility.Collapsed;

        // 5) 日期数字颜色（优先级：今日 > 选中 > 休/班/当月 > 非当月 > 周末）
        DayText.Foreground = ResolveDayForeground(day, IsSelected);
        DayText.FontWeight = day.IsToday ? FontWeights.Bold : FontWeights.SemiBold;

        // 6) 次级文字 + 颜色（优先级 ① 节假日名 → ② 节气 → ③ 农历节日 → ④ 农历日名）
        RenderSecondaryText(day);

        // 7) 事件圆点（仅日程开关开启时由 VM 决定 HasEvents）
        RenderEventDots(day);
    }

    /// <summary>日期数字前景色。</summary>
    private Brush ResolveDayForeground(CalendarDay day, bool isSelected)
    {
        if (day.IsToday)
        {
            return FindBrush("TextOnAccentBrush") ?? Brushes.White;
        }

        if (isSelected)
        {
            return FindBrush("TodayAccentBrush") ?? Brushes.DodgerBlue;
        }

        if (!day.IsCurrentMonth)
        {
            return FindBrush("TextDisabledBrush") ?? Brushes.Gray;
        }

        // 周末弱区分（P1-3）：周六 / 周日略降饱和（不改变底色，避免与「休」混淆）
        bool isWeekend = day.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        if (isWeekend)
        {
            return FindBrush("WeekendTextBrush") ?? Brushes.Gray;
        }

        return FindBrush("TextPrimaryBrush") ?? Brushes.Black;
    }

    /// <summary>渲染次级文字（含优先级与着色）。</summary>
    private void RenderSecondaryText(CalendarDay day)
    {
        // ① 法定节假日名（红）
        if (!string.IsNullOrEmpty(day.HolidayName))
        {
            SetSecondaryText(day.HolidayName!, FindBrush("HolidayTextBrush"));
            return;
        }

        // ② 节气（绿）
        if (!string.IsNullOrEmpty(day.SolarTerm))
        {
            SetSecondaryText(day.SolarTerm!, FindBrush("SolarTermTextBrush"));
            return;
        }

        // ③ 农历节日（橙）
        if (!string.IsNullOrEmpty(day.LunarFestival))
        {
            SetSecondaryText(day.LunarFestival!, FindBrush("LunarFestivalTextBrush"));
            return;
        }

        // ④ 农历日名（灰，TextSecondary；非当月沿用已降饱和处理）
        if (!string.IsNullOrEmpty(day.LunarDate))
        {
            SetSecondaryText(day.LunarDate!, FindBrush("TextSecondaryBrush"));
            return;
        }

        LunarText.Visibility = Visibility.Collapsed;
    }

    private void SetSecondaryText(string text, Brush? brush)
    {
        LunarText.Text = text;
        LunarText.Foreground = brush ?? (FindBrush("TextSecondaryBrush") ?? Brushes.Gray);
        LunarText.Visibility = Visibility.Visible;
    }

    /// <summary>渲染休 / 班 胶囊。</summary>
    private void RenderCapsule(DayKind kind)
    {
        if (kind == DayKind.None)
        {
            Capsule.Visibility = Visibility.Collapsed;
            return;
        }

        Capsule.Visibility = Visibility.Visible;
        Capsule.Background = kind == DayKind.Holiday
            ? (FindBrush("CapsuleHolidayBrush") ?? Brushes.Orange)
            : (FindBrush("CapsuleWorkdayBrush") ?? Brushes.SteelBlue);
        CapsuleText.Text = kind == DayKind.Holiday ? "休" : "班";

        // 紧凑档：隐藏胶囊文字（仅保留底色块），由 Metrics 资源 CapsuleOpacity 控制
        double opacity = TryFindDoubleResource("CapsuleOpacity", 1.0);
        CapsuleText.Opacity = opacity;
    }

    /// <summary>渲染事件圆点（按颜色去重，最多 3 个；紧凑档尺寸为 0 → 不可见）。</summary>
    private void RenderEventDots(CalendarDay day)
    {
        // 紧凑档：事件圆点尺寸 0 → 直接隐藏
        double dotSize = TryFindDoubleResource("CellEventDotSize", 4.0);

        if (!day.HasEvents || day.Events.Count == 0 || dotSize <= 0)
        {
            EventDots.Visibility = Visibility.Collapsed;
            Dot1.Visibility = Visibility.Collapsed;
            Dot2.Visibility = Visibility.Collapsed;
            Dot3.Visibility = Visibility.Collapsed;
            return;
        }

        var distinctColors = day.Events
            .Select(e => e.Color)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct()
            .Take(3)
            .ToList();

        EventDots.Visibility = Visibility.Visible;
        var dots = new[] { Dot1, Dot2, Dot3 };
        for (int i = 0; i < dots.Length; i++)
        {
            if (i < distinctColors.Count)
            {
                dots[i].Visibility = Visibility.Visible;
                try
                {
                    var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(distinctColors[i])!);
                    brush.Freeze();
                    dots[i].Fill = brush;
                }
                catch
                {
                    dots[i].Fill = FindBrush("EventDotBrush") ?? Brushes.DodgerBlue;
                }
            }
            else
            {
                dots[i].Visibility = Visibility.Collapsed;
            }
        }
    }

    private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var cell = (DayCell)d;
        if (cell.DayData is not { } day)
        {
            return;
        }

        bool isSelected = (bool)e.NewValue;

        // 选中描边：今日已有填充圆，避免视觉冲突（今日仅保留填充圆）
        cell.SelectedBorder.Visibility = isSelected && !day.IsToday
            ? Visibility.Visible
            : Visibility.Collapsed;

        cell.DayText.Foreground = cell.ResolveDayForeground(day, isSelected);
    }

    private void OnDayClick(object sender, MouseButtonEventArgs e)
    {
        DayClicked?.Invoke(this, new RoutedEventArgs { Source = this });
    }

    // ==================================================================
    // 资源查找
    // ==================================================================

    private static Brush? FindBrush(string key)
        => Application.Current?.TryFindResource(key) as Brush;

    /// <summary>读取 double 资源（如 CapsuleOpacity / CellEventDotSize），缺失时用默认值。</summary>
    private static double TryFindDoubleResource(string key, double fallback)
    {
        var value = Application.Current?.TryFindResource(key);
        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => fallback
        };
    }
}
