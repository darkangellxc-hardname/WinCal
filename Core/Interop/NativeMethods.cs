using System.Runtime.InteropServices;
using System.Text;

namespace WinCal.Core.Interop;

/// <summary>
/// 全部 Win32 / shcore P/Invoke 声明的唯一来源（单一来源，避免签名散落各处）。
///
/// <para><b>★ 三条硬性约定（实测踩坑，必须遵守）：</b></para>
/// <list type="number">
///   <item>所有指针型参数一律使用 <see cref="IntPtr"/>，<b>不要</b>用 <c>long</c> / <c>int</c>——
///         否则 64 位下溢出，回调静默失效（读不到真实坐标）。</item>
///   <item><c>SetWindowsHookExW</c> 与 <c>CallNextHookEx</c> 显式声明完整的
///         <see cref="DllImportAttribute"/> 签名（含正确返回类型与 <see cref="CharSet"/>），不依赖默认推断。</item>
///   <item>委托类型 <see cref="LowLevelMouseProc"/> 的签名必须与 Win32 完全一致；
///         委托实例必须由调用方用字段持有，防止 GC 回收后回调静默失效。</item>
/// </list>
/// </summary>
public static class NativeMethods
{
    // ==================================================================
    // 常量：钩子 / 鼠标消息 / 热键
    // ==================================================================

    /// <summary>低级鼠标钩子类型（WH_MOUSE_LL）。</summary>
    public const int WH_MOUSE_LL = 14;

    /// <summary>低级键盘钩子类型（备用，本设计未使用）。</summary>
    public const int WH_KEYBOARD_LL = 13;

    /// <summary>鼠标左键按下消息。</summary>
    public const int WM_LBUTTONDOWN = 0x0201;

    /// <summary>鼠标左键抬起消息。</summary>
    public const int WM_LBUTTONUP = 0x0202;

    /// <summary>鼠标右键按下消息（用于复位抑制状态）。</summary>
    public const int WM_RBUTTONDOWN = 0x0204;

    /// <summary>鼠标右键抬起消息。</summary>
    public const int WM_RBUTTONUP = 0x0205;

    /// <summary>鼠标移动消息。</summary>
    public const int WM_MOUSEMOVE = 0x0200;

    /// <summary>全局热键触发的窗口消息。</summary>
    public const int WM_HOTKEY = 0x0312;

    /// <summary>退出消息，post 给线程即可中止其消息循环。</summary>
    public const int WM_QUIT = 0x0012;

    // ==================================================================
    // 常量：热键修饰键
    // ==================================================================

    /// <summary>Alt 修饰键。</summary>
    public const uint MOD_ALT = 0x0001;

    /// <summary>Ctrl 修饰键。</summary>
    public const uint MOD_CONTROL = 0x0002;

    /// <summary>Shift 修饰键。</summary>
    public const uint MOD_SHIFT = 0x0004;

    /// <summary>Win 键修饰键。</summary>
    public const uint MOD_WIN = 0x0008;

    /// <summary>按住不放时不重复触发。</summary>
    public const uint MOD_NOREPEAT = 0x4000;

    // ==================================================================
    // 常量：显示器
    // ==================================================================

    /// <summary>MONITOR_DEFAULTTONEAREST：取距离最近的显示器。</summary>
    public const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>MONITOR_DEFAULTTOPRIMARY：取主显示器。</summary>
    public const uint MONITOR_DEFAULTTOPRIMARY = 1;

    // ==================================================================
    // 常量：SetWindowPos 标志
    // ==================================================================

    /// <summary>保持尺寸不变。</summary>
    public const uint SWP_NOSIZE = 0x0001;

    /// <summary>保持 Z 序不变（不改变置顶状态）。</summary>
    public const uint SWP_NOZORDER = 0x0004;

    /// <summary>不激活窗口（不抢焦点）。</summary>
    public const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>生效 DPI 类型（GetDpiForMonitor 的 dpiType）。</summary>
    public const int MDT_EFFECTIVE_DPI = 0;

