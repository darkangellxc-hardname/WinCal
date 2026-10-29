using System.Windows.Controls;
using System.Windows.Input;
using WinCal.ViewModels;

namespace WinCal.Views.Controls;

/// <summary>
/// 当月放假速览条（P1-1）—— 显示「本月：国庆节 10/1–10/8 休8天 · 10/10(六) 上班」，
/// 文案由 <see cref="CalendarViewModel.HolidaySummaryText"/> 从 ICS 数据推导（无假期时外部折叠、不占高度）。
/// 点击可跳转到当月第一个假期第一天（<see cref="CalendarViewModel.NavigateToFirstHoliday"/>）。
/// </summary>
public partial class HolidaySummaryBar : UserControl
{
    /// <summary>默认构造。</summary>
    public HolidaySummaryBar()
    {
        InitializeComponent();
    }

    private void OnBarClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is CalendarViewModel vm && vm.HasHolidaySummary)
        {
            vm.NavigateToFirstHoliday();
        }
    }
}
