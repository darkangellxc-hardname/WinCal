using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WinCal.Core.Helpers;
using WinCal.Core.Interop;
using WinCal.Views;

namespace WinCal.Core.Services;

/// <summary>
/// 面板编排器（装配层解耦，T05）。把「日历面板的创建 / 显示 / 切换 / 定位 / 尺寸持久化」
/// 从 <c>App.xaml.cs</c> 中抽出，使 <c>App</c> 只负责装配与生命周期，不再直接操作窗口细节。
///
/// <para><b>多显示器定位（P0 修复）：</b>定位用 <see cref="WindowPositionHelper.PositionNearTaskbar(Window, IntPtr?)"/>，
/// 并支持传入<b>目标显示器</b>句柄——「点哪块屏的时钟，面板就出现在哪块屏」。
/// 无点击坐标的入口（热键 / 托盘）由装配层传入「光标所在显示器」。</para>
///
/// <para><b>行为与改造前保持一致：</b>显示后调用 <see cref="PopupWindow.StartFocusTracking"/>
/// （焦点丢失自动关闭）。尺寸恢复由 <see cref="PopupWindow"/> 自身从设置读取并持久化。</para>
/// </summary>
public sealed class PopupController : IDisposable
{
    private PopupWindow? _popup;
    private bool _disposed;

    /// <summary>当前面板窗口（可能为 null，表示从未创建或已关闭）；供诊断使用。</summary>
    public PopupWindow? Current => _popup;

    /// <summary>
    /// 显示面板：已显示则激活（必要时重定位到目标屏）；否则（重新）创建并按目标屏工作区定位，随后启动焦点跟踪。
    /// 用于「点任务栏时钟 → 始终显示」场景。
    /// </summary>
    /// <param name="targetMonitor">
    /// 目标显示器句柄；为 null 时由 <see cref="WindowPositionHelper"/> 回退到窗口当前所在屏。
    /// </param>
    public void Show(IntPtr? targetMonitor = null)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_popup != null && _popup.IsVisible)
            {
                // 已显示：若目标屏与窗口当前所在屏不同（如面板赖在旧屏），重新定位后再激活。
                if (targetMonitor.HasValue &&
                    NativeMethods.GetMonitorForWindow(new WindowInteropHelper(_popup).Handle) != targetMonitor.Value)
                {
                    WindowPositionHelper.PositionNearTaskbar(_popup, targetMonitor);
                }

                // ★ 此处【不】调用 ApplyInitialScheduleLayout()：该方法是「首次布局」入口，
                //   非幂等（每调用一次即在当前高度上再 +176 DIP）。已显示的窗口其「展开态」
                //   早已在首次 Show 时应用过；此分支重复调用会让日程高度翻倍。
                //   （面板在屏间移动时物理尺寸由 SetWindowPos 的 SWP_NOSIZE 保持，无需重放布局。）
                _popup.Activate();
                return;
            }

            var popup = CreatePopup();
            popup.Show();
            WindowPositionHelper.PositionNearTaskbar(popup, targetMonitor);

            // ★ 时序修复（方案 C）：定位完成后，再补做「日程展开态」布局。
            //   必须放在 PositionNearTaskbar 之后 —— AdjustHeightForSchedule 按窗口当前所在屏计算，
            //   而 Show() 同步触发的 OnLoaded 早于本次定位（窗口尚在出生位），故不能在 OnLoaded 里做。
            popup.ApplyInitialScheduleLayout();

            popup.Activate();

            // 延迟启动焦点跟踪，给窗口时间获取焦点后再记录前台窗口
            popup.Dispatcher.BeginInvoke(
                DispatcherPriority.Loaded,
                new Action(() => popup.StartFocusTracking()));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: PopupController.Show error: {ex}");
            CloseInternal();
        }
    }

    /// <summary>
    /// 切换面板显隐：未显示则按目标屏显示，已显示则隐藏。
    /// 用于「点任务栏时钟」与「全局快捷键」——用户再点/再按一次应关闭。
    /// </summary>
    /// <param name="targetMonitor">
    /// 目标显示器句柄（新显示时使用）；为 null 时回退窗口当前所在屏。
    /// </param>
    public void Toggle(IntPtr? targetMonitor = null)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (_popup == null || !_popup.IsVisible)
            {
                var popup = CreatePopup();
                popup.Show();
                WindowPositionHelper.PositionNearTaskbar(popup, targetMonitor);

                // ★ 时序修复（方案 C）：定位完成后补做「日程展开态」布局（理由同 Show）。
                popup.ApplyInitialScheduleLayout();

                popup.Activate();
                popup.StartFocusTracking();
            }
            else
            {
                _popup.Hide();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: PopupController.Toggle error: {ex}");
            CloseInternal();
        }
    }

    /// <summary>隐藏面板（不销毁，便于下次快速复用）。</summary>
    public void Hide() => _popup?.Hide();

    /// <summary>
    /// 持久化面板尺寸（装配层契约入口）。
    /// <para>注：<see cref="PopupWindow"/> 内部亦在拖拽结束时经 <c>PanelResizeHelper</c> 写盘；
    /// 本方法提供等价的外部持久化入口，二者写入相同字段、幂等。</para>
    /// </summary>
    /// <param name="width">宽度（DIP）。</param>
    /// <param name="height">高度（DIP）。</param>
    public void SavePanelSize(double width, double height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        try
        {
            var settings = AppSettings.Load();
            settings.PanelWidth = width;
            settings.PanelHeight = height;
            settings.Save();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: PopupController.SavePanelSize error: {ex.Message}");
        }
    }

    /// <summary>创建面板并挂接「关闭即清引用」回调（防止引用旧窗口）。</summary>
    private PopupWindow CreatePopup()
    {
        // 关闭旧窗口（若有），确保每次显示都是干净状态（与改造前一致）
        _popup?.Close();

        var popup = new PopupWindow();
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_popup, popup))
            {
                _popup = null;
            }
        };
        _popup = popup;
        return popup;
    }

    private void CloseInternal()
    {
        try
        {
            _popup?.Close();
        }
        catch (Exception)
        {
            // 忽略：窗口可能已销毁
        }

        _popup = null;
    }

    /// <summary>关闭并释放面板窗口。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CloseInternal();
        GC.SuppressFinalize(this);
    }
}
