using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WinCal.Core.Models;
using WinCal.ViewModels;

namespace WinCal.Views.Controls;

/// <summary>
/// 日程列表中的单条事件项。保留三项既有能力：<b>hover 详情</b>、<b>左侧颜色条</b>、
/// <b>来源去重圆点/配色</b>；并让前景色改走 <see cref="FrameworkElement.SetResourceReference"/>
/// 绑定主题画刷，从而在切换主题时自动跟随（不再于 code-behind 写入一次性静态画刷）。
/// </summary>
public partial class EventItem : UserControl
{
    public EventItem()
    {
        InitializeComponent();
    }

    /// <summary>事件数据。</summary>
    public static readonly DependencyProperty EventDataProperty =
        DependencyProperty.Register(
            nameof(EventData), typeof(CalendarEvent), typeof(EventItem),
            new PropertyMetadata(null, OnEventDataChanged));

    /// <summary>绑定的事件对象。</summary>
    public CalendarEvent? EventData
    {
        get => (CalendarEvent?)GetValue(EventDataProperty);
        set => SetValue(EventDataProperty, value);
    }

    /// <summary>请求显示 / 隐藏悬停详情（参数为 null 表示隐藏）。</summary>
    public event Action<CalendarEvent?>? HoverDetailRequested;

    private static void OnEventDataChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (EventItem)d;
        if (e.NewValue is not CalendarEvent evt)
        {
            return;
        }

        // 标题后追加日期（多天事件显示范围，如 “劳动节(5月1日-5月5日)”）
        string datePart;
        var startDate = evt.StartTime.Date;
        var endDate = evt.IsAllDay ? evt.EndTime.Date.AddDays(-1) : evt.EndTime.Date;
        // 防止日期倒挂（无 DTEND 的全天事件 EndTime == StartTime，减一天后倒挂）
        if (endDate < startDate) endDate = startDate;

        if (startDate != endDate)
        {
            if (startDate.Year != endDate.Year)
            {
                datePart =
                    $"{startDate.Year}/{startDate.Month}/{startDate.Day}-{endDate.Year}/{endDate.Month}/{endDate.Day}";
            }
            else
            {
                datePart = $"{startDate.Month}月{startDate.Day}日-{endDate.Month}月{endDate.Day}日";
            }
        }
        else
        {
            datePart = $"{startDate.Month}月{startDate.Day}日";
        }
        item.TitleText.Text = $"{evt.Title}({datePart})";

        // 时间文本
        item.TimeText.Text = EventListViewModel.FormatEventSummary(evt);

        // 已结束的事件：删除线 + 降透明度；未结束：正常
        var now = DateTime.Now;
        bool isPast = evt.IsAllDay
            ? evt.EndTime.Date <= DateTime.Today
            : evt.EndTime <= now;

        if (isPast)
        {
            // 前景色仍绑定主题画刷（TextSecondaryBrush），叠加删除线 + 半透明
            item.TitleText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            item.TimeText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            item.TitleText.TextDecorations = TextDecorations.Strikethrough;
            item.TimeText.TextDecorations = TextDecorations.Strikethrough;
            item.Opacity = 0.5;
        }
        else
        {
            item.TitleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            item.TimeText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            item.TitleText.TextDecorations = null;
            item.TimeText.TextDecorations = null;
            item.Opacity = 1.0;
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        // 背景由 XAML 样式触发器（IsMouseOver）驱动；此处仅负责请求悬停详情
        HoverDetailRequested?.Invoke(EventData);
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        HoverDetailRequested?.Invoke(null);
    }
}
