using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WinCal.Core.Interop;

namespace WinCal.Core.Helpers;

/// <summary>
/// 面板缩放的九个热区（四边 + 四角 + 无）。
/// </summary>
public enum PanelResizeZone
{
    /// <summary>不在任何热区。</summary>
    None = 0,

    /// <summary>左边（SizeWE）。</summary>
    Left,

    /// <summary>右边（SizeWE）。</summary>
    Right,

    /// <summary>上边（SizeNS）。</summary>
    Top,

    /// <summary>下边（SizeNS）。</summary>
    Bottom,

    /// <summary>左上角（SizeNWSE）。</summary>
    TopLeft,

    /// <summary>右上角（SizeNESW）。</summary>
    TopRight,

    /// <summary>左下角（SizeNESW）。</summary>
    BottomLeft,

    /// <summary>右下角（SizeNWSE）。</summary>
    BottomRight
}

/// <summary>
/// 手工缩放引擎（D6）—— 为 <c>WindowStyle=None</c> + <c>AllowsTransparency=True</c> 的无边框面板
/// 提供自由缩放能力，<b>不使用</b> <c>WindowChrome</c>（与透明窗口组合易出坑）。
///
/// <para><b>锚点规则（P0-3 验收 3 / §8⑧）：</b></para>
/// <list type="bullet">
///   <item>拖动<b>左 / 上边</b>：<c>Left/Top</c> 与 <c>Width/Height</c> 同改 —— 右 / 下边界固定（面板贴住任务栏）。</item>
///   <item>拖动<b>右 / 下边</b>：仅改 <c>Width/Height</c>（左 / 上固定），并钳制不越工作区。</item>
///   <item>钳制：<c>W ∈ [minW, work.W-16]</c>、<c>H ∈ [minH, work.H-16]</c>。</item>
/// </list>
///
/// <para><b>坐标一律使用 DIP</b>：鼠标屏幕坐标经 <c>CompositionTarget.TransformFromDevice</c> 转换，
/// 与 <c>Window.Left/Top/Width/Height</c> 同坐标系（见 §8②）。</para>
/// </summary>
public static class PanelResizeHelper
{
    /// <summary>四边热区宽度（DIP）。</summary>
    private const double EdgeThickness = 6.0;

    /// <summary>四角热区边长（DIP）。</summary>
    private const double CornerSize = 10.0;

    /// <summary>距工作区边缘的最小留白（DIP）。</summary>
    private const double WorkAreaMargin = 16.0;

    /// <summary>结束缩放后的写盘防抖时长（P0-3 验收 4）。</summary>
    private static readonly TimeSpan CommitDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 为窗口挂载缩放能力。
    /// </summary>
    /// <param name="win">目标窗口（无边框、可透明）。</param>
    /// <param name="minW">最小宽度（DIP，如 320）。</param>
    /// <param name="minH">最小高度（DIP，如 420）。</param>
    /// <param name="onCommit">缩放结束后 500ms 防抖回调，用于持久化尺寸（P0-3 验收 4）。</param>
    public static void Attach(Window win, double minW, double minH, Action<double, double> onCommit)
    {
        if (win == null)
        {
            throw new ArgumentNullException(nameof(win));
        }

        // 用事件订阅持有 state（窗口持有委托 → state 不会被 GC），无需额外字段存储
        var state = new ResizeState(win, minW, minH, onCommit ?? ((_, _) => { }));
        win.PreviewMouseMove += state.OnPreviewMouseMove;
        win.PreviewMouseLeftButtonDown += state.OnPreviewMouseDown;
        win.MouseMove += state.OnMouseMove;
        win.PreviewMouseLeftButtonUp += state.OnMouseUp;

        // ★ 防御性修复 1：补齐鼠标捕获的异常释放路径。
        //   仅靠 OnMouseUp 释放捕获时，若拖拽中窗口被隐藏/关闭（拖拽中窗口 Hide/Close，
        //   或面板焦点丢失自动关闭），将永远收不到 MouseUp → _dragging 永久卡 true，
        //   且原生鼠标捕获残留会劫持系统鼠标。以下三个订阅保证任何异常路径都会释放。
        win.LostMouseCapture += state.OnLostMouseCapture;
        win.IsVisibleChanged += state.OnIsVisibleChanged;
        win.Closed += state.OnWindowClosed;
    }

