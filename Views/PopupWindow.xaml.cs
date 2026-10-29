using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using WinCal.Core.Helpers;
using WinCal.Core.Interop;
using WinCal.Core.Models;
using WinCal.Core.Services;
using WinCal.ViewModels;
using WinCal.Views.Controls;

namespace WinCal.Views;

/// <summary>
/// 日历主面板（T03 整体重做）。
///
/// <para><b>结构（PRD §6.1）：</b>诚实横幅（可折叠）+ Header(44) + 星期头(24) + 月历网格(*)
/// + 放假速览条（可折叠）+ 日程三态区。</para>
///
/// <para><b>关键改造：</b></para>
/// <list type="bullet">
///   <item>移除 <c>SizeToContent</c>（与自由缩放硬冲突），默认 420×620、最小 320×420。</item>
///   <item>改用 <see cref="PanelResizeHelper"/> 手工缩放（不使用 <c>WindowChrome</c>），右下角锚点固定并持久化尺寸。</item>
///   <item>保留 9 项优点：今日高亮、非当月灰显、主题跟随、圆角、<c>ClipToBounds</c>、
///         <see cref="StartFocusTracking"/> 焦点丢失自动关闭、事件 hover 详情。</item>
/// </list>
/// </summary>
public partial class PopupWindow : Window
{
    /// <summary>最小宽度（DIP）。</summary>
    private const double MinPanelWidth = 320;

    /// <summary>最小高度（DIP）。加入黄历面板后需保证 6 行网格每格 ≥ 49px。</summary>
    private const double MinPanelHeight = 576;

    private readonly CalendarViewModel _calendarViewModel;
    private readonly AppSettings _settings;
    private EventDetailWindow? _detailWindow;
    private DispatcherTimer? _clockTimer;

    /// <summary>面板是否已固定到屏幕（固定后失焦 / 前台窗口变化不自动隐藏）。</summary>
    private bool _isPinned;

    /// <summary>默认构造。</summary>
    public PopupWindow()
    {
        _settings = AppSettings.Load();
        _calendarViewModel = new CalendarViewModel(_settings);

        InitializeComponent();

        DataContext = _calendarViewModel;

        // 从设置恢复固定态（每次显示都是新建实例，故此处初始化即最终态）
        _isPinned = _settings.PinPanel;

        // 绑定事件列表
        EventListControl.ItemsSource = _calendarViewModel.UpcomingEvents;
        _calendarViewModel.UpcomingEvents.CollectionChanged += (_, _) => UpdateNoEventsVisibility();

        // 恢复上次保存的面板尺寸（关闭重开后完全恢复，P0-3 验收 4）
        RestorePanelSize();

        Loaded += OnLoaded;

        // 仅用于刷新 Header 时间，不再依赖窗口尺寸变化重定位（缩放由 PanelResizeHelper 处理）
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => _calendarViewModel.RefreshHeaderTime();

        // 手工缩放：右下角锚点固定 + 结束 500ms 防抖写盘
        PanelResizeHelper.Attach(this, MinPanelWidth, MinPanelHeight, SavePanelSize);

        // 鼠标移动显示事件详情浮层
        PreviewMouseMove += OnPreviewMouseMove;

        // 抓手 hover 提升饱和度
        MouseMove += OnGripHover;

        // 面板被任何路径隐藏时收起年月浮层（含 PopupController 直接调 Hide()）
        IsVisibleChanged += (_, _) => { if (!IsVisible) { HidePicker(); } };

        // Esc 关闭年月浮层
        KeyDown += OnWindowKeyDown;
    }

    // ==================================================================
    // 尺寸持久化
    // ==================================================================

    /// <summary>从设置恢复面板尺寸（带最小值保护）。</summary>
    private void RestorePanelSize()
    {
        Width = Math.Max(MinPanelWidth, _settings.PanelWidth);
        Height = Math.Max(MinPanelHeight, _settings.PanelHeight);
    }

    /// <summary>持久化面板尺寸（由 <see cref="PanelResizeHelper"/> 防抖后回调）。</summary>
    private void SavePanelSize(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        _settings.PanelWidth = width;
        _settings.PanelHeight = height;
        _settings.Save();
    }

    // ==================================================================
    // 生命周期
    // ==================================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplySettings();
        UpdateNoEventsVisibility();
        UpdatePinVisual();

