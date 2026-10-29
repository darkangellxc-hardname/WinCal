using System.Windows;
using System.Windows.Interop;
using WinCal.Core.Interop;

namespace WinCal.Core.Helpers;

/// <summary>
/// 弹出窗口定位工具：把面板放到「目标显示器」工作区的右下角（贴任务栏上方）。
///
/// <para><b>★ 多显示器 + 混合 DPI 实测结论（阶段A 探针，2026-09-20）：</b></para>
/// <list type="bullet">
///   <item><c>Window.Left/Top</c> 是「相对虚拟桌面原点的绝对 DIP」，但其换算使用
///         <b>窗口当前所在显示器</b>的缩放，且 WPF 对跨屏 DPI 的跟踪<b>存在异步滞后</b>。
///         实测：窗口在主屏（150%）时把 <c>Left</c> 设为副屏（100%）目标 DIP，
///         结果落到物理 x≈7992（副屏只到 5760，已飞出屏外）。</item>
///   <item>因此跨显示器定位<b>绝不能</b>靠设置 <c>Left/Top</c>，必须用
///         <see cref="NativeMethods.SetWindowPos"/> 以<b>物理像素</b>直置；
///         <c>SetWindowPos</c> 在 主→副→主 复用序列中精确落位、始终落在目标屏工作区内（实测 5/5 PASS）。</item>
///   <item>定位后必须把物理坐标回写为 DIP（<c>Left = x / scale</c>）同步 WPF 模型，
///         否则下游 DIP 逻辑（如 <c>PopupWindow.AdjustHeightForSchedule</c>）会读到滞后值而把窗口算到屏外
///         （实测：不写回时算出物理 top=449，跑到工作区上方）。</item>
/// </list>
///
/// <para>所有坐标为物理像素（<c>GetMonitorInfoW</c> / <c>SetWindowPos</c> 同坐标系），
/// 与 WH_MOUSE_LL 钩子回调一致（PerMonitorV2 前提）。</para>
/// </summary>
public static class WindowPositionHelper
{
    /// <summary>右侧留白（DIP）。</summary>
    private const double RightMarginDip = 12.0;

    /// <summary>底部留白（DIP）。</summary>
    private const double BottomMarginDip = 8.0;

    /// <summary>
    /// 将窗口定位到目标显示器工作区右下角（贴任务栏上方），并把窗口最大高度限制在工作区内。
    /// 必须在窗口 <c>Show()</c> 之后调用（需要窗口句柄与尺寸）。
    /// </summary>
    /// <param name="window">目标窗口（需已显示，句柄非零）。</param>
    /// <param name="targetMonitor">
    /// 目标显示器句柄。<b>有值</b>时直接以该显示器的 <c>rcWork</c> 定位（修复「点哪屏弹哪屏」）；
    /// <b>为 null</b> 时回退到「窗口当前所在显示器」（<c>MonitorFromWindow</c> + MONITOR_DEFAULTTONEAREST），
    /// 保持旧行为兼容。
    /// </param>
    public static void PositionNearTaskbar(Window window, IntPtr? targetMonitor = null)
    {
        if (window == null)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return; // 尚未创建 HWND（未 Show）：无法定位
        }

        // ---- 1) 解析目标显示器 ----
        IntPtr monitor = targetMonitor ?? NativeMethods.GetMonitorForWindow(hwnd);
        if (!NativeMethods.TryGetMonitorInfo(monitor, out NativeMethods.MONITORINFO info) ||
            !info.rcWork.IsValid)
        {
            // 取不到显示器信息：退化为「窗口当前所在屏」再试一次
            monitor = NativeMethods.GetMonitorForWindow(hwnd);
            if (!NativeMethods.TryGetMonitorInfo(monitor, out info) || !info.rcWork.IsValid)
            {
                return; // 完全取不到：放弃定位（保持窗口原位，绝不乱放）
            }
        }

        // 目标显示器缩放系数（用于把尺寸/留白 DIP→物理，及回写 DIP）
        double scale = NativeMethods.GetDpiScaleForMonitor(monitor);
        if (scale <= 0)
        {
            scale = 1.0;
        }

        var work = info.rcWork;

        // ---- 2) 计算窗口物理尺寸 ----
        double widthDip = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        double heightDip = window.ActualHeight > 0 ? window.ActualHeight : window.Height;

        if (widthDip <= 0 || heightDip <= 0)
        {
            window.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widthDip = window.DesiredSize.Width;
            heightDip = window.DesiredSize.Height;
        }

        if (widthDip <= 0)
        {
            widthDip = window.Width > 0 ? window.Width : 420;
        }

        if (heightDip <= 0)
        {
            heightDip = window.Height > 0 ? window.Height : 620;
        }

        int widthPhys = (int)Math.Round(widthDip * scale);
        int heightPhys = (int)Math.Round(heightDip * scale);
        int bottomMarginPhys = (int)Math.Round(BottomMarginDip * scale);
        int rightMarginPhys = (int)Math.Round(RightMarginDip * scale);

        // 工作区可用高度（物理）
        int workHeightPhys = work.Bottom - work.Top;
        int maxHeightPhys = workHeightPhys - bottomMarginPhys;
        if (maxHeightPhys <= 0)
        {
            maxHeightPhys = workHeightPhys;
        }

        // 限制窗口最大高度不超出工作区（供后续内容自适应 / 缩放参考）
        double maxHeightDip = maxHeightPhys / scale;
        if (window.MaxHeight > 0 && window.MaxHeight < maxHeightDip)
        {
            maxHeightDip = window.MaxHeight;
        }

        window.MaxHeight = maxHeightDip;
        if (heightPhys > maxHeightPhys)
        {
            heightPhys = maxHeightPhys;
        }

        // ---- 3) 计算目标物理左上角（工作区右下角内缩，贴任务栏上方） ----
        int x = work.Right - widthPhys - rightMarginPhys;
        int y = work.Bottom - heightPhys - bottomMarginPhys;

        // ---- 4) 相对「目标屏工作区」钳制（修复原硬编码 left<0→4 在多屏失效的问题） ----
        int maxX = work.Right - widthPhys;
        int maxY = work.Bottom - heightPhys;
        int minX = work.Left;
        int minY = work.Top;
        if (maxX < minX)
        {
            maxX = minX; // 窗口比工作区还宽：左对齐，宁可溢出右缘
        }

        if (maxY < minY)
        {
            maxY = minY;
        }

        x = Math.Clamp(x, minX, maxX);
        y = Math.Clamp(y, minY, maxY);

        // ---- 5) 物理直置（跨屏唯一可靠方式） ----
        NativeMethods.SetWindowPos(
            hwnd,
            IntPtr.Zero,
            x,
            y,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // ---- 6) 回写 WPF DIP，同步其 Left/Top 模型（供 AdjustHeightForSchedule / 缩放使用） ----
        window.Left = x / scale;
        window.Top = y / scale;

        // ---- 7) 复核：若上游异步 / 竞态导致漂移 > 1px，再纠正一次 ----
        if (NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT actual))
        {
            int dx = Math.Abs(actual.Left - x);
            int dy = Math.Abs(actual.Top - y);
            if (dx > 1 || dy > 1)
            {
                NativeMethods.SetWindowPos(
                    hwnd,
                    IntPtr.Zero,
                    x,
                    y,
                    0,
                    0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                window.Left = x / scale;
                window.Top = y / scale;
            }
        }
    }
}
