using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace WinCal.Core.Interop;

/// <summary>
/// 单个时钟区域（命中判定的最小单位）。一个显示器对应一条（多显示器时 ≤ 显示器数）。
/// </summary>
public sealed class ClockRegion
{
    /// <summary>时钟矩形（物理像素）。</summary>
    public NativeMethods.RECT Rect { get; set; }

    /// <summary>该时钟所属显示器句柄（可用于定位弹面板的目标屏）。</summary>
    public IntPtr Monitor { get; set; }

    /// <summary>来源描述，便于日志追溯（win10 类名枚举 / win11 uia）。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>便于日志输出。</summary>
    public override string ToString() => $"[{Source}] {Rect} monitor=0x{Monitor.ToInt64():X}";
}

/// <summary>
/// 任务栏时钟命中判定 + <b>矩形缓存</b>。
///
/// <para>性能约束（D1）：<see cref="HitTest"/> 位于 WH_MOUSE_LL 回调热路径上，
/// 只允许遍历已缓存的矩形（≤ 3 条），<b>严禁</b>做窗口枚举 / UIA / 文件 / 网络 IO。
/// 所有昂贵的定位逻辑一律在 <see cref="Refresh"/> 中完成。</para>
///
/// <list type="bullet">
///   <item><b>Win10 风格任务栏</b>（含 ExplorerPatcher 的 Win10 风格主屏任务栏 → 时钟是独立窗口）：
///         <c>FindWindowW("Shell_TrayWnd")</c> 与 <c>"Shell_SecondaryTrayWnd"</c> →
///         <c>EnumChildWindows</c> 收集类名为 <c>TrayClockWClass</c> 的窗口矩形。
///         <para>本机实测主屏时钟 rect = (3645,2100,3760,2160)。</para></item>
///   <item><b>Win11 风格任务栏</b>（ExplorerPatcher 的 Win11 风格副屏 → 时钟类名不同）：
///         副屏 <c>Shell_SecondaryTrayWnd</c> 下时钟窗口类名为 <c>ClockButton</c>
///         （<b>不是</b> <c>TrayClockWClass</c>）。两条类名公用同一枚举路径
///         （白名单 <see cref="ClockWindowClasses"/>）。
///         <para>本机实测副屏时钟 rect = (5687,1565,5760,1605)，为该任务栏最右元素，不重叠通知中心。</para></item>
///   <item><b>原生 Win11 XAML 任务栏</b>（无独立子窗口）：用 UI Automation 依序尝试三条
///         匹配路径，任一条唯一命中即采用（各显示器 root 相互独立，主屏命中不影响副屏）：
///         <list type="number">
///           <item>旧版 / ExplorerPatcher 的 Win11 风格任务栏：<c>AutomationId == "ClockButton"</c>；</item>
///           <item>新版原生 Win11 任务栏（实测于 Windows 11 IoT Enterprise LTSC 2024, 24H2,
///                 build 26100.9711）：时钟元素 <c>ClassName == "SystemTray.OmniButton"</c>
///                 （<c>ControlType == Button</c>，<c>Name</c> 形如 <c>"时钟 9:16\n2026/10/29"</c>；
///                 同任务栏其它元素类名为 <c>SystemTray.OmniButtonRight</c>（音量）、
///                 <c>SystemTray.AccentButton</c>（网络）、<c>SystemTray.NormalButton</c>（输入法/通知）、
///                 <c>SystemTray.ShowDesktopButton</c>（显示桌面），<c>OmniButton</c> 唯一鉴别时钟；
///                 若同 root 命中多个 OmniButton，则再用 Name 的时间特征正则过滤，过滤后唯一才采用）；</item>
///           <item>跨版本兜底：<c>ControlType == Button</c> 且 <c>Name</c> 匹配时间正则
///                 <c>\d{1,2}:\d{2}</c>，要求唯一命中。</item>
///         </list>
///         <para><b>UIA 定位失败时不再猜测</b>：直接放弃定位（返回空表），
///         此时 <see cref="HitTest"/> 始终失败、一律放行给系统。</para></item>
/// </list>
/// </summary>
public sealed class TaskbarClockLocator
{
    // ------------------------------------------------------------------
    // 常量
    // ------------------------------------------------------------------

    /// <summary>Win10 主任务栏窗口类名。</summary>
    private const string ShellTrayWndClass = "Shell_TrayWnd";

    /// <summary>Win10 副屏任务栏窗口类名。</summary>
    private const string ShellSecondaryTrayWndClass = "Shell_SecondaryTrayWnd";

