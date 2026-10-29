using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WinCal.ViewModels;

namespace WinCal.Views.Controls;

/// <summary>
/// 月历控件（T03 重做）—— <b>导航按钮已上移到面板 Header</b>，本控件只负责：
/// 星期头显示、6×7 网格渲染、格子点击选中。
///
/// <para>保留原有优点：星期头动态更新（支持周一起始）、点击选中联动、非当月灰显由
/// <see cref="DayCell"/> 处理。</para>
/// </summary>
public partial class MonthCalendar : UserControl
{
    /// <summary>默认构造。</summary>
    public MonthCalendar()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;

        // 在控件级别捕获点击（比单格绑定更可靠，保留原有实现）
        PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
    }

    /// <summary>当前绑定的 ViewModel（可能为 null）。</summary>
    private CalendarViewModel? Vm => DataContext as CalendarViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BindToViewModel();

        // 首次加载时按设置的周起始日刷新星期头
        if (Vm is { } vm)
        {
            UpdateWeekHeaders(vm.WeekStartDay);
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        BindToViewModel();
    }

    /// <summary>把网格 ItemsSource 接到 ViewModel，并监听集合重建以同步选中态。</summary>
    private void BindToViewModel()
    {
        if (Vm is not { } vm)
        {
            return;
        }

        CalendarGrid.ItemsSource = vm.CalendarDays;
        UpdateWeekHeaders(vm.WeekStartDay);

        vm.CalendarDays.CollectionChanged -= OnDaysChanged;
        vm.CalendarDays.CollectionChanged += OnDaysChanged;

        // 绑定后若已有数据（构造期 NavigateToToday 已生成首帧），同步选中态
        if (vm.SelectedDayIndex >= 0)
        {
            ApplySelection(vm.SelectedDayIndex);
        }
    }

    private void OnDaysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // 网格重建（翻月 / 刷新）后同步选中态与星期头
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Vm is { } vm)
            {
                UpdateWeekHeaders(vm.WeekStartDay);
                ApplySelection(vm.SelectedDayIndex);
            }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// 更新星期标题行（支持周一起始或周日起始）。
    /// </summary>
    /// <param name="weekStartDay">0=周日，1=周一。</param>
    public void UpdateWeekHeaders(int weekStartDay)
    {
        string[] monStart = { "一", "二", "三", "四", "五", "六", "日" };
        string[] sunStart = { "日", "一", "二", "三", "四", "五", "六" };
        var headers = weekStartDay == 1 ? monStart : sunStart;

        var textBlocks = new[] { H0, H1, H2, H3, H4, H5, H6 };
        for (int i = 0; i < 7; i++)
        {
            textBlocks[i].Text = headers[i];
        }
    }

    private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var hitResult = VisualTreeHelper.HitTest(this, e.GetPosition(this));
        if (hitResult == null)
        {
            return;
        }

        var dayCell = FindAncestor<DayCell>(hitResult.VisualHit);
        if (dayCell?.DayData is { } dayData && Vm is { } vm)
        {
            ClearAllSelections();
            dayCell.IsSelected = true;
            vm.SelectDate(dayData.Date);
        }
    }

    /// <summary>按索引高亮对应格子（清除其它格子的选中态）。</summary>
    private void ApplySelection(int index)
    {
        ClearAllSelections();
        if (index < 0)
        {
            return;
        }

        var cell = FindCellAt(index);
        if (cell != null)
        {
            cell.IsSelected = true;
        }
    }

    /// <summary>按线性索引取得网格中的 DayCell。</summary>
    private DayCell? FindCellAt(int index)
    {
        var panel = FindItemsPanel(CalendarGrid);
        if (panel == null || index < 0 || index >= panel.Children.Count)
        {
            return null;
        }

        // ItemsControl 生成的容器是 ContentPresenter，DayCell 在其可视子树内
        var container = panel.Children[index];
        return container as DayCell ?? FindDescendant<DayCell>(container);
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
            {
                return match;
            }

            var found = FindDescendant<T>(child);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static Panel? FindItemsPanel(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Panel panel)
            {
                return panel;
            }

            var found = FindItemsPanel(child);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T ancestor)
            {
                return ancestor;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>清除所有 DayCell 的选中状态。</summary>
    private void ClearAllSelections()
    {
        ClearAllSelectionsRecursive(CalendarGrid);
    }

    private void ClearAllSelectionsRecursive(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is DayCell dayCell)
            {
                dayCell.IsSelected = false;
            }
            else
            {
                ClearAllSelectionsRecursive(child);
            }
        }
    }
}
