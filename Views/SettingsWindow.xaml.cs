using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using WinCal.Core.Helpers;
using WinCal.Core.Interop;
using WinCal.Core.Services;

namespace WinCal.Views;

/// <summary>
/// 设置窗口中的单条 ICS 订阅可编辑项：
/// URL（只读展示）+ 显示别名 + <b>用途</b>（节假日 / 农历节气 / 日程）+ 启停。
/// 用途决定该订阅的数据进入哪一条解耦数据流（契约 C12 / Q12）。
/// </summary>
public sealed class SubscriptionItem : INotifyPropertyChanged
{
    private string _alias = string.Empty;
    private int _purposeIndex;
    private bool _enabled = true;

    /// <summary>在列表中的序号（用于配色轮转，仅展示）。</summary>
    public int Index { get; set; }

    /// <summary>完整订阅 URL。</summary>
    public string FullUrl { get; set; } = string.Empty;

    /// <summary>截短后的 URL（界面展示用）。</summary>
    public string DisplayUrl { get; set; } = string.Empty;

    /// <summary>展示用配色（十六进制字符串）。</summary>
    public string Color { get; set; } = "#FF6D00";

    /// <summary>显示别名（可编辑）。</summary>
    public string Alias
    {
        get => _alias;
        set
        {
            if (_alias == value) return;
            _alias = value;
            OnPropertyChanged(nameof(Alias));
        }
    }

    /// <summary>用途下拉的选中索引：0=节假日、1=农历节气、2=日程（与 <see cref="IcsPurpose"/> 数值一致）。</summary>
    public int PurposeIndex
    {
        get => _purposeIndex;
        set
        {
            if (_purposeIndex == value) return;
            _purposeIndex = value;
            OnPropertyChanged(nameof(PurposeIndex));
        }
    }