    /// <summary>Win10 任务栏时钟窗口类名。</summary>
    private const string TrayClockWClass = "TrayClockWClass";

    /// <summary>
    /// Win11 风格任务栏时钟窗口类名（实测：副屏 <c>Shell_SecondaryTrayWnd</c> 下时钟为
    /// <c>ClockButton</c>，而非 <c>TrayClockWClass</c>）。
    /// </summary>
    /// <remarks>
    /// 风险自查（2026-09-20 独立复核）：主屏 <c>Shell_TrayWnd</c> 子窗口<b>不含</b>
    /// <c>ClockButton</c>（主屏时钟为 <c>TrayClockWClass</c>），故接受该类名<b>不会</b>与主屏
    /// 通知中心（<c>TrayButton</c>，3760-3832）重叠，不会误吞系统功能；副屏 <c>ClockButton</c>
    /// 本身即最右侧元素（无通知中心），安全。
    /// </remarks>
    private const string Win11ClockButtonClass = "ClockButton";

    /// <summary>接受的时钟窗口类名白名单（Win10 风格 + Win11 风格）。</summary>
    private static readonly string[] ClockWindowClasses = { TrayClockWClass, Win11ClockButtonClass };

    /// <summary>旧版 / ExplorerPatcher 的 Win11 风格任务栏时钟按钮 UI Automation Id。</summary>
    /// <remarks>原生 Win11 任务栏（build 26100.9711 实测）中该 aid 已不存在——系统托盘图标
    /// 的 aid 统一为 <c>SystemTrayIcon</c>（音量/网络/输入法相同），无鉴别力，
    /// 仅用于兼容 EP 的 Win11 风格任务栏 / 更老系统。</remarks>
    private const string Win11ClockAutomationId = "ClockButton";

    /// <summary>
    /// 新版原生 Win11 任务栏时钟按钮的 UIA ClassName（唯一鉴别点）。
    /// </summary>
    /// <remarks>
    /// 实测（Windows 11 IoT Enterprise LTSC 2024, 24H2, build 26100.9711）：
    /// 整条任务栏只有时钟的 ClassName 为 <c>SystemTray.OmniButton</c>
    /// （ControlType == Button，Name 形如 "时钟 9:16\n2026/10/29"）。
    /// 同任务栏其它元素：音量 <c>SystemTray.OmniButtonRight</c>、网络 <c>SystemTray.AccentButton</c>、
    /// 输入法/通知 <c>SystemTray.NormalButton</c>、显示桌面 <c>SystemTray.ShowDesktopButton</c>。
    /// 用该类名精确命中时钟，不会误吞「显示桌面」/通知中心。
    /// </remarks>
    private const string Win11ClockButtonClassName = "SystemTray.OmniButton";

    /// <summary>
    /// UIA 元素 Name 的时间特征（如 "时钟 9:16\n2026/10/29"）。
    /// 用于多候选时的二次鉴别，音量/网络/输入法等元素的 Name 不会命中。
    /// </summary>
    private static readonly Regex TimePatternRegex = new(@"\d{1,2}:\d{2}", RegexOptions.CultureInvariant);

    /// <summary>缓存刷新周期（秒）。显示器/设置变化时会由外部立即触发 <see cref="Refresh"/> 覆盖此周期。</summary>
    private const int RefreshIntervalSeconds = 30;

    // ------------------------------------------------------------------
    // 状态
    // ------------------------------------------------------------------

    private readonly object _sync = new();
    private List<ClockRegion> _cache = new();
    private DateTime _lastRefreshUtc = DateTime.MinValue;
    private Timer? _timer;
    private bool _started;
    private bool _disposed;

    /// <summary>缓存条目数（调试/诊断用）。</summary>
    public int CachedRegionCount
    {
        get
        {
            lock (_sync)
            {
                return _cache.Count;
            }
        }
    }

    // ------------------------------------------------------------------
    // 命中判定（热路径，只读缓存，O(1)~O(3)）
    // ------------------------------------------------------------------