    // ==================================================================
    // 常量：DPI 感知上下文（SetProcessDpiAwarenessContext）
    // ==================================================================

    /// <summary>DPI_AWARENESS_CONTEXT_UNAWARE = -1。</summary>
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_UNAWARE = new(-1);

    /// <summary>DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = -2。</summary>
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_SYSTEM_AWARE = new(-2);

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = -3。</summary>
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = new(-3);

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4（本设计目标）。</summary>
    public static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    // ==================================================================
    // 消息专用窗口（HWND_MESSAGE）
    // ==================================================================

    /// <summary>HWND_MESSAGE = (IntPtr)(-3)，用于创建不可见的消息专用窗口。</summary>
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    // ==================================================================
    // 结构体
    // ==================================================================

    /// <summary>坐标点（物理像素）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        /// <summary>X 坐标。</summary>
        public int X;

        /// <summary>Y 坐标。</summary>
        public int Y;

        /// <summary>便捷构造。</summary>
        public POINT(int x, int y)
        {
            X = x;
            Y = y;
        }
    }

    /// <summary>矩形（物理像素，右/下为开区间语义）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        /// <summary>左边界（含）。</summary>
        public int Left;

        /// <summary>上边界（含）。</summary>
        public int Top;

        /// <summary>右边界（不含）。</summary>
        public int Right;

        /// <summary>下边界（不含）。</summary>
        public int Bottom;

        /// <summary>宽度（像素）。</summary>
        public readonly int Width => Right - Left;

        /// <summary>高度（像素）。</summary>
        public readonly int Height => Bottom - Top;

        /// <summary>是否为非退化矩形（宽高均为正）。</summary>
        public readonly bool IsValid => Right > Left && Bottom > Top;

        /// <summary>判断给定物理坐标是否落入本矩形（左/上含、右/下不含）。</summary>
        public readonly bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;

        /// <summary>便于日志输出。</summary>
        public override readonly string ToString() => $"({Left},{Top},{Right},{Bottom})";
    }

    /// <summary>WH_MOUSE_LL 回调的 lParam 结构（MSLLHOOKSTRUCT），坐标为物理像素。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        /// <summary>鼠标屏幕坐标（物理像素）。</summary>
        public POINT pt;

        /// <summary>滚轮/按键附加数据。</summary>
        public uint mouseData;

        /// <summary>事件标志（LLMHF_INJECTED 等）。</summary>
        public uint flags;

        /// <summary>消息时间戳。</summary>
        public uint time;

        /// <summary>附加信息（由产生该事件的驱动/程序提供）。</summary>
        public IntPtr dwExtraInfo;
    }

    /// <summary>显示器信息（MONITORINFO）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        /// <summary>结构体大小，调用前必须赋值为 <c>Marshal.SizeOf&lt;MONITORINFO&gt;()</c>。</summary>
        public int cbSize;

        /// <summary>显示器完整矩形（物理像素）。</summary>
        public RECT rcMonitor;

        /// <summary>显示器工作区矩形（物理像素，任务栏已被扣除）。</summary>
        public RECT rcWork;

        /// <summary>标志位：MONITORINFOF_PRIMARY = 1。</summary>
        public uint dwFlags;

        /// <summary>是否为主显示器。</summary>
        public readonly bool IsPrimary => (dwFlags & 1u) != 0u;
    }

    /// <summary>窗口消息（MSG）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        /// <summary>目标窗口句柄。</summary>
        public IntPtr hwnd;

        /// <summary>消息标识。</summary>
        public uint message;

        /// <summary>消息 wParam。</summary>
        public IntPtr wParam;

        /// <summary>消息 lParam。</summary>
        public IntPtr lParam;

        /// <summary>消息时间。</summary>
        public uint time;

        /// <summary>消息产生时的屏幕坐标。</summary>
        public POINT pt;

        /// <summary>保留字段（与原生 MSG 布局对齐，x64 下 sizeof = 48）。</summary>
        public uint lPrivate;
    }

    // ==================================================================
    // 委托类型（签名必须与 Win32 完全一致）
    // ==================================================================

    /// <summary>
    /// 低级鼠标钩子回调（HOOKPROC）。所有指针型参数均为 <see cref="IntPtr"/>，
    /// 严禁改为 <c>long</c>/<c>int</c>（64 位溢出会导致回调静默失效）。
    /// </summary>
    /// <param name="nCode">钩子代码，&lt; 0 时必须直接 CallNextHookEx。</param>
    /// <param name="wParam">消息标识（WM_LBUTTONDOWN 等）。</param>
    /// <param name="lParam">指向 <see cref="MSLLHOOKSTRUCT"/> 的指针。</param>
    /// <returns>返回 <c>(IntPtr)1</c> 表示吞掉消息；否则返回 CallNextHookEx 的结果。</returns>
    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    /// <summary>EnumWindows / EnumChildWindows 回调。</summary>
    /// <param name="hWnd">当前枚举到的窗口句柄。</param>
    /// <param name="lParam">调用方传入的自定义参数。</param>
    /// <returns>返回 true 继续枚举，false 终止。</returns>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // ==================================================================
    // P/Invoke：钩子（★ 显式完整签名，勿改）
    // ==================================================================

    /// <summary>
    /// 安装钩子。WH_MOUSE_LL 为全局低级钩子，<paramref name="dwThreadId"/> 传 0，
    /// <paramref name="hMod"/> 传当前模块句柄（<c>GetModuleHandleW(null)</c>）。
    /// 安装该钩子的线程必须运行消息循环。
    /// </summary>
    [DllImport("user32.dll",
        EntryPoint = "SetWindowsHookExW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        CallingConvention = CallingConvention.Winapi)]
    public static extern IntPtr SetWindowsHookExW(
        int idHook,
        LowLevelMouseProc lpfn,
        IntPtr hMod,
        uint dwThreadId);

    /// <summary>卸载钩子。退出程序前必须调用，避免残留影响用户鼠标。</summary>
    [DllImport("user32.dll",
        EntryPoint = "UnhookWindowsHookEx",
        SetLastError = true,
        CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    /// <summary>
    /// 把消息传递给钩子链中的下一个钩子。<b>未命中一律调用此方法放行</b>，
    /// 切勿吞掉无关点击。<paramref name="lParam"/> 必须是 <see cref="IntPtr"/>。
    /// </summary>
    [DllImport("user32.dll",
        EntryPoint = "CallNextHookEx",
        SetLastError = true,
        CallingConvention = CallingConvention.Winapi)]
    public static extern IntPtr CallNextHookEx(
        IntPtr hhk,
        int nCode,
        IntPtr wParam,
        IntPtr lParam);

    // ==================================================================
    // P/Invoke：消息循环
    // ==================================================================

    /// <summary>从线程消息队列取消息（阻塞）。返回 0 表示收到 WM_QUIT。</summary>
    [DllImport("user32.dll",
        EntryPoint = "GetMessageW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        CallingConvention = CallingConvention.Winapi)]
    public static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    /// <summary>翻译虚拟键消息。</summary>
    [DllImport("user32.dll",
        EntryPoint = "TranslateMessage",
        SetLastError = true,
        CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    /// <summary>派发消息到目标窗口过程。</summary>
    [DllImport("user32.dll",
        EntryPoint = "DispatchMessageW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        CallingConvention = CallingConvention.Winapi)]
    public static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    /// <summary>向指定线程投递消息（用于投递 WM_QUIT 中止其消息循环）。</summary>
    [DllImport("user32.dll",
        EntryPoint = "PostThreadMessageW",
        SetLastError = true,
        CharSet = CharSet.Unicode,
        CallingConvention = CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostThreadMessageW(uint idThread, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>取当前线程 ID。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern uint GetCurrentThreadId();

    /// <summary>取模块句柄（<paramref name="lpModuleName"/> 为 null 时返回宿主 exe 模块句柄）。</summary>
    [DllImport("kernel32.dll",
        EntryPoint = "GetModuleHandleW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string? lpModuleName);

    // ==================================================================
    // P/Invoke：窗口枚举 / 类名 / 矩形
    // ==================================================================

    /// <summary>按类名 / 标题查找顶层窗口。返回第一个匹配项，失败返回 <see cref="IntPtr.Zero"/>。</summary>
    [DllImport("user32.dll",
        EntryPoint = "FindWindowW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    /// <summary>枚举全部顶层窗口。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>枚举指定窗口的全部子窗口（<paramref name="hWndParent"/> 传 0 等价于 EnumWindows）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>取窗口类名（Unicode）。返回写入字符数（不含结尾 NUL）。</summary>
    [DllImport("user32.dll",
        EntryPoint = "GetClassNameW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    public static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    /// <summary>取窗口矩形（物理像素）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    /// <summary>取包含指定屏幕点的窗口句柄。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr WindowFromPoint(POINT point);

    /// <summary>取当前前台窗口句柄。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetForegroundWindow();

    // ==================================================================
    // P/Invoke：显示器
    // ==================================================================

    /// <summary>取包含指定窗口的显示器句柄。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    /// <summary>取包含指定点的显示器句柄。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>取显示器信息（Unicode 版）。</summary>
    [DllImport("user32.dll",
        EntryPoint = "GetMonitorInfoW",
        SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    /// <summary>
    /// 取指定显示器的 DPI（shcore）。<paramref name="dpiType"/> 用 <see cref="MDT_EFFECTIVE_DPI"/>。
    /// 返回 0 表示成功（S_OK），非 0 为 HRESULT 错误。仅 Win8.1+ 可用。
    /// </summary>
    [DllImport("shcore.dll", SetLastError = true)]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // ==================================================================
    // P/Invoke：窗口定位 / 光标
    // ==================================================================

    /// <summary>
    /// 改变窗口位置 / 尺寸 / Z 序。多显示器 + 混合 DPI 下，
    /// <b>物理像素定位（跨屏）必须用此 API</b>：WPF 的 <c>Window.Left/Top</c> 是 DIP，
    /// 跨显示器换算会使用「窗口当前所在屏」的缩放而错位（实测）。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int X,
        int Y,
        int cx,
        int cy,
        uint uFlags);

    /// <summary>取当前鼠标光标的物理坐标（用于把「无点击坐标」入口映射到光标所在显示器）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    // ==================================================================
    // P/Invoke：全局热键
    // ==================================================================

    /// <summary>注册全局热键。<paramref name="fsModifiers"/> 为 MOD_* 组合，<paramref name="vk"/> 为虚拟键码。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    /// <summary>注销全局热键。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ==================================================================
    // P/Invoke：DPI 感知（D2 修复）
    // ==================================================================

    /// <summary>
    /// shcore.SetProcessDpiAwareness：0=Unaware，1=System，2=PerMonitor。
    /// 必须在任何窗口创建之前调用（App.OnStartup 最早处）。返回 HRESULT。
    /// </summary>
    [DllImport("shcore.dll", SetLastError = true)]
    public static extern int SetProcessDpiAwareness(int value);

    /// <summary>
    /// user32.SetProcessDpiAwarenessContext：Win10 1703+ 推荐方式，
    /// 传 <see cref="DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2"/>。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    /// <summary>user32.SetProcessDPIAware：最老的兜底方式（Vista+）。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetProcessDPIAware();

    /// <summary>取指定窗口的 DPI（96 = 100%）。仅 Win10 1607+ 可用。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    // ==================================================================
    // 便捷辅助（非 P/Invoke）
    // ==================================================================

    /// <summary>
    /// 尝试设置 PerMonitorV2 DPI 感知。按「最新 API → 次新 API → 兜底 API」顺序尝试，
    /// 全部失败返回 false（此时依赖 app.manifest 的声明）。
    /// </summary>
    /// <returns>是否有任一方式调用成功。</returns>
    public static bool TryEnablePerMonitorV2()
    {
        // 1) Win10 1703+：SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)
        try
        {
            if (SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
            {
                return true;
            }
        }
        catch (EntryPointNotFoundException)
        {
            // 系统过旧，继续尝试下一档
        }
        catch (DllNotFoundException)
        {
            // 忽略
        }

        // 2) Win8.1+：shcore.SetProcessDpiAwareness(2)（PerMonitor）
        try
        {
            const int E_ACCESSDENIED = unchecked((int)0x80070005);
            int hr = SetProcessDpiAwareness(2);
            if (hr == 0)
            {
                return true;
            }

            // 已经设置过会返回 E_ACCESSDENIED，此时视为成功（清单已生效）
            if (hr == E_ACCESSDENIED)
            {
                return true;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }

        // 3) 最终兜底：SetProcessDPIAware（System 感知，聊胜于无）
        try
        {
            if (SetProcessDPIAware())
            {
                return true;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }

        return false;
    }

    /// <summary>读取窗口类名，失败或空返回 <see cref="string.Empty"/>。</summary>
    public static string GetClassName(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(256);
        int len = GetClassNameW(hWnd, buffer, buffer.Capacity);
        return len > 0 ? buffer.ToString(0, len) : string.Empty;
    }

    /// <summary>取窗口所在显示器句柄（MONITOR_DEFAULTTONEAREST）。</summary>
    public static IntPtr GetMonitorForWindow(IntPtr hWnd) => MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);

    /// <summary>取包含指定物理点的显示器句柄（MONITOR_DEFAULTTONEAREST）。</summary>
    /// <param name="x">物理像素 X（来自钩子回调 / GetCursorPos）。</param>
    /// <param name="y">物理像素 Y。</param>
    /// <returns>显示器句柄。</returns>
    public static IntPtr GetMonitorForPoint(int x, int y)
        => MonitorFromPoint(new POINT(x, y), MONITOR_DEFAULTTONEAREST);

    /// <summary>取光标所在显示器句柄；取光标失败时回退主显示器。</summary>
    /// <returns>显示器句柄（保证非 <see cref="IntPtr.Zero"/>，除非系统完全无显示器）。</returns>
    public static IntPtr GetCursorMonitor()
    {
        if (GetCursorPos(out POINT pt))
        {
            return MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        }

        // 兜底：光标不可用时取主显示器
        return MonitorFromPoint(new POINT(0, 0), MONITOR_DEFAULTTOPRIMARY);
    }

    /// <summary>
    /// 取指定显示器的 DPI 缩放系数（1.0 = 100%）。优先 shcore.GetDpiForMonitor；
    /// 失败（系统过旧）时回退 1.0。
    /// </summary>
    /// <param name="hMonitor">显示器句柄。</param>
    /// <returns>缩放系数（dpiX / 96.0）。</returns>
    public static double GetDpiScaleForMonitor(IntPtr hMonitor)
    {
        try
        {
            if (hMonitor != IntPtr.Zero &&
                GetDpiForMonitor(hMonitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 &&
                dpiX > 0)
            {
                return dpiX / 96.0;
            }
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }

        return 1.0;
    }

    /// <summary>取显示器的完整 / 工作区信息；失败返回 false。</summary>
    /// <param name="hMonitor">显示器句柄。</param>
    /// <param name="info">输出显示器信息（<c>cbSize</c> 由本方法填充）。</param>
    /// <returns>是否成功。</returns>
    public static bool TryGetMonitorInfo(IntPtr hMonitor, out MONITORINFO info)
    {
        info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (hMonitor == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            return GetMonitorInfo(hMonitor, ref info);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>取窗口的 DPI 缩放系数（1.0 = 100%）。失败时返回 1.0。</summary>
    public static double GetDpiScale(IntPtr hWnd)
    {
        try
        {
            if (hWnd != IntPtr.Zero)
            {
                uint dpi = GetDpiForWindow(hWnd);
                if (dpi > 0)
                {
                    return dpi / 96.0;
                }
            }
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }

        return 1.0;
    }
}