    /// <summary>是否启用该订阅。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            OnPropertyChanged(nameof(Enabled));
        }
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 设置窗口。对齐 12 项设置决策（Q3/Q8/Q9/Q10/Q12 / P0-2/P0-7/P0-8）：
/// <list type="bullet">
///   <item>ICS 订阅使用 <see cref="IcsSubscription"/> 列表并新增「用途」下拉；</item>
///   <item>新增「显示托盘图标 / 启用日程 / 日程展开 / 弹出快捷键 / 使用模拟数据 / 首启状态」；</item>
///   <item>「系统日历」数据源显式标注<b>本机不可用</b>并给出原因（绝不静默降级）；</item>
///   <item>实现死设置 <see cref="AppSettings.ShowHeaderDateTime"/> 与 <see cref="AppSettings.TimeFormat"/>；</item>
///   <item>托盘隐藏时退出入口在本窗口内。</item>
/// </list>
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private bool _initialized;
    private readonly List<SubscriptionItem> _subscriptions = new();

    /// <summary>
    /// 真实生效快捷键的提供者。由 T05 在装配时注入，例如：
    /// <c>SettingsWindow.ActiveHotkeyProvider = () =&gt; _hotkeyService?.ActiveHotkey;</c>。
    /// 为 <c>null</c> 时退化为显示设置中的「期望组合」并加注「下次启动生效」。
    /// </summary>
    public static Func<string?>? ActiveHotkeyProvider { get; set; }

    private static readonly string[] FontSizeLabels = { "最小", "较小", "标准", "较大", "最大" };
    private static readonly int[] IcsRefreshValues = { 10, 30, 60, 120 };

    private static readonly string[] SubscriptionColors =
    {
        "#FF6D00", "#0078D4", "#E91E63", "#00897B", "#7B1FA2", "#C62828", "#2E7D32", "#F57F17"
    };

    /// <summary>构造设置窗口：从磁盘加载设置并填充 UI。</summary>
    public SettingsWindow()
    {
        _settings = AppSettings.Load();

        InitializeComponent();
        LoadSettings();
        _initialized = true;

        Loaded += OnLoaded;
    }

    // ==================================================================
    // 加载
    // ==================================================================

    /// <summary>把持久化设置加载到各 UI 控件。</summary>
    private void LoadSettings()
    {
        // #1 颜色主题
        ThemeComboBox.SelectedIndex = (int)_settings.ThemeMode;

        // #2 字体大小
        FontSizeSlider.Value = _settings.FontSizeOffset;
        FontSizeSlider.ValueChanged += (_, _) => UpdateFontSizeLabel();
        UpdateFontSizeLabel();

        // #3 顶部时间日期（实现死设置 ShowHeaderDateTime / TimeFormat）
        ShowHeaderDateTimeCheck.IsChecked = _settings.ShowHeaderDateTime;
        TimeFormatCombo.SelectedIndex = (int)_settings.TimeFormat;

        // #4 开机自启动：以注册表实际状态为准（StartupHelper.IsEnabled 兼容新旧值名），
        //    避免注册表被外部改动后界面与实际不符；同时回写配置值保持两者自洽。
        bool actuallyEnabled = StartupHelper.IsEnabled();
        _settings.AutoStartup = actuallyEnabled;
        AutoStartupCheckBox.IsChecked = actuallyEnabled;
        AutoStartupAskedText.Text = _settings.AutoStartupAsked
            ? "（首启时已询问过是否开机自启）"
            : "（首启尚未询问；首次运行时将提示一次）";

        // #5 显示托盘图标（P0-2，默认关闭）
        ShowTrayIconCheck.IsChecked = _settings.ShowTrayIcon;

        // #6 弹出快捷键（显示真实生效组合）
        HotkeyInputBox.Text = _settings.StartupHotkey;
        RefreshHotkeyDisplay();

        // #7 启用日程（P0-7，默认关闭）
        EnableScheduleCheck.IsChecked = _settings.EnableSchedule;
        ScheduleExpandedCheck.IsChecked = _settings.ScheduleExpanded;

        // #8 数据源
        DataSourceComboBox.SelectedIndex = (int)_settings.DataSource;
        MarkSystemCalendarUnavailable();
        UpdateIcsPanelVisibility();

        // 加载 ICS 订阅列表（含别名 / 用途 / 启停）
        _subscriptions.Clear();
        for (int i = 0; i < _settings.Subscriptions.Count; i++)
        {
            var sub = _settings.Subscriptions[i];
            if (sub == null || string.IsNullOrWhiteSpace(sub.Url)) continue;

            _subscriptions.Add(new SubscriptionItem
            {
                Index = i,
                FullUrl = sub.Url,
                DisplayUrl = ShortenUrl(sub.Url),
                Color = SubscriptionColors[_subscriptions.Count % SubscriptionColors.Length],
                Alias = sub.Alias ?? string.Empty,
                PurposeIndex = (int)sub.Purpose,
                Enabled = sub.Enabled
            });
        }
        RenumberSubscriptions();
        RefreshSubscriptionList();

        var refreshIdx = Array.IndexOf(IcsRefreshValues, _settings.IcsRefreshMinutes);
        IcsRefreshCombo.SelectedIndex = refreshIdx >= 0 ? refreshIdx : 1;

        // #9 近期事件天数
        UpcomingDaysCombo.SelectedIndex = _settings.UpcomingDays switch
        {
            1 => 0,
            7 => 2,
            _ => 1
        };

        // #10 周起始日
        WeekStartSunday.IsChecked = _settings.WeekStartDay == WeekStartDay.Sunday;
        WeekStartMonday.IsChecked = _settings.WeekStartDay == WeekStartDay.Monday;

        // #11 使用模拟数据（P0-8）
        UseMockDataCheck.IsChecked = _settings.UseMockData;

        // #12 首启状态（只读展示，供 T05 首启引导参考）
        FirstRunStateText.Text =
            $"自启询问：{(_settings.AutoStartupAsked ? "已完成" : "未进行")}　·　"
            + $"首启提示：{(_settings.FirstRunNoticeShown ? "已展示" : "未展示")}";
    }

    /// <summary>「系统日历」选项显式标注本机不可用，并给出原因（P0-8，绝不静默可用）。</summary>
    private void MarkSystemCalendarUnavailable()
    {
        bool unavailable = !_settings.UseMockData;
        SystemCalendarItem.Content = unavailable ? "系统日历（本机不可用）" : "系统日历";
        SystemCalendarItem.ToolTip = unavailable
            ? "本机缺少 Windows SDK 的 WinRT 日历 API，系统日历不可用；请改用 ICS 订阅。"
            : "已开启模拟数据，此选项无实际意义。";
    }

    /// <summary>异步探测数据源真实状态，刷新「系统日历」说明文案（使用 T02 的 <see cref="DataSourceStatus"/>）。</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await RefreshDataSourceNoteAsync();
    }

    /// <summary>
    /// 通过 <see cref="DateInfoService.GetDataSourceStatusAsync"/> 获取诚实状态，
    /// 据此显示「系统日历」不可用原因与横幅文案。探测失败时也绝不假装可用。
    /// </summary>
    private async Task RefreshDataSourceNoteAsync()
    {
        try
        {
            var service = new DateInfoService(_settings);
            var status = await service.GetDataSourceStatusAsync();

            if (status.IsSystemCalendarUnavailable)
            {
                var note = "⚠ 系统日历：本机不可用（缺少 Windows SDK 的 WinRT 日历 API）。"
                           + "请使用「ICS 订阅链接」并提供「节假日」与「农历节气」订阅。";
                if (!string.IsNullOrWhiteSpace(status.BannerText))
                {
                    note += "\n" + status.BannerText;
                }
                SystemCalendarNote.Text = note;
            }
            else
            {
                SystemCalendarNote.Text = "ℹ 当前已开启「使用模拟数据」，数据源状态不适用于真实场景。";
            }

            SystemCalendarNote.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            // 诚实兜底：探测失败也必须标注不可用，绝不假装可用
            SystemCalendarNote.Text =
                "⚠ 系统日历：本机不可用（缺少 Windows SDK）。已改用 ICS 订阅提供数据。";
            SystemCalendarNote.Visibility = Visibility.Visible;
            Debug.WriteLine($"WinCal: 设置页数据源状态探测失败: {ex.Message}");
        }
    }

    // ==================================================================
    // ICS 订阅列表
    // ==================================================================

    /// <summary>截短 URL 显示（保留域名与路径首尾）。</summary>
    private static string ShortenUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return string.Empty;
        if (url.Length <= 60) return url;

        try
        {
            var uri = new Uri(url);
            var path = uri.AbsolutePath;
            if (path.Length > 30)
            {
                path = path.Substring(0, 15) + "..." + path.Substring(path.Length - 10);
            }
            return $"{uri.Host}{path}";
        }
        catch
        {
            return url.Substring(0, 30) + "...";
        }
    }

    /// <summary>重新编号并轮转配色（仅影响展示）。</summary>
    private void RenumberSubscriptions()
    {
        for (int i = 0; i < _subscriptions.Count; i++)
        {
            _subscriptions[i].Index = i;
            _subscriptions[i].Color = SubscriptionColors[i % SubscriptionColors.Length];
        }
    }

    /// <summary>刷新订阅列表控件。</summary>
    private void RefreshSubscriptionList()
    {
        if (SubscriptionList == null) return;
        SubscriptionList.ItemsSource = null;
        SubscriptionList.ItemsSource = _subscriptions;
    }

    /// <summary>添加一条新的 ICS 订阅。</summary>
    private void OnAddSubscription(object sender, RoutedEventArgs e)
    {
        var url = NewIcsUrlTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;

        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("请输入有效的 HTTP/HTTPS 链接", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _subscriptions.Add(new SubscriptionItem
        {
            FullUrl = url,
            DisplayUrl = ShortenUrl(url),
            Alias = string.Empty,
            // 默认用途：节假日（与 AppSettings 迁移逻辑一致）
            PurposeIndex = (int)IcsPurpose.Holiday,
            Enabled = true
        });

        NewIcsUrlTextBox.Text = string.Empty;
        RenumberSubscriptions();
        RefreshSubscriptionList();
    }

    /// <summary>删除一条 ICS 订阅。</summary>
    private void OnRemoveSubscription(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is SubscriptionItem item)
        {
            _subscriptions.Remove(item);
            RenumberSubscriptions();
            RefreshSubscriptionList();
        }
    }

    // ==================================================================
    // 交互
    // ==================================================================

    private void UpdateFontSizeLabel()
    {
        var idx = (int)FontSizeSlider.Value + 2; // -2..2 → 0..4
        if (idx >= 0 && idx < FontSizeLabels.Length)
        {
            FontSizeLabel.Text = FontSizeLabels[idx];
        }
    }

    /// <summary>刷新快捷键展示：优先显示真实生效组合，否则显示期望组合。</summary>
    private void RefreshHotkeyDisplay()
    {
        var active = ActiveHotkeyProvider?.Invoke();
        if (!string.IsNullOrWhiteSpace(active))
        {
            HotkeyValueText.Text = $"{active}　（当前生效）";
        }
        else
        {
            HotkeyValueText.Text = $"{_settings.StartupHotkey}　（期望，下次启动生效）";
        }
    }

    private void OnDataSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        UpdateIcsPanelVisibility();
    }

    /// <summary>仅当数据源为「ICS 订阅链接」或「两者都用」时显示订阅面板。</summary>
    private void UpdateIcsPanelVisibility()
    {
        if (IcsPanel == null || DataSourceComboBox == null) return;
        var source = (DataSourceType)DataSourceComboBox.SelectedIndex;
        IcsPanel.Visibility = source == DataSourceType.IcsUrl || source == DataSourceType.Both
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    // ==================================================================
    // 保存 / 重置
    // ==================================================================

    /// <summary>从 UI 收集所有值并持久化。</summary>
    private void OnSave(object sender, RoutedEventArgs e)
    {
        // 快捷键校验：复用 T01 的真实解析器，非法输入直接拦下
        var hotkey = HotkeyInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(hotkey))
        {
            hotkey = GlobalHotkeyService.DefaultHotkey;
        }
        if (!GlobalHotkeyService.TryParseHotkey(hotkey, out _, out _, out var parseError))
        {
            MessageBox.Show($"快捷键「{hotkey}」无效：{parseError}", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // #1 颜色主题
        _settings.ThemeMode = (ThemeMode)ThemeComboBox.SelectedIndex;
        ThemeHelper.ApplyTheme(_settings.ThemeMode);

        // #2 字体大小
        _settings.FontSizeOffset = (int)FontSizeSlider.Value;

        // #3 顶部时间日期（实现死设置）
        _settings.ShowHeaderDateTime = ShowHeaderDateTimeCheck.IsChecked == true;
        _settings.TimeFormat = (TimeFormatMode)TimeFormatCombo.SelectedIndex;

        // #4 开机自启动
        _settings.AutoStartup = AutoStartupCheckBox.IsChecked == true;
        ApplyAutoStartup(_settings.AutoStartup);

        // #5 显示托盘图标（P0-2；保存后立即生效，无需重启 —— D-1 修复）
        _settings.ShowTrayIcon = ShowTrayIconCheck.IsChecked == true;
        App.ApplyTrayIconSetting(_settings.ShowTrayIcon);

        // #6 弹出快捷键（P0-9；实际重新注册由 T05 装配层负责）
        _settings.StartupHotkey = hotkey;

        // #7 启用日程（P0-7）
        _settings.EnableSchedule = EnableScheduleCheck.IsChecked == true;
        _settings.ScheduleExpanded = ScheduleExpandedCheck.IsChecked == true;

        // #8 数据源 + 订阅列表（含用途，替代旧 IcsUrls/IcsAliases）
        _settings.DataSource = (DataSourceType)DataSourceComboBox.SelectedIndex;
        _settings.Subscriptions = _subscriptions
            .Where(s => !string.IsNullOrWhiteSpace(s.FullUrl))
            .Select(s => new IcsSubscription(
                s.FullUrl,
                s.Alias ?? string.Empty,
                NormalizePurpose(s.PurposeIndex),
                s.Enabled))
            .ToList();
        _settings.IcsRefreshMinutes = IcsRefreshValues[Math.Max(0, IcsRefreshCombo.SelectedIndex)];

        // #9 近期事件天数
        _settings.UpcomingDays = UpcomingDaysCombo.SelectedIndex switch
        {
            0 => 1,
            2 => 7,
            _ => 3
        };

        // #10 周起始日
        _settings.WeekStartDay = WeekStartMonday.IsChecked == true
            ? WeekStartDay.Monday
            : WeekStartDay.Sunday;

        // #11 使用模拟数据（P0-8）
        _settings.UseMockData = UseMockDataCheck.IsChecked == true;

        // 迁移字段已由写入 Subscriptions 覆盖，确保旧字段不残留
        _settings.IcsUrls = null;
        _settings.IcsAliases = null;

        // 持久化
        _settings.Save();

        Close();
    }

    /// <summary>把下拉索引规范化为合法的 <see cref="IcsPurpose"/>（越界回退 Holiday）。</summary>
    private static IcsPurpose NormalizePurpose(int index) => index switch
    {
        0 => IcsPurpose.Holiday,
        1 => IcsPurpose.Almanac,
        2 => IcsPurpose.Schedule,
        _ => IcsPurpose.Holiday
    };

    /// <summary>恢复默认值（仅重置 UI，需用户再点保存才落盘）。</summary>
    private void OnResetDefaults(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "确定恢复所有设置为默认值？", "恢复默认",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (result != MessageBoxResult.OK) return;

        ThemeComboBox.SelectedIndex = (int)ThemeMode.FollowSystem;
        FontSizeSlider.Value = 0;
        ShowHeaderDateTimeCheck.IsChecked = true;          // 默认开启
        TimeFormatCombo.SelectedIndex = (int)TimeFormatMode.FollowSystem;
        AutoStartupCheckBox.IsChecked = false;
        ShowTrayIconCheck.IsChecked = false;               // P0-2 默认关闭
        HotkeyInputBox.Text = GlobalHotkeyService.DefaultHotkey;
        RefreshHotkeyDisplay();
        EnableScheduleCheck.IsChecked = false;             // P0-7 默认关闭
        ScheduleExpandedCheck.IsChecked = false;
        DataSourceComboBox.SelectedIndex = (int)DataSourceType.IcsUrl; // 默认 ICS 订阅
        _subscriptions.Clear();
        RefreshSubscriptionList();
        NewIcsUrlTextBox.Text = string.Empty;
        IcsRefreshCombo.SelectedIndex = 1;
        UpcomingDaysCombo.SelectedIndex = 1;
        WeekStartSunday.IsChecked = false;
        WeekStartMonday.IsChecked = true;                  // 默认周一
        UseMockDataCheck.IsChecked = false;                // P0-8 默认关闭
    }

    private void ApplyAutoStartup(bool enable)
    {
        try
        {
            if (enable)
            {
                StartupHelper.Enable();
            }
            else
            {
                StartupHelper.Disable();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"设置开机自启动失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ==================================================================
    // 窗口级交互
    // ==================================================================

    private void OnFooterLinkClick(object sender, MouseButtonEventArgs e)
    {
        // 阻止本次按下继续冒泡到窗口级 OnMouseLeftButtonDown，
        // 否则点击页脚链接会顺带触发 DragMove（窗口跟着鼠标跑）。
        e.Handled = true;

        try
        {
            Process.Start(new ProcessStartInfo("https://www.huangao.net") { UseShellExecute = true });
        }
        catch
        {
            // 打开浏览器失败不影响主流程
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    /// <summary>退出程序。托盘图标默认隐藏后无右键菜单，此处是必要的退出入口。</summary>
    private void OnExitApp(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "确定退出 WinCal？", "退出程序",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (result == MessageBoxResult.OK)
        {
            Application.Current.Shutdown();
        }
    }

    /// <summary>
    /// 有界窗口拖动：仅当左键按在标题栏（<c>TitleBar</c>）区域内时才允许拖动，
    /// 内容区（ComboBox / Slider / CheckBox / TextBox / ScrollViewer）按下绝不拖动窗口。
    /// </summary>
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // 命中标题栏内部控件（尤其是关闭按钮）时不拖动：它们自行处理交互。
        if (!IsPressWithinTitleBar(e.OriginalSource))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 窗口状态异常（如拖动过程中被关闭/失去鼠标捕获）时静默忽略，不弹窗、不崩溃。
        }
    }

    /// <summary>
    /// 判断按下的原始命中元素是否位于标题栏（<c>TitleBar</c>）内。
    /// 沿可视化树向上查找祖先；若途中遇到按钮类控件（含 <c>CloseButton</c>）则返回 false。
    /// </summary>
    /// <param name="originalSource">鼠标事件的 <c>OriginalSource</c>。</param>
    /// <returns>按在标题栏空白处返回 true；否则 false。</returns>
    private bool IsPressWithinTitleBar(object? originalSource)
    {
        if (originalSource is not DependencyObject start)
        {
            return false;
        }

        // 沿可视化树向上查找。VisualTreeHelper.GetParent 仅接受 Visual /
        // Visual3D 节点，遇到非 Visual（如 Run）会抛异常，故仅对 Visual 继续上溯。
        DependencyObject? current = start;
        while (current != null)
        {
            // 关闭按钮等按钮控件自行处理 Click，不参与拖动。
            if (current is ButtonBase)
            {
                return false;
            }

            // 命中标题栏 Border 本身（或其为祖先）即视为标题栏拖动区。
            if (ReferenceEquals(current, TitleBar))
            {
                return true;
            }

            current = current is Visual ? VisualTreeHelper.GetParent(current) : null;
        }

        return false;
    }
}