    /// <summary>
    /// 计算给定点落在哪个热区（供引擎与单测调用）。
    /// </summary>
    /// <param name="p">窗口内坐标（DIP，原点在窗口左上）。</param>
    /// <param name="s">窗口尺寸（DIP）。</param>
    /// <returns>命中的热区；不在边界返回 <see cref="PanelResizeZone.None"/>。</returns>
    public static PanelResizeZone HitZone(Point p, Size s)
    {
        if (p.X < 0 || p.Y < 0 || p.X > s.Width || p.Y > s.Height)
        {
            return PanelResizeZone.None;
        }

        bool leftEdge = p.X <= EdgeThickness;
        bool rightEdge = p.X >= s.Width - EdgeThickness;
        bool topEdge = p.Y <= EdgeThickness;
        bool bottomEdge = p.Y >= s.Height - EdgeThickness;

        bool leftCorner = p.X <= CornerSize;
        bool rightCorner = p.X >= s.Width - CornerSize;
        bool topCorner = p.Y <= CornerSize;
        bool bottomCorner = p.Y >= s.Height - CornerSize;

        // 角优先于边（角热区 10×10 覆盖边热区 6）
        if (topCorner && leftCorner) return PanelResizeZone.TopLeft;
        if (topCorner && rightCorner) return PanelResizeZone.TopRight;
        if (bottomCorner && leftCorner) return PanelResizeZone.BottomLeft;
        if (bottomCorner && rightCorner) return PanelResizeZone.BottomRight;

        if (leftEdge) return PanelResizeZone.Left;
        if (rightEdge) return PanelResizeZone.Right;
        if (topEdge) return PanelResizeZone.Top;
        if (bottomEdge) return PanelResizeZone.Bottom;

        return PanelResizeZone.None;
    }

    /// <summary>热区 → 鼠标光标。</summary>
    private static Cursor CursorFor(PanelResizeZone zone) => zone switch
    {
        PanelResizeZone.Left or PanelResizeZone.Right => Cursors.SizeWE,
        PanelResizeZone.Top or PanelResizeZone.Bottom => Cursors.SizeNS,
        PanelResizeZone.TopLeft or PanelResizeZone.BottomRight => Cursors.SizeNWSE,
        PanelResizeZone.TopRight or PanelResizeZone.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.Arrow
    };

    /// <summary>
    /// 单窗口的缩放状态机（每窗口一份）。
    /// </summary>
    private sealed class ResizeState
    {
        private readonly Window _win;
        private readonly double _minW;
        private readonly double _minH;
        private readonly Action<double, double> _onCommit;
        private readonly System.Windows.Threading.DispatcherTimer _commitTimer;

        private bool _dragging;
        private PanelResizeZone _zone = PanelResizeZone.None;

        private double _startLeft;
        private double _startTop;
        private double _startWidth;
        private double _startHeight;
        private double _startScreenX;
        private double _startScreenY;

        public ResizeState(Window win, double minW, double minH, Action<double, double> onCommit)
        {
            _win = win;
            _minW = minW;
            _minH = minH;
            _onCommit = onCommit;

            _commitTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = CommitDelay
            };
            _commitTimer.Tick += (_, _) =>
            {
                _commitTimer.Stop();
                _onCommit(_win.Width, _win.Height);
            };
        }

        /// <summary>把窗口内的鼠标事件坐标换算为屏幕 DIP 坐标。</summary>
        private Point GetScreenDip(MouseEventArgs e)
        {
            // PointToScreen 返回物理像素；TransformFromDevice 把物理像素映射为 DIP
            var device = _win.PointToScreen(e.GetPosition(_win));
            var source = PresentationSource.FromVisual(_win);
            double scaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            double scaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;
            return new Point(device.X * scaleX, device.Y * scaleY);
        }

        /// <summary>取窗口所在显示器的工作区（DIP，L/T/R/B）。</summary>
        private (double Left, double Top, double Right, double Bottom) GetWorkAreaDip()
        {
            var source = PresentationSource.FromVisual(_win);
            double scaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
            double scaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

            try
            {
                var hwnd = new WindowInteropHelper(_win).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    var monitor = NativeMethods.GetMonitorForWindow(hwnd);
                    var info = new NativeMethods.MONITORINFO
                    {
                        cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>()
                    };

                    if (NativeMethods.GetMonitorInfo(monitor, ref info) && info.rcWork.IsValid)
                    {
                        return (
                            info.rcWork.Left * scaleX,
                            info.rcWork.Top * scaleY,
                            info.rcWork.Right * scaleX,
                            info.rcWork.Bottom * scaleY);
                    }
                }
            }
            catch
            {
                // 回退到 WPF 主显示器工作区
            }

