using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Hardcodet.Wpf.TaskbarNotification;
using WinCal.Core.Helpers;
using WinCal.Core.Interop;
using WinCal.Core.Services;
using WinCal.Views;

namespace WinCal;

/// <summary>
/// 应用程序装配中枢（T05）。
///
/// <para><b>启动序列（架构 §7 T05 / 时序②）：</b></para>
/// <list type="number">
///   <item>最早处启用 PerMonitorV2 DPI 感知（钩子坐标与 <c>GetWindowRect</c> 同坐标系的前提）；</item>
///   <item>加载设置 → 应用主题；</item>
///   <item>装配任务栏时钟鼠标钩子（<see cref="ClockClickHook"/>），点时钟 → 切换面板（替代已删除的拦截器）；</item>
///   <item>装配全局快捷键（<see cref="GlobalHotkeyService"/>，默认 <c>Ctrl+Alt+C</c>），失败<b>显式上报</b>（Q8）；</item>
///   <item>按 <see cref="AppSettings.ShowTrayIcon"/> 决定是否创建托盘图标（默认隐藏，P0-2）；</item>
///   <item>首启引导（<see cref="FirstRunService"/>，Q6/Q7）。</item>
/// </list>
///
/// <para><b>退出序列（P0-1 验收 5）：</b>钩子 → 热键 → 定位器 → 面板 → 托盘 → <c>base.OnExit</c>。</para>
/// </summary>
public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;

    private TaskbarClockLocator? _clockLocator;
    private ClockClickHook? _clockHook;
    private GlobalHotkeyService? _hotkey;
    private PopupController? _popupController;
    private FirstRunService? _firstRunService;
    private AppSettings? _settings;

    /// <summary>快捷键注册失败时的原因文案（Q8：不静默，供首启提示/诊断呈现）。</summary>
    private string? _hotkeyRegistrationError;

    /// <summary>面板切换去抖窗口（毫秒）。窗口内重复的切换请求一律忽略。</summary>
    /// <remarks>
    /// 任务栏时钟点击可能因系统行为产生「双击 / 连点」：
    /// 第一次到达即打开面板，紧接的第二次到达会立刻又关闭，用户观感为「点了没反应」。
    /// 以 250ms 去抖窗口吞掉这一对抖动，保证一次意图只切换一次。
    /// </remarks>
    private const int ToggleDebounceMs = 250;

    /// <summary>上一次成功执行面板切换的时刻（<see cref="Environment.TickCount64"/>）。</summary>
    private long _lastToggleTicks;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ★ D2：必须在任何窗口创建之前启用 PerMonitorV2（app.manifest 之外的运行时双保险）
        try
        {
            NativeMethods.TryEnablePerMonitorV2();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: TryEnablePerMonitorV2 failed: {ex.Message}");
        }

        // 全局异常处理
        DispatcherUnhandledException += (s, args) =>
        {
            Debug.WriteLine($"WinCal: Unhandled exception: {args.Exception}");
            args.Handled = true;
        };

        // 加载设置（时序②第 1 步：含旧数据目录迁移 + 旧字段迁移）
        _settings = AppSettings.Load();

        // 应用主题
        ThemeHelper.ApplyTheme(_settings.ThemeMode);

        // 面板编排器
        _popupController = new PopupController();

        // 托盘图标：默认隐藏（P0-2），仅在 ShowTrayIcon=true 时创建
        if (_settings.ShowTrayIcon)
        {
            CreateTrayIcon();
        }
        // 装配任务栏时钟鼠标钩子（P0-1 核心入口）
        SetupClockHook();

        // 装配全局快捷键（P0-9 兜底入口）
        SetupHotkey();

        // 首启引导（Q6/Q7）
        _firstRunService = new FirstRunService(_settings);
        Dispatcher.BeginInvoke(new Action(RunFirstRunGuideIfNeeded));
    }

    // ==================================================================
    // 托盘图标（按 ShowTrayIcon 显隐）
    // ==================================================================

    /// <summary>创建托盘图标并挂接左键切换面板 + 右键菜单（设置 / 退出）。</summary>
    private void CreateTrayIcon()
    {
        // 幂等保护：已创建则直接返回（快速路径）
        if (_trayIcon != null)
        {
            return;
        }

        TaskbarIcon? trayIcon = null;

        try
        {
            trayIcon = (TaskbarIcon)FindResource("TrayIcon")!;

            // ★ D-1 残余修复：采用具名处理器 + 「先 -= 再 +=」的幂等订阅。
            //   即便 catch 分支把 _trayIcon 置 null、后又被重新调用，单例上也不会产生重复订阅，
            //   从而避免「一次左键触发两次 TogglePopup → 面板开了又立刻关」的假死现象。
            trayIcon.TrayLeftMouseDown -= OnTrayLeftMouseDown;
            trayIcon.TrayLeftMouseDown += OnTrayLeftMouseDown;

            // 动态生成带今日日期数字的图标
            trayIcon.Icon = TrayIconGenerator.Generate(DateTime.Today.Day);

            // 更新托盘提示文本（品牌统一：WinCal）
            trayIcon.ToolTipText = $"WinCal - {DateTime.Now:yyyy年M月d日 dddd}";

            // 动态创建右键菜单（每次重建，避免复用已挂父级的菜单）
            var menu = new ContextMenu();

            var settingsItem = new MenuItem { Header = "设置" };
            settingsItem.Click += (s, args) => OpenSettings();
            menu.Items.Add(settingsItem);

            menu.Items.Add(new Separator());

            var exitItem = new MenuItem { Header = "退出" };
            exitItem.Click += (s, args) => Shutdown();
            menu.Items.Add(exitItem);

            trayIcon.ContextMenu = menu;
            trayIcon.Visibility = Visibility.Visible;

            // 全流程成功后才发布字段引用（避免半初始化状态被后续逻辑误用）
            _trayIcon = trayIcon;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: CreateTrayIcon failed: {ex.Message}");

            // 初始化中途失败：解绑本次订阅，保证单例上没有残留订阅（可安全重入）
            if (trayIcon != null)
            {
                try
                {
                    trayIcon.TrayLeftMouseDown -= OnTrayLeftMouseDown;
                }
                catch (Exception unsubEx)
                {
                    Debug.WriteLine($"WinCal: CreateTrayIcon 回滚订阅失败: {unsubEx.Message}");
                }
            }

            _trayIcon = null;
        }
    }

    /// <summary>托盘图标左键按下 → 切换面板显隐（具名处理器，支持幂等反/订阅）。</summary>
    /// <remarks>
    /// 托盘点击无「屏幕坐标」语义 → 以<b>光标所在显示器</b>为目标屏
    /// （用户刚操作处即其视线焦点，最符合直觉；见 <see cref="NativeMethods.GetCursorMonitor"/>）。
    /// </remarks>
    private void OnTrayLeftMouseDown(object sender, RoutedEventArgs e)
        => TogglePopup(NativeMethods.GetCursorMonitor());

    /// <summary>
    /// 隐藏托盘图标（D-1）。<b>仅置为 <see cref="Visibility.Collapsed"/></b>，
    /// <b>不调用</b> <see cref="TaskbarIcon.Dispose"/>，也<b>不</b>清空字段引用：
    /// <list type="bullet">
    ///   <item><see cref="App.xaml"/> 中的 <c>TrayIcon</c> 是 <b>Application 资源单例</b>，
    ///         <c>Dispose()</c> 会永久置 <c>IsDisposed = true</c> 且销毁消息接收窗口，
    ///         导致后续「再次勾选显示托盘」时无法复用（复用即抛 <see cref="ObjectDisposedException"/>）。</item>
    ///   <item>保留字段引用可保证「隐藏 → 再次显示」的幂等：<see cref="CreateTrayIcon"/>
    ///         不会因引用为空而重复订阅 <c>TrayLeftMouseDown</c>（避免一次点击触发多次）。</item>
    ///   <item>Hardcodet 2.0.1 的 <c>TaskbarIcon</c> 已重写 <c>VisibilityProperty</c>：
    ///         置为 <see cref="Visibility.Collapsed"/> 会自动移除托盘图标，置回
    ///         <see cref="Visibility.Visible"/> 会自动重新创建 —— 天然支持可逆显隐。</item>
    /// </list>
    /// 真正的一次性释放仍由 <see cref="OnExit"/> 统一调用 <see cref="TaskbarIcon.Dispose"/> 完成。
    /// </summary>
    private void HideTrayIcon()
    {
        if (_trayIcon == null)
        {
            return;
        }

        try
        {
            _trayIcon.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: HideTrayIcon failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 立即应用「显示托盘图标」设置（D-1：设置保存后即时生效，无需重启）。
    ///
    /// <para>幂等：多次以相同入参调用不会产生多个图标；重复开启时 <see cref="CreateTrayIcon"/>
    /// 与 Hardcodet 内部的 <c>CreateTaskbarIcon</c> 均自带幂等保护。</para>
    /// </summary>
    /// <param name="show">true = 显示托盘图标；false = 隐藏托盘图标。</param>
    public static void ApplyTrayIconSetting(bool show)
    {
        try
        {
            if (Current is not App app)
            {
                return;
            }

            // 托盘图标属于 UI 元素，必须切到 UI 线程操作
            if (app.Dispatcher.CheckAccess())
            {
                app.ApplyTrayIconSettingCore(show);
            }
            else
            {
                app.Dispatcher.BeginInvoke(new Action(() => app.ApplyTrayIconSettingCore(show)));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: ApplyTrayIconSetting failed: {ex.Message}");
        }
    }

    /// <summary>在 UI 线程上执行托盘图标显隐（幂等）。</summary>
    private void ApplyTrayIconSettingCore(bool show)
    {
        if (show)
        {
            if (_trayIcon == null)
            {
                CreateTrayIcon();
            }
            else
            {
                // 已存在：仅确保可见（可能此前被置为 Collapsed）
                _trayIcon.Visibility = Visibility.Visible;
            }
        }
        else
        {
            HideTrayIcon();
        }
    }

    // ==================================================================
    // 时钟钩子装配
    // ==================================================================

    /// <summary>装配任务栏时钟低级鼠标钩子：命中 → 切换面板（Toggle，非 Show）。</summary>
    private void SetupClockHook()
    {
        try
        {
            _clockLocator = new TaskbarClockLocator();
            _clockHook = new ClockClickHook(_clockLocator);
            _clockHook.SetUiDispatcher(Dispatcher);

            // 点时钟 = 切换（再点一次关闭）；用点击坐标解析目标显示器 → 「点哪屏弹哪屏」
            _clockHook.ClockClicked += (x, y) => TogglePopup(ResolveMonitorForClick(x, y));

            bool ok = _clockHook.Start();
            if (!ok)
            {
                Debug.WriteLine($"WinCal: 时钟钩子未安装成功: {_clockHook.LastError}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: SetupClockHook failed: {ex.Message}");
        }
    }

    // ==================================================================
    // 全局快捷键装配
    // ==================================================================

    /// <summary>装配全局快捷键并注入「实际生效组合」到设置页（Q8 不静默）。</summary>
    private void SetupHotkey()
    {
        try
        {
            _hotkey = new GlobalHotkeyService();
            // 热键无点击坐标 → 以光标所在显示器为目标屏（用户视线焦点所在，最直觉）
            _hotkey.Pressed += () => TogglePopup(NativeMethods.GetCursorMonitor());
            _hotkey.RegistrationFailed += msg =>
            {
                _hotkeyRegistrationError = msg;
                Debug.WriteLine($"WinCal: 快捷键注册失败: {msg}");
            };

            // 主组合 → 备选组合；失败会经 RegistrationFailed 上报
            _hotkey.TryRegister(out string? error);
            if (error != null)
            {
                _hotkeyRegistrationError = error;
                Debug.WriteLine($"WinCal: 全部快捷键注册失败: {error}");
            }

            // 关键：把「实际生效的快捷键」注入设置页（T04 已预留此静态挂钩）
            SettingsWindow.ActiveHotkeyProvider = () => _hotkey?.ActiveHotkey;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: SetupHotkey failed: {ex.Message}");
        }
    }

    // ==================================================================
    // 首启引导
    // ==================================================================

    /// <summary>按需执行首启引导；若快捷键注册失败，则把原因一并呈现给用户（Q8）。</summary>
    private void RunFirstRunGuideIfNeeded()
    {
        if (_firstRunService == null || _settings == null)
        {
            return;
        }

        try
        {
            _firstRunService.RunIfNeeded(null, _hotkey?.ActiveHotkey);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: RunFirstRunGuideIfNeeded failed: {ex.Message}");
        }

        // 快捷键注册失败：显式提示（不静默），即使首启提示已处理过也要告知
        if (!string.IsNullOrWhiteSpace(_hotkeyRegistrationError))
        {
            try
            {
                MessageBox.Show(
                    _hotkeyRegistrationError,
                    "WinCal 快捷键提示",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WinCal: 显示快捷键失败提示异常: {ex.Message}");
            }
        }
    }

    // ==================================================================
    // 设置窗口
    // ==================================================================

    /// <summary>
    /// 显示设置窗口（独立顶层窗口，单例模式）。可从右键菜单、托盘或面板齿轮按钮调用。
    /// <para><b>注意：签名与可见性由 T03 的 <c>PopupWindow</c> 依赖，必须保持不变。</b></para>
    /// </summary>
    public static void ShowSettings()
    {
        try
        {
            var app = (App)Current;
            if (app._settingsWindow != null && app._settingsWindow.IsVisible)
            {
                app._settingsWindow.Activate();
                return;
            }

            app._settingsWindow = new SettingsWindow();
            app._settingsWindow.Closed += (_, _) => app._settingsWindow = null;
            app._settingsWindow.Show();
            app._settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            var logPath = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "wincal_error.log");
            try
            {
                System.IO.File.WriteAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n{ex}\n\n--- InnerException ---\n{ex.InnerException}");
            }
            catch (Exception writeEx)
            {
                Debug.WriteLine($"WinCal: 写错误日志失败: {writeEx.Message}");
            }

            // 提示文案与实际文件名一致（wincal_error.log，修复原提示与文件名不符的笔误）
            MessageBox.Show("错误已写入桌面 wincal_error.log", "WinCal 错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenSettings()
    {
        // 使用 Dispatcher 延迟打开，避免与右键菜单的弹出窗口冲突
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Debug.WriteLine("WinCal: OpenSettings dispatcher callback executing");
            ShowSettings();
        }));
    }

    // ==================================================================
    // 面板显隐（转调 PopupController）
    // ==================================================================

    /// <summary>
    /// 由时钟点击的物理坐标解析出应当弹出的目标显示器（「点哪屏弹哪屏」）。
    ///
    /// <para><b>解析顺序：</b></para>
    /// <list type="number">
    ///   <item>优先用时钟定位器缓存的命中区域 <c>FindRegion(x,y)?.Monitor</c> —— 最精确
    ///         （该区域就是被点的那个时钟，其 <c>Monitor</c> 已由 <c>GetMonitorForWindow</c> 解析）；</item>
    ///   <item>缓存未命中（如显示器刚变化、缓存未刷新）时回退
    ///         <c>MonitorFromPoint(x, y, MONITOR_DEFAULTTONEAREST)</c> —— 用点击点直接求所在屏。</item>
    /// </list>
    /// </summary>
    /// <param name="x">点击点物理 X（来自钩子回调）。</param>
    /// <param name="y">点击点物理 Y。</param>
    /// <returns>目标显示器句柄。</returns>
    private IntPtr ResolveMonitorForClick(int x, int y)
    {
        // 1) 优先用缓存的命中区域（该区域所属屏即被点时钟所在屏）
        try
        {
            var region = _clockLocator?.FindRegion(x, y);
            if (region != null && region.Monitor != IntPtr.Zero)
            {
                return region.Monitor;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: ResolveMonitorForClick(FindRegion) failed: {ex.Message}");
        }

        // 2) 回退：直接用点击物理坐标求所在显示器
        return NativeMethods.GetMonitorForPoint(x, y);
    }

    /// <summary>
    /// 切换日历面板显示/隐藏。点时钟与全局快捷键共用此入口。
    ///
    /// <para><b>★ 防御性修复 3：</b>加入 <see cref="ToggleDebounceMs"/> 去抖。
    /// 时钟点击在系统层可能被识别为双击/连点，若不拦截，第一次打开后紧跟的第二次会在
    /// 极短时间内把面板又关掉，表现为「闪一下」或「点了没反应」。
    /// 采用 <see cref="Interlocked.Exchange(ref long, long)"/> 原子地读取并更新上次触发时刻，
    /// 保证多入口（时钟钩子线程 → UI 线程、全局热键线程 → UI 线程）并发调用时判定仍准确。</para>
    /// </summary>
    /// <param name="targetMonitor">
    /// 目标显示器句柄（本次切换若为「打开」则在该屏弹出）；为 null 时由定位层回退窗口当前所在屏。
    /// </param>
    private void TogglePopup(IntPtr? targetMonitor = null)
    {
        long now = Environment.TickCount64;
        long previous = Interlocked.Exchange(ref _lastToggleTicks, now);

        // 距上次切换不足去抖窗口：视为同一意图的抖动，直接忽略本次
        if (now - previous < ToggleDebounceMs)
        {
            return;
        }

        _popupController?.Toggle(targetMonitor);
    }

    // ==================================================================
    // 退出清理（顺序重要：钩子 → 热键 → 定位器 → 面板 → 托盘）
    // ==================================================================

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _clockHook?.Dispose();     // 内部 UnhookWindowsHookEx —— 必须！否则鼠标可能异常
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: 释放时钟钩子失败: {ex.Message}");
        }

        try
        {
            _hotkey?.Dispose();        // 内部 UnregisterHotKey
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: 释放热键失败: {ex.Message}");
        }

        try
        {
            _clockLocator?.Dispose();  // 释放定时器与显示器变化订阅
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: 释放时钟定位器失败: {ex.Message}");
        }

        try
        {
            _popupController?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: 释放面板失败: {ex.Message}");
        }

        _trayIcon?.Dispose();

        base.OnExit(e);
    }
}