        // ★ 时序修复（方案 C，2026-09-20 探针实证）：
        //   不再在此处直接调 AdjustHeightForSchedule —— Show() 会【同步】触发 Loaded，
        //   此刻窗口仍停在级联出生位（默认主屏，即使目标是副屏），会按错误显示器计算。
        //   改由装配层在 PositionNearTaskbar 完成后显式调用 ApplyInitialScheduleLayout()。
        //   （探针证据：Probe8 证明 Show() 同步触发 Loaded；Probe16 快照显示 Loaded 时窗口仍在
        //   主屏出生位；Probe17 统计真实顺序下新旧算法同为 6/12 正确 = 非确定性竞态。）

        _clockTimer?.Start();
    }

    /// <summary>
    /// 装配层在 <see cref="WinCal.Core.Helpers.WindowPositionHelper.PositionNearTaskbar"/> 完成后调用：
    /// 若日程区处于「启用 + 展开」态，按窗口<b>当前（已定位）所在屏</b>补做向上生长布局。
    ///
    /// <para><b>为何需要这个入口（方案 C）：</b><see cref="AdjustHeightForSchedule"/> 按「窗口当前所在屏」
    /// 计算，必须<b>在定位之后</b>调用；而 <c>Show()</c> 同步触发的 <c>OnLoaded</c> 早于定位，
    /// 时机不对。故把「展开态布局」的触发时机交给装配层（定位完成后）。</para>
    ///
    /// <para><b>幂等性说明：</b>本方法<b>非幂等</b>——<see cref="AdjustHeightForSchedule(bool)"/> 每次
    /// 调用都会在当前高度上 ±176 DIP，故<b>只能在窗口刚显示（未展开态）后调用一次</b>；
    /// 面板已显示且已展开的分支重复调用会导致高度翻倍，禁止调用。</para>
    /// </summary>
    public void ApplyInitialScheduleLayout()
    {
        if (_calendarViewModel.EnableSchedule && _calendarViewModel.ScheduleExpanded)
        {
            AdjustHeightForSchedule(true);
        }
        // 条件不满足（未启用 / 未展开）时为纯 no-op，无副作用（不移动、不改高）。
    }

    /// <summary>应用用户设置到界面。</summary>
    private void ApplySettings()
    {
        // #1 主题（保留：深浅色跟随系统）
        ThemeHelper.ApplyTheme(_settings.ThemeMode);

        // 响应式规格档（P1-4）：按当前宽度应用一次
        MetricsHelper.ApplyTierForWidth(ActualWidth > 0 ? ActualWidth : Width);

        // #2 字体大小偏移
        ApplyFontSizeOffset();

        // #10 Header 时间
        _calendarViewModel.RefreshHeaderTime();

        // #6 周起始日（单一数据源已由 VM 从设置读取，星期头同步刷新）
        Calendar.UpdateWeekHeaders(_calendarViewModel.WeekStartDay);

        _ = _calendarViewModel.RefreshDataAsync();
    }

    /// <summary>应用字体大小偏移（基于基准字号 + offset，用 ScaleTransform 缩放整面板）。</summary>
    private void ApplyFontSizeOffset()
    {
        var offset = _settings.FontSizeOffset;
        if (offset == 0)
        {
            RootBorder.LayoutTransform = Transform.Identity;
            return;
        }

        // offset 范围 -2~+2，每级缩放 6%
        var scale = 1.0 + offset * 0.06;
        RootBorder.LayoutTransform = new ScaleTransform(scale, scale);
    }

    // ==================================================================
    // 响应式规格（宽度变化时切换档位）
    // ==================================================================

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        MetricsHelper.ApplyTierForWidth(sizeInfo.NewSize.Width);
    }

    // ==================================================================
    // 焦点丢失自动关闭（保留：StartFocusTracking，不删除）
    // ==================================================================

    private DispatcherTimer? _focusTimer;
    private IntPtr _initialForegroundWindow;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    private const uint GW_OWNER = 4;

    /// <summary>
    /// 启动焦点丢失检测（保留原有能力）。
    /// 策略：记录弹出时的前台窗口，只有当前台窗口变为其它窗口时才关闭。
    /// 固定态下直接返回，不启用焦点跟踪。
    /// </summary>
    public void StartFocusTracking()
    {
        if (_isPinned)
        {
            return; // 固定态：不启用焦点跟踪
        }

        StopFocusTracking();

        _initialForegroundWindow = GetForegroundWindow();

        _focusTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _focusTimer.Tick += OnFocusCheckTick;
        _focusTimer.Start();
    }

    private void StopFocusTracking()
    {
        if (_focusTimer != null)
        {
            _focusTimer.Stop();
            _focusTimer.Tick -= OnFocusCheckTick;
            _focusTimer = null;
        }
    }

    private void OnFocusCheckTick(object? sender, EventArgs e)
    {
        if (_isPinned)
        {
            return; // 固定态：防御性短路（正常已停表，不触发）
        }

        var foreground = GetForegroundWindow();
        var myHwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        if (foreground == myHwnd)
        {
            return;
        }

        var owner = GetWindow(foreground, GW_OWNER);
        if (owner == myHwnd)
        {
            return;
        }

        if (foreground == _initialForegroundWindow)
        {
            return;
        }

        CloseDetailWindow();
        Hide();
        StopFocusTracking();
    }

    /// <summary>失焦自动关闭（定时器检测的补充）；固定态下只收起 hover 详情、不关闭面板。</summary>
    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);

        // hover 详情浮层仍随失焦收起（与固定无关）
        CloseDetailWindow();

        if (_isPinned)
        {
            return; // 固定态：失焦不关闭面板
        }

        Hide();
        StopFocusTracking();
    }

    // ==================================================================
    // 面板固定（Pin）
    // ==================================================================

    /// <summary>切换固定态并持久化；固定时停止焦点跟踪，取消固定时恢复。</summary>
    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        _settings.PinPanel = _isPinned;
        _settings.Save();

        UpdatePinVisual();

        if (_isPinned)
        {
            StopFocusTracking();
        }
        else
        {
            StartFocusTracking();
        }
    }

    /// <summary>刷新图钉图标与提示（未固定=次级色；已固定=强调色 + SelectedBrush 底衬）。</summary>
    private void UpdatePinVisual()
    {
        PinIcon.Visibility = _isPinned ? Visibility.Collapsed : Visibility.Visible;
        PinIconActive.Visibility = _isPinned ? Visibility.Visible : Visibility.Collapsed;
        PinButton.ToolTip = _isPinned ? "取消固定（点击别处将关闭）" : "固定到屏幕（点击别处不关闭）";

        // 主题无 TransparentBrush 资源，未固定态直接赋 Brushes.Transparent（避免引用不存在资源）
        if (_isPinned)
        {
            PinButton.SetResourceReference(BackgroundProperty, "SelectedBrush");
        }
        else
        {
            PinButton.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    // ==================================================================
    // 事件详情浮层（保留：hover 详情）
    // ==================================================================

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        var hitResult = VisualTreeHelper.HitTest(this, e.GetPosition(this));
        if (hitResult == null)
        {
            CloseDetailWindow();
            return;
        }

        if (IsMouseOverSettingsButton(hitResult.VisualHit))
        {
            CloseDetailWindow();
            return;
        }

        var eventItem = FindAncestor<EventItem>(hitResult.VisualHit);
        if (eventItem?.EventData is CalendarEvent evt)
        {
            ShowDetailWindow(evt);
        }
        else
        {
            CloseDetailWindow();
        }
    }

    private bool IsMouseOverSettingsButton(DependencyObject visualHit)
        => FindAncestor<Button>(visualHit) == SettingsButton;

    private void ShowDetailWindow(CalendarEvent evt)
    {
        if (_detailWindow == null)
        {
            _detailWindow = new EventDetailWindow { ShowActivated = false };
        }

        _detailWindow.ShowEvent(evt, this);
    }

    private void CloseDetailWindow() => _detailWindow?.Hide();

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

    // ==================================================================
    // 抓手 hover
    // ==================================================================

    private void OnGripHover(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(this);
        bool nearGrip = pos.X >= ActualWidth - 20 && pos.Y >= ActualHeight - 20;

        var brush = nearGrip
            ? (TryFindResource("TextSecondaryBrush") as Brush)
            : (TryFindResource("TextDisabledBrush") as Brush);

        if (brush == null)
        {
            return;
        }

        foreach (var dot in new[] { GripDot00, GripDot01, GripDot02, GripDot10, GripDot11, GripDot12, GripDot20, GripDot21, GripDot22 })
        {
            dot.Fill = brush;
        }
    }

    // ==================================================================
    // 事件列表可见性
    // ==================================================================

    private void UpdateNoEventsVisibility()
    {
        // 注意：EventScrollViewer.Visibility 由 XAML 绑定 IsScheduleListVisible 独占，
        // 这里<b>不再</b>直接赋值（避免清除绑定）。仅更新列表内部占位/收尾标记。
        bool hasEvents = _calendarViewModel.UpcomingEvents.Count > 0;
        NoEventsText.Visibility = hasEvents ? Visibility.Collapsed : Visibility.Visible;
        EndMarker.Visibility = hasEvents ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================================================================
    // 交互
    // ==================================================================

    private void OnPrevMonthClick(object sender, RoutedEventArgs e) => _calendarViewModel.NavigatePreviousMonth();

    private void OnNextMonthClick(object sender, RoutedEventArgs e) => _calendarViewModel.NavigateNextMonth();

    private void OnTodayClick(object sender, RoutedEventArgs e) => _calendarViewModel.NavigateToToday();

    private void OnBannerCloseClick(object sender, RoutedEventArgs e) => _calendarViewModel.DismissBanner();

    // ==================================================================
    // 年月快速跳转浮层
    // ==================================================================

    /// <summary>浮层基准年：月份视图=将要跳转的年份；年份视图=当前页基准年（十年对齐）。</summary>
    private int _pickerYear;

    /// <summary>浮层当前是否处于「选择年份」视图（false=月份视图）。</summary>
    private bool _pickerYearView;

    /// <summary>点 Header 年月标题：打开浮层（默认月份视图，基准年=当前显示年）。</summary>
    private void OnMonthTitleClick(object sender, RoutedEventArgs e)
    {
        _pickerYear = _calendarViewModel.Year;
        _pickerYearView = false;
        RefreshPicker();
        PickerOverlay.Visibility = Visibility.Visible;
    }

    /// <summary>关闭浮层（不影响面板显隐）。</summary>
    private void HidePicker() => PickerOverlay.Visibility = Visibility.Collapsed;

    /// <summary>点遮罩空白处关闭；点卡片内部不关闭。</summary>
    private void OnPickerOverlayClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsWithin(e.OriginalSource as DependencyObject, PickerCard))
        {
            HidePicker();
        }
    }

    private void OnPickerPrevDecadeClick(object sender, RoutedEventArgs e) => ShiftPickerYear(-10);

    private void OnPickerPrevYearClick(object sender, RoutedEventArgs e) => ShiftPickerYear(-1);

    private void OnPickerNextYearClick(object sender, RoutedEventArgs e) => ShiftPickerYear(1);

    private void OnPickerNextDecadeClick(object sender, RoutedEventArgs e) => ShiftPickerYear(10);

    /// <summary>在支持范围内平移基准年；越界则忽略（不抛异常、不改变状态）。</summary>
    private void ShiftPickerYear(int delta)
    {
        int next = _pickerYear + delta;
        if (next < LunarCalendarHelper.MinSupportedYear || next > LunarCalendarHelper.MaxSupportedYear)
        {
            return;
        }

        _pickerYear = next;
        RefreshPicker();
    }

    /// <summary>点年标签：月份视图 ↔ 年份视图 互切。</summary>
    private void OnPickerYearLabelClick(object sender, RoutedEventArgs e)
    {
        _pickerYearView = !_pickerYearView;
        RefreshPicker();
    }

    /// <summary>重建浮层内容（月份 3×4 或 年份 3×4）。</summary>
    private void RefreshPicker()
    {
        PickerGrid.Children.Clear();

        if (!_pickerYearView)
        {
            PickerYearLabelButton.Content = $"{_pickerYear}年";
            PickerYearLabelButton.ToolTip = "点击选择年份";

            for (int m = 1; m <= 12; m++)
            {
                int month = m;
                var button = CreatePickerButton($"{m}月");
                if (_pickerYear == _calendarViewModel.Year && m == _calendarViewModel.Month)
                {
                    HighlightPickerButton(button);
                }

                button.Click += (_, _) =>
                {
                    HidePicker();
                    _calendarViewModel.NavigateToMonth(_pickerYear, month);
                };
                PickerGrid.Children.Add(button);
            }
        }
        else
        {
            int start = Math.Clamp(
                _pickerYear / 10 * 10,
                LunarCalendarHelper.MinSupportedYear,
                LunarCalendarHelper.MaxSupportedYear - 11);

            PickerYearLabelButton.Content = $"{start} - {start + 11}";
            PickerYearLabelButton.ToolTip = "点击返回月份选择";

            for (int i = 0; i < 12; i++)
            {
                int year = start + i;
                var button = CreatePickerButton(year.ToString(CultureInfo.InvariantCulture));
                if (year == _calendarViewModel.Year)
                {
                    HighlightPickerButton(button);
                }

                button.Click += (_, _) =>
                {
                    _pickerYear = year;
                    _pickerYearView = false;
                    RefreshPicker();
                };
                PickerGrid.Children.Add(button);
            }
        }
    }

    /// <summary>创建浮层内的网格按钮（套用 <c>PickerButtonStyle</c>）。</summary>
    private Button CreatePickerButton(string text)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource("PickerButtonStyle"),
            FontSize = 13,
            Height = 44,
            Margin = new Thickness(3)
        };
        return button;
    }

    /// <summary>把「当前显示的年/月」标为强调色 + 半粗（不用底色，避免与 hover 触发冲突）。</summary>
    private static void HighlightPickerButton(Button button)
    {
        button.SetResourceReference(Control.ForegroundProperty, "TodayAccentBrush");
        button.FontWeight = FontWeights.SemiBold;
    }

    /// <summary>判断 <paramref name="node"/> 是否位于 <paramref name="target"/> 的可视子树内（沿可视树向上查找）。</summary>
    private static bool IsWithin(DependencyObject? node, DependencyObject target)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, target))
            {
                return true;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    /// <summary>Esc 关闭年月浮层（面板本身不受影响）。</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && PickerOverlay.Visibility == Visibility.Visible)
        {
            HidePicker();
            e.Handled = true;
        }
    }

    private async void OnToggleSchedule(object sender, MouseButtonEventArgs e)
    {
        bool expanding = !_calendarViewModel.ScheduleExpanded;
        _calendarViewModel.ToggleSchedule();
        UpdateNoEventsVisibility();

        // P0-7 §6.3：展开时窗口向上生长 176px（右下角锚点不动）；触顶则改为压缩月历网格。
        AdjustHeightForSchedule(expanding);

        // 展开时如无数据则补刷一次
        if (_calendarViewModel.IsScheduleListVisible && _calendarViewModel.UpcomingEvents.Count == 0)
        {
            await _calendarViewModel.RefreshDataAsync();
        }
    }

    /// <summary>
    /// 展开 / 收起日程区时调整窗口高度（右下角锚点固定）。触工作区顶部时不再上移，
    /// 改由月历网格压缩吸收（事件列表内部滚动）。
    ///
    /// <para><b>★ 多显示器 + 混合 DPI：全部走物理像素</b>（与
    /// <see cref="WinCal.Core.Helpers.WindowPositionHelper.PositionNearTaskbar"/> 同一坐标体系）。
    /// 旧实现有两处根缺陷（阶段 A 探针实证）：
    /// <list type="number">
    ///   <item><c>SystemParameters.WorkArea</c> 在多显示器下<b>只返回主屏工作区</b>，
    ///         面板在副屏时拿主屏的 top 去钳制 → 钳不住；</item>
    ///   <item><c>Top/Height</c> 是 WPF DIP 赋值，跨屏换算用「窗口当前所在屏」的缩放、
    ///         且虚拟 DIP 空间按各屏缩放拼合、<b>不均匀</b>，DIP 加减法 ≠ 物理加减法。</item>
    /// </list>
    /// 现改为：取窗口所在屏的 <c>rcWork</c>（物理）+ 该屏 scale，用 <c>SetWindowPos</c>
    /// 物理直置（底边锚点、向上生长），随后<b>回写 DIP</b> 同步 WPF 模型。</para>
    ///
    /// <para><b>★ 调用前提（时序，探针实证 2026-09-20）：</b>本方法按「窗口<b>当前所在屏</b>」
    /// 计算，故必须<b>在窗口已定位到目标屏之后</b>调用（即 <c>PositionNearTaskbar</c> 之后）。
    /// 交互路径（面板已打开、用户点日程条 → <c>OnToggleSchedule</c>）满足此前提，实测 12/12 PASS。
    /// <b>注意</b>：<c>OnLoaded</c> 里那次调用发生在 <c>Show()</c> 内部、早于
    /// <c>PopupController</c> 的 <c>PositionNearTaskbar</c>，此时窗口仍在<b>级联出生位</b>
    /// （默认在主屏，即使目标是副屏），会按错误显示器计算。探针实测该路径新旧算法<b>同为 6/12 正确</b>
    /// （非确定性竞态，与算法无关）——属装配层调用顺序问题，已上报，本方法不做规避。</para>
    /// </summary>
    /// <param name="expanding">true=展开（+176 DIP 高度）；false=收起（−176 DIP 高度）。</param>
    private void AdjustHeightForSchedule(bool expanding)
    {
        const double scheduleListHeight = 176.0;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return; // 尚未创建 HWND（未 Show）：无法调整
        }

        // 目标屏 = 窗口当前所在屏（Show 之后窗口已由 PositionNearTaskbar 定位到目标屏）
        IntPtr monitor = NativeMethods.GetMonitorForWindow(hwnd);
        if (!NativeMethods.TryGetMonitorInfo(monitor, out NativeMethods.MONITORINFO info) || !info.rcWork.IsValid)
        {
            return; // 取不到显示器信息：放弃调整（保持原状，绝不乱放）
        }

        double scale = NativeMethods.GetDpiScaleForMonitor(monitor);
        if (scale <= 0)
        {
            scale = 1.0;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT actual) || !actual.IsValid)
        {
            return; // 取不到窗口物理矩形：放弃
        }

        var work = info.rcWork;

        // 当前高度（DIP）：以逻辑 Height 为准（权威值，不受物理换算误差影响）
        double currentHeightDip = Height > 0 ? Height : (actual.Bottom - actual.Top) / scale;
        double targetHeightDip = expanding ? currentHeightDip + scheduleListHeight : currentHeightDip - scheduleListHeight;
        targetHeightDip = Math.Max(MinPanelHeight, targetHeightDip);

        int heightPhys = (int)Math.Round(targetHeightDip * scale);

        // 底边锚点：右下角固定、向上生长（物理像素）
        int newTopPhys = actual.Bottom - heightPhys;

        // 触顶：不允许 Top 小于工作区顶部，改为保持底边、压缩内容（高度收缩）
        if (newTopPhys < work.Top)
        {
            newTopPhys = work.Top;
            heightPhys = actual.Bottom - newTopPhys;
        }

        // 物理直置：宽度不变（actual.Width）、底边不动、只改高度与顶边。
        // ⚠️ 此处高度要变，故【不能】带 SWP_NOSIZE（与 PositionNearTaskbar 那次不同）。
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            actual.Left,
            newTopPhys,
            actual.Right - actual.Left,
            heightPhys,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // 回写 DIP 同步 WPF 模型（Height/Top），供下游逻辑读取（铁律：不回写会读到滞后值）
        Height = heightPhys / scale;
        Top = newTopPhys / scale;

        // 复核：若漂移 > 1px（上游异步 / DPI 变更竞态），再纠正一次（与 PositionNearTaskbar 同款防御）
        if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT check) &&
            (Math.Abs(check.Top - newTopPhys) > 1 || Math.Abs(check.Bottom - (newTopPhys + heightPhys)) > 1))
        {
            NativeMethods.SetWindowPos(
                hwnd,
                IntPtr.Zero,
                actual.Left,
                newTopPhys,
                actual.Right - actual.Left,
                heightPhys,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            Height = heightPhys / scale;
            Top = newTopPhys / scale;
        }

        // 该增长不写入持久化尺寸（仅用户拖拽才持久化，避免「关闭重开」尺寸漂移）
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        CloseDetailWindow();
        Hide();
        App.ShowSettings();
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer?.Stop();
        StopFocusTracking();
        CloseDetailWindow();
        HidePicker();
        base.OnClosed(e);
    }
}