            var work = SystemParameters.WorkArea;
            return (work.Left, work.Top, work.Right, work.Bottom);
        }

        /// <summary>未拖动时：更新光标形状。</summary>
        public void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_dragging)
            {
                return;
            }

            var size = new Size(_win.ActualWidth, _win.ActualHeight);
            _zone = HitZone(e.GetPosition(_win), size);
            _win.Cursor = CursorFor(_zone);
        }

        /// <summary>鼠标左键按下：若落在热区则开始拖动。</summary>
        public void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
            {
                return;
            }

            var size = new Size(_win.ActualWidth, _win.ActualHeight);
            _zone = HitZone(e.GetPosition(_win), size);
            if (_zone == PanelResizeZone.None)
            {
                return;
            }

            _dragging = true;
            _startLeft = _win.Left;
            _startTop = _win.Top;
            _startWidth = _win.Width;
            _startHeight = _win.Height;

            var screen = GetScreenDip(e);
            _startScreenX = screen.X;
            _startScreenY = screen.Y;

            _commitTimer.Stop();
            _win.CaptureMouse();
            e.Handled = true;
        }

        /// <summary>拖动中：按热区调整 Left/Top/Width/Height。</summary>
        public void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            var screen = GetScreenDip(e);
            double dX = screen.X - _startScreenX;
            double dY = screen.Y - _startScreenY;
            Apply(dX, dY);
            e.Handled = true;
        }

        /// <summary>松开左键：结束拖动、释放捕获、启动写盘防抖。</summary>
        /// <remarks>
        /// <b>顺序约束（勿改）：</b><c>ReleaseMouseCapture()</c> 会同步触发 <c>LostMouseCapture</c>
        /// → <see cref="CancelDrag"/> 会 <c>_commitTimer.Stop()</c>；因此 <c>_commitTimer.Start()</c>
        /// 必须保持在 <c>ReleaseMouseCapture()</c> <b>之后</b>，写盘防抖才生效。
        /// </remarks>
        public void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging)
            {
                return;
            }

            _dragging = false;
            _win.ReleaseMouseCapture();
            _commitTimer.Stop();
            _commitTimer.Start();
            e.Handled = true;
        }

        /// <summary>中止拖动并释放鼠标捕获（防御性：任何异常路径都必须释放，否则残留捕获会劫持鼠标）。</summary>
        public void CancelDrag()
        {
            // 只有「拖动进行中」被中止时才取消写盘防抖；
            // 若已正常松手（_dragging 为 false），此时 _commitTimer 可能正在等待写盘，
            // 不能被本方法误停（否则尺寸改动丢失，面板回落旧尺寸）。
            bool wasDragging = _dragging;
            _dragging = false;

            if (wasDragging)
            {
                _commitTimer.Stop();
            }

            try
            {
                if (_win.IsMouseCaptured)
                {
                    _win.ReleaseMouseCapture();
                }
            }
            catch (Exception)
            {
                // 窗口可能已销毁
            }
        }

        /// <summary>捕获被系统/他方夺走时中止拖动（例如其它窗口抢焦点）。</summary>
        public void OnLostMouseCapture(object sender, MouseEventArgs e) => CancelDrag();

        /// <summary>窗口被隐藏时中止拖动：<c>PopupWindow</c> 的失焦自动关闭与焦点跟踪均走 <c>Hide()</c>，
        /// 此时不可能再收到 <c>OnMouseUp</c>，必须在此释放，否则拖拽状态永久卡死。</summary>
        public void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            // 窗口被隐藏（PopupWindow.OnDeactivated / 焦点跟踪均走 Hide()）时不可能再收到 OnMouseUp
            if (e.NewValue is false)
            {
                CancelDrag();
            }
        }

        /// <summary>窗口关闭时中止拖动（释放捕获，避免残留劫持鼠标）。</summary>
        public void OnWindowClosed(object? sender, EventArgs e) => CancelDrag();

        /// <summary>
        /// 依据拖动增量与当前热区重算窗口几何（含边界钳制，保证右下角锚点固定）。
        /// </summary>
        private void Apply(double dX, double dY)
        {
            var (workLeft, workTop, workRight, workBottom) = GetWorkAreaDip();

            double maxW = Math.Max(_minW, (workRight - workLeft) - WorkAreaMargin);
            double maxH = Math.Max(_minH, (workBottom - workTop) - WorkAreaMargin);

            double left = _startLeft;
            double top = _startTop;
            double width = _startWidth;
            double height = _startHeight;

            bool growLeft = _zone is PanelResizeZone.Left or PanelResizeZone.TopLeft or PanelResizeZone.BottomLeft;
            bool growRight = _zone is PanelResizeZone.Right or PanelResizeZone.TopRight or PanelResizeZone.BottomRight;
            bool growTop = _zone is PanelResizeZone.Top or PanelResizeZone.TopLeft or PanelResizeZone.TopRight;
            bool growBottom = _zone is PanelResizeZone.Bottom or PanelResizeZone.BottomLeft or PanelResizeZone.BottomRight;

            // 右 / 下边：仅改尺寸，右 / 下边界固定（此处右 / 下即移动边界）
            if (growRight)
            {
                width = Math.Clamp(_startWidth + dX, _minW, maxW);
                left = _startLeft;
                width = Math.Min(width, workRight - left); // 不越工作区右缘
            }

            if (growBottom)
            {
                height = Math.Clamp(_startHeight + dY, _minH, maxH);
                top = _startTop;
                height = Math.Min(height, workBottom - top); // 不越工作区下缘
            }

            // 左 / 上边：右 / 下边界固定，Left/Top 与 Width/Height 同改
            if (growLeft)
            {
                double right = _startLeft + _startWidth;
                width = Math.Clamp(_startWidth - dX, _minW, maxW);
                left = right - width;
                if (left < workLeft)
                {
                    left = workLeft;
                    width = right - left;
                }
            }

            if (growTop)
            {
                double bottom = _startTop + _startHeight;
                height = Math.Clamp(_startHeight - dY, _minH, maxH);
                top = bottom - height;
                if (top < workTop)
                {
                    top = workTop;
                    height = bottom - top;
                }
            }

            _win.Left = left;
            _win.Top = top;
            _win.Width = width;
            _win.Height = height;
        }
    }
}