    /// <summary>
    /// 命中判定：仅遍历已缓存的时钟矩形（≤ 3 条）。<b>热路径，禁止 IO / 枚举 / UIA。</b>
    /// </summary>
    /// <param name="x">点击点 X（物理像素，来自 WH_MOUSE_LL 回调）。</param>
    /// <param name="y">点击点 Y（物理像素）。</param>
    /// <returns>命中返回 true；未命中返回 false（调用方必须放行该次点击）。</returns>
    public bool HitTest(int x, int y)
    {
        // 读取缓存引用（快照语义：Refresh 用整表替换，故无需加锁遍历）
        List<ClockRegion> snapshot;
        lock (_sync)
        {
            snapshot = _cache;
        }

        for (int i = 0; i < snapshot.Count; i++)
        {
            if (snapshot[i].Rect.Contains(x, y))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>取得命中给定点的时钟区域（未命中返回 null）。供弹面板时定位屏幕用。</summary>
    public ClockRegion? FindRegion(int x, int y)
    {
        List<ClockRegion> snapshot;
        lock (_sync)
        {
            snapshot = _cache;
        }

        foreach (var region in snapshot)
        {
            if (region.Rect.Contains(x, y))
            {
                return region;
            }
        }

        return null;
    }

    /// <summary>取得缓存的只读快照（诊断用）。</summary>
    public IReadOnlyList<ClockRegion> GetCachedRegions()
    {
        lock (_sync)
        {
            return _cache.ToArray();
        }
    }

    // ------------------------------------------------------------------
    // 刷新（昂贵路径，允许枚举 / UIA）
    // ------------------------------------------------------------------

    /// <summary>
    /// 重新枚举全部显示器的时钟矩形并替换缓存。
    /// 应在启动时调用一次，并在显示器/设置变化时再次调用；
    /// 同时由内部 30s 周期定时器兜底（<see cref="StartAutoRefresh"/>）。
    /// </summary>
    /// <returns>本次枚举到的区域数量（0 表示未找到任何时钟，命中将始终失败并放行）。</returns>
    public int Refresh()
    {
        List<ClockRegion> regions;
        try
        {
            regions = EnumerateClockRects();
        }
        catch (Exception)
        {
            // 定位失败绝不能让钩子线程崩溃：退化为空缓存（= 永不放行失败，安全侧）
            regions = new List<ClockRegion>();
        }

        lock (_sync)
        {
            _cache = regions;
            _lastRefreshUtc = DateTime.UtcNow;
        }

        return regions.Count;
    }

    /// <summary>若距上次刷新超过 <see cref="RefreshIntervalSeconds"/> 则刷新（周期兜底）。</summary>
    public void RefreshIfStale()
    {
        bool stale;
        lock (_sync)
        {
            stale = (DateTime.UtcNow - _lastRefreshUtc).TotalSeconds >= RefreshIntervalSeconds;
        }

        if (stale)
        {
            Refresh();
        }
    }

    /// <summary>周期自动刷新是否处于运行状态。</summary>
    public bool IsAutoRefreshRunning => _started;

    /// <summary>启动 30s 周期自动刷新（兜底显示器/设置变化未触发事件的情况）。</summary>
    public void StartAutoRefresh()
    {
        if (_disposed || _started)
        {
            return;
        }

        // 每 5 秒检查一次是否已过刷新周期（30s）；首次立即刷新
        _timer = new Timer(_ => RefreshIfStale(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        _started = true;
    }

    /// <summary>停止周期刷新。</summary>
    public void StopAutoRefresh()
    {
        _started = false;
        _timer?.Dispose();
        _timer = null;
    }

    /// <summary>尝试订阅 <c>SystemEvents.DisplaySettingsChanged</c>，变化时立即刷新缓存。</summary>
    public void AttachDisplayChangeWatcher()
    {
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        }
        catch (Exception)
        {
            // 无消息泵 / 权限受限时订阅会抛异常：忽略，退化为 30s 周期刷新
        }
    }

    /// <summary>取消订阅显示器变化事件。</summary>
    public void DetachDisplayChangeWatcher()
    {
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        }
        catch (Exception)
        {
            // 忽略
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Refresh();

    // ------------------------------------------------------------------
    // 枚举实现
    // ------------------------------------------------------------------

    /// <summary>
    /// 枚举全部显示器上的时钟矩形。优先 Win10 类名路径；该路径无结果时回退 Win11 路径。
    /// </summary>
    /// <returns>时钟区域列表（可能为空）。</returns>
    public static List<ClockRegion> EnumerateClockRects()
    {
        var regions = new List<ClockRegion>();

        // ---------- 路径 1：Win10（含 ExplorerPatcher 的 Win10 风格任务栏） ----------
        EnumerateWin10ClockRects(regions);

        // ---------- 路径 2：Win11 原生（UIA） ----------
        if (regions.Count == 0)
        {
            EnumerateWin11ClockRects(regions);
        }

        return regions;
    }

    /// <summary>
    /// Win10 / Win11 任务栏路径：枚举 <c>Shell_TrayWnd</c> / <c>Shell_SecondaryTrayWnd</c> 子窗口中的
    /// 时钟窗口（类名白名单：<c>TrayClockWClass</c>（Win10 风格 / 主屏）或
    /// <c>ClockButton</c>（Win11 风格 / 副屏）），取其 <c>GetWindowRect</c>。
    /// </summary>
    private static void EnumerateWin10ClockRects(List<ClockRegion> sink)
    {
        CollectClockChildren(NativeMethods.FindWindowW(ShellTrayWndClass, null), "win10:Shell_TrayWnd", sink);
        CollectClockChildren(NativeMethods.FindWindowW(ShellSecondaryTrayWndClass, null), "win10:Shell_SecondaryTrayWnd", sink);

        // 多副屏任务栏：Shell_SecondaryTrayWnd 可能有多个，用 EnumWindows 全量兜住
        if (sink.Count == 0)
        {
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                string cls = NativeMethods.GetClassName(hWnd);
                if (cls == ShellTrayWndClass || cls == ShellSecondaryTrayWndClass)
                {
                    CollectClockChildren(hWnd, "win10:enumwnd:" + cls, sink);
                }

                return true;
            }, IntPtr.Zero);
        }
    }

    /// <summary>枚举单个任务栏窗口的子窗口，收集类名为 <c>TrayClockWClass</c> 的矩形。</summary>
    private static void CollectClockChildren(IntPtr taskbar, string source, List<ClockRegion> sink)
    {
        if (taskbar == IntPtr.Zero)
        {
            return;
        }

        var sb = new StringBuilder(64);
        NativeMethods.EnumChildWindows(taskbar, (hChild, _) =>
        {
            sb.Clear();
            int len = NativeMethods.GetClassNameW(hChild, sb, sb.Capacity);
            if (len <= 0)
            {
                return true;
            }

            if (!IsClockWindowClass(sb.ToString(0, len)))
            {
                return true;
            }

            if (!NativeMethods.GetWindowRect(hChild, out NativeMethods.RECT rect) || !rect.IsValid)
            {
                return true;
            }

            // 过滤明显退化的矩形（DWM 遮蔽的模板窗口可能为 1×1 / 0 尺寸）
            if (rect.Width < 8 || rect.Height < 8)
            {
                return true;
            }

            sink.Add(new ClockRegion
            {
                Rect = rect,
                Monitor = NativeMethods.GetMonitorForWindow(hChild),
                Source = source
            });

            return true;
        }, IntPtr.Zero);
    }

    /// <summary>判断窗口类名是否为受支持的时钟窗口（白名单，顺序无关）。</summary>
    /// <param name="className">窗口类名。</param>
    /// <returns>是时钟窗口返回 true。</returns>
    private static bool IsClockWindowClass(string className)
    {
        for (int i = 0; i < ClockWindowClasses.Length; i++)
        {
            if (string.Equals(className, ClockWindowClasses[i], StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Win11 路径：用 UI Automation 依序尝试三条匹配路径（AutomationId 旧版 →
    /// <c>SystemTray.OmniButton</c> 类名 → Button + 时间特征 Name 兜底），
    /// 任一条在某个 root 上唯一命中即记录该 ClockRegion。
    /// <b>UIA 失败即放弃</b>（不加几何兜底）：返回空 → 命中始终失败 → 一律放行给系统。
    /// </summary>
    private static void EnumerateWin11ClockRects(List<ClockRegion> sink)
    {
        // ---------- UI Automation ----------
        try
        {
            CollectWin11ClockViaUia(sink);
        }
        catch (Exception)
        {
            // UIA 不可用（无 Desktop 权限 / 任务栏为 XAML 但元素未就绪）：忽略。
            //
            // 不做几何兜底的理由：现代 Windows 任务栏中时钟紧贴「通知区」（TrayButton），
            // 而非紧贴任务栏右缘。以「右缘内缩固定像素 + 固定宽度」得到的条带必然覆盖通知中心，
            // 一旦命中通知中心就会被 ClockClickHook 吞掉点击，导致通知中心打不开。
            // 宁可漏判（用户仍有 Ctrl+Alt+C 热键），也绝不能误吞系统功能。
        }
    }

    /// <summary>
    /// UIA 路径：在每个任务栏 root（<c>Shell_TrayWnd</c> / <c>Shell_SecondaryTrayWnd</c>）下
    /// 依序尝试三条匹配路径，任一条唯一命中即记录该 root 的时钟区域。
    /// 各 root 相互独立处理：主屏命中<b>不会</b>跳过副屏（每个显示器任务栏各有一个时钟）。
    /// </summary>
    private static void CollectWin11ClockViaUia(List<ClockRegion> sink)
    {
        var roots = new List<(IntPtr Hwnd, string Tag)>();
        var main = NativeMethods.FindWindowW(ShellTrayWndClass, null);
        if (main != IntPtr.Zero)
        {
            roots.Add((main, "win11:uia:Shell_TrayWnd"));
        }

        var secondary = NativeMethods.FindWindowW(ShellSecondaryTrayWndClass, null);
        if (secondary != IntPtr.Zero)
        {
            roots.Add((secondary, "win11:uia:Shell_SecondaryTrayWnd"));
        }

        foreach (var (hwnd, tag) in roots)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root == null)
                {
                    continue;
                }

                // 三条路径依序尝试，任一条唯一命中即采用（安全侧：多个候选且无法鉴别时放弃）
                var found = FindUniqueByClockAutomationId(root)
                            ?? FindUniqueOmniButton(root)
                            ?? FindUniqueButtonWithTimeName(root);

                if (found == null)
                {
                    continue;
                }

                var ab = found.Current.BoundingRectangle;
                if (ab.IsEmpty || ab.Width < 8 || ab.Height < 8)
                {
                    continue;
                }

                // BoundingRectangle 为物理像素，与 WH_MOUSE_LL 钩子坐标同系
                // （进程已启用 PerMonitorV2），不做任何 DPI 换算
                var rect = new NativeMethods.RECT
                {
                    Left = (int)Math.Round(ab.Left),
                    Top = (int)Math.Round(ab.Top),
                    Right = (int)Math.Round(ab.Right),
                    Bottom = (int)Math.Round(ab.Bottom)
                };

                sink.Add(new ClockRegion
                {
                    Rect = rect,
                    Monitor = NativeMethods.GetMonitorForWindow(hwnd),
                    Source = tag
                });
            }
            catch (ElementNotAvailableException)
            {
                // 单个根失败不影响其余
            }
        }
    }

    /// <summary>
    /// 路径 1（旧版 / EP Win11 风格任务栏）：<c>AutomationId == "ClockButton"</c>。
    /// 恰好 1 个才采用；0 个或多个（无法鉴别）返回 null 继续下一条路径。
    /// </summary>
    private static AutomationElement? FindUniqueByClockAutomationId(AutomationElement root)
    {
        try
        {
            var matches = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, Win11ClockAutomationId));

            return matches is { Count: 1 } ? matches[0] : null;
        }
        catch (Exception)
        {
            // 单条路径查询失败不应影响其余路径
            return null;
        }
    }

    /// <summary>
    /// 路径 2（新版原生 Win11 任务栏，实测 build 26100.9711）：
    /// <c>ClassName == "SystemTray.OmniButton"</c>。
    /// 恰好 1 个 → 直接采用；多个 → 用 Name 的时间特征正则过滤，过滤后恰好 1 个才采用；0 个 → 放弃。
    /// </summary>
    private static AutomationElement? FindUniqueOmniButton(AutomationElement root)
    {
        try
        {
            var matches = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, Win11ClockButtonClassName));

            if (matches == null || matches.Count == 0)
            {
                return null;
            }

            if (matches.Count == 1)
            {
                return matches[0];
            }

            // 防御未来版本：多个 OmniButton 时用时间特征鉴别，过滤后唯一才采用
            var withTime = matches.Cast<AutomationElement>().Where(MatchesTimePattern).ToList();
            return withTime.Count == 1 ? withTime[0] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 路径 3（跨版本兜底）：<c>ControlType == Button</c> 且 <c>Name</c> 匹配时间正则
    /// <c>\d{1,2}:\d{2}</c>。过滤后恰好 1 个才采用，否则放弃（安全侧，绝不几何猜测）。
    /// </summary>
    private static AutomationElement? FindUniqueButtonWithTimeName(AutomationElement root)
    {
        try
        {
            var matches = root.FindAll(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));

            if (matches == null || matches.Count == 0)
            {
                return null;
            }

            var withTime = matches.Cast<AutomationElement>().Where(MatchesTimePattern).ToList();
            return withTime.Count == 1 ? withTime[0] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>判断 UIA 元素 Name 是否含时间特征（如 "时钟 9:16\n2026/10/29"）。读取失败视为不匹配。</summary>
    private static bool MatchesTimePattern(AutomationElement element)
    {
        try
        {
            string? name = element.Current.Name;
            return !string.IsNullOrEmpty(name) && TimePatternRegex.IsMatch(name);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // 生命周期
    // ------------------------------------------------------------------

    /// <summary>释放定时器与事件订阅。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAutoRefresh();
        DetachDisplayChangeWatcher();
        lock (_sync)
        {
            _cache = new List<ClockRegion>();
        }
    }
}
