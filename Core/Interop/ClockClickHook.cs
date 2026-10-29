using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WinCal.Core.Interop;

/// <summary>
/// <c>WH_MOUSE_LL</c> 低级鼠标钩子服务：识别「用户点了任务栏时钟」并抛 <see cref="ClockClicked"/> 事件。
///
/// <para><b>设计要点（对应 D1 / P0-1 / 共享知识 ③）：</b></para>
/// <list type="bullet">
///   <item>运行在<b>后台 STA 线程 + 自有消息循环</b>（<c>GetMessage</c> / <c>TranslateMessage</c> / <c>DispatchMessage</c>），
///         不占用 UI 线程。</item>
///   <item>回调委托实例由字段 <see cref="_proc"/> 持有，<b>防止 GC 回收后回调静默失效</b>。</item>
///   <item>回调内<b>只允许</b>比对已缓存的时钟矩形；<b>禁止</b>窗口枚举 / UIA / 文件 / 网络 IO。</item>
///   <item>命中才返回 <c>(IntPtr)1</c> 吞掉点击；<b>未命中一律 <c>CallNextHookEx</c> 放行</b>（0 误弹）。</item>
///   <item><see cref="Stop"/> / <see cref="Dispose"/> 必须 <c>UnhookWindowsHookEx</c>，避免残留影响用户鼠标。</item>
/// </list>
/// </summary>
public sealed class ClockClickHook : IDisposable
{
    /// <summary>命中后抑制配对 up 的最长等待（毫秒）。超时未收到配对 up 则自动复位，防永久锁死。</summary>
    private const int SuppressUpTimeoutMs = 600;

    // ------------------------------------------------------------------
    // 状态
    // ------------------------------------------------------------------

    private readonly TaskbarClockLocator _locator;

    /// <summary>★ 委托实例必须用字段持有，否则被 GC 回收后回调静默失效（共享知识 ③）。</summary>
    private NativeMethods.LowLevelMouseProc? _proc;

    /// <summary>消息循环所在线程。</summary>
    private Thread? _thread;

    /// <summary>该线程的托管 ID，用于投递 WM_QUIT 终止消息循环。</summary>
    private uint _nativeThreadId;

    /// <summary>钩子句柄。</summary>
    private IntPtr _hook = IntPtr.Zero;

    /// <summary>是否已加载钩子（保证内存可见性）。</summary>
    private volatile bool _installed;

    /// <summary>命中 down 之后抑制配对的 up（避免系统收到半次点击）。</summary>
    private volatile bool _suppressUp;

    /// <summary>最近一次置位 <see cref="_suppressUp"/> 的时刻（<see cref="Environment.TickCount64"/>）。</summary>
    private long _suppressUpTicks;

    /// <summary>投递到 UI 线程的调度器；为 null 时直接在钩子线程上触发事件。</summary>
    private Dispatcher? _uiDispatcher;

    private volatile bool _running;
    private bool _disposed;

    /// <summary>钩子句柄是否有效（调试/诊断用）。</summary>
    public bool IsRunning => _installed && _hook != IntPtr.Zero;

    /// <summary>钩子安装失败时的错误信息（Win32 错误码描述），未失败为 null。</summary>
    public string? LastError { get; private set; }

    // ------------------------------------------------------------------
    // 事件
    // ------------------------------------------------------------------

    /// <summary>命中任务栏时钟并发生左键按下时触发，参数为点击点物理坐标 (x, y)。</summary>
    public event Action<int, int>? ClockClicked;

    // ------------------------------------------------------------------
    // 构造
    // ------------------------------------------------------------------

    /// <summary>
    /// 构造钩子服务。
    /// </summary>
    /// <param name="locator">时钟命中判定器（需已 <see cref="TaskbarClockLocator.Refresh"/> 过缓存）。</param>
    /// <param name="uiDispatcher">
    /// UI 线程调度器。传入时事件经 <c>Dispatcher.BeginInvoke</c> 抛到 UI 线程；
    /// 为 null 时事件在当前钩子线程同步触发（调用方需自行保证安全）。
    /// </param>
    public ClockClickHook(TaskbarClockLocator locator, Dispatcher? uiDispatcher = null)
    {
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _uiDispatcher = uiDispatcher;
    }

    /// <summary>设置/更新 UI 线程调度器（装配层在 UI 就绪后调用）。</summary>
    public void SetUiDispatcher(Dispatcher dispatcher) => _uiDispatcher = dispatcher;

    // ------------------------------------------------------------------
    // 启停
    // ------------------------------------------------------------------

    /// <summary>
    /// 启动钩子：先刷新时钟矩形缓存，再创建后台 STA 线程并安装 <c>WH_MOUSE_LL</c>。
    /// </summary>
    /// <returns>
    /// 钩子是否成功安装。<b>结果直接来自线程内 <c>SetWindowsHookExW</c> 的真实返回值</b>（经结果门闩同步），
    /// 不再依赖 <see cref="IsRunning"/> 的可见性。
    /// </returns>
    public bool Start()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ClockClickHook));
        }

        if (_running)
        {
            return IsRunning;
        }

        // 缓存必须在钩子装载前就绪（回调内不做枚举）
        _locator.Refresh();
        _locator.AttachDisplayChangeWatcher();
        _locator.StartAutoRefresh();

        // ★ D-2 修复（1/3）：运行标志必须在启动线程“之前”置位。
        //   原实现在 ready.Wait 之后才置 _running=true，ThreadProc 可能在 Start() 置位之前
        //   先跑到 while (_running) 读到 false → 消息循环立即退出 → finally 卸载刚装好的钩子
        //   （即「装上又立刻消失」、ELAPSED≈1ms 且 IsRunning=False 的根因）。
        _running = true;
        LastError = null;

        // ★ D-2 修复（2/3）：用结果门闩传递 SetWindowsHookExW 的真实成败，
        //   替代原先「靠 IsRunning 反推安装是否成功」的不确定做法。
        var installResult =
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _thread = new Thread(() => ThreadProc(installResult))
        {
            IsBackground = true,
            Name = "WinCal.ClockClickHook"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        // 等待钩子安装结果（最多 5 秒，避免装配层永久阻塞）
        bool installed = false;
        try
        {
            if (installResult.Task.Wait(TimeSpan.FromSeconds(5)))
            {
                installed = installResult.Task.Result;
            }
        }
        catch (AggregateException)
        {
            installed = false;
        }

        if (!installed)
        {
            // 安装失败：回退运行标志，避免 Stop() 误判为仍在运行
            _running = false;
        }

        // ★ 返回值直接来自线程内 SetWindowsHookExW 的真实结果，不再依赖 IsRunning
        return installed;
    }

    /// <summary>
    /// 停止钩子：投递 WM_QUIT 终止消息循环，等待线程退出，并卸载钩子。
    /// </summary>
    public void Stop()
    {
        if (!_running && !_installed && _thread == null)
        {
            return;
        }

        _running = false;

        // 1) 主动卸载钩子（即便消息循环尚未退出也先摘钩，立刻停止回调）
        UninstallHook();

        // 2) 投递 WM_QUIT 中止 GetMessage 阻塞
        try
        {
            if (_nativeThreadId != 0)
            {
                NativeMethods.PostThreadMessageW(_nativeThreadId, NativeMethods.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            }
        }
        catch (Exception)
        {
            // 忽略：线程可能已自然退出
        }

        // 3) 等待线程结束（最多 2 秒），避免残留
        try
        {
            _thread?.Join(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 忽略
        }

        _thread = null;
        _nativeThreadId = 0;
        _suppressUp = false;

        _locator.StopAutoRefresh();
        _locator.DetachDisplayChangeWatcher();
    }

    // ------------------------------------------------------------------
    // 线程体 + 消息循环
    // ------------------------------------------------------------------

    private void ThreadProc(TaskCompletionSource<bool> installResult)
    {
        _nativeThreadId = NativeMethods.GetCurrentThreadId();

        try
        {
            // 委托实例由字段持有，生命周期与钩子一致
            _proc = HookProc;

            IntPtr hModule = NativeMethods.GetModuleHandleW(null);
            IntPtr hook = NativeMethods.SetWindowsHookExW(
                NativeMethods.WH_MOUSE_LL,
                _proc,
                hModule,
                0);

            if (hook == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                LastError = $"SetWindowsHookExW(WH_MOUSE_LL) 失败，Win32 错误码 {err}（{new System.ComponentModel.Win32Exception(err).Message}）";
                _installed = false;
            }
            else
            {
                // ★ D-2 修复（3/3）：用 Volatile.Write 发布句柄（IntPtr 不能直接标 volatile），
                //   确保 Start() 线程读取 IsRunning 时一定看得到非零句柄。
                Volatile.Write(ref _hook, hook);
                LastError = null;
                _installed = true;
            }
        }
        catch (Exception ex)
        {
            LastError = $"安装鼠标钩子时发生异常：{ex.Message}";
            _installed = false;
        }
        finally
        {
            // 先发布真实安装结果（含内存屏障），再据此决定是否进入消息循环
            installResult.TrySetResult(_installed);
        }

        if (!_installed)
        {
            return;
        }

        // ---------- 消息循环（钩子回调依赖它才能被投递） ----------
        try
        {
            var msg = new NativeMethods.MSG();
            while (_running)
            {
                int ret = NativeMethods.GetMessageW(out msg, IntPtr.Zero, 0, 0);

                // 0 = WM_QUIT；-1 = 错误
                if (ret == 0 || ret == -1)
                {
                    break;
                }

                NativeMethods.TranslateMessage(ref msg);
                NativeMethods.DispatchMessageW(ref msg);
            }
        }
        catch (Exception ex)
        {
            LastError = $"钩子消息循环异常退出：{ex.Message}";
        }
        finally
        {
            UninstallHook();
        }
    }

    private void UninstallHook()
    {
        _installed = false;
        IntPtr hook = Interlocked.Exchange(ref _hook, IntPtr.Zero);
        if (hook != IntPtr.Zero)
        {
            try
            {
                NativeMethods.UnhookWindowsHookEx(hook);
            }
            catch (Exception)
            {
                // 忽略：卸载失败不影响进程退出
            }
        }
    }

    // ------------------------------------------------------------------
    // ★ 钩子回调（性能关键：< 1ms；禁止 IO / 枚举 / UIA）
    // ------------------------------------------------------------------

    /// <summary>
    /// WH_MOUSE_LL 回调。实现严格照架构设计 §3.1 伪码：
    /// <list type="number">
    ///   <item><c>nCode &lt; 0</c> → 直接 <c>CallNextHookEx</c> 返回；</item>
    ///   <item><c>WM_LBUTTONDOWN</c> 且命中 → 置 <c>_suppressUp</c>，经 Dispatcher 抛事件，<c>return (IntPtr)1</c>；</item>
    ///   <item><c>WM_LBUTTONUP</c> 且 <c>_suppressUp</c> → 复位并 <c>return (IntPtr)1</c>；</item>
    ///   <item>其余一律 <c>CallNextHookEx</c> 放行（绝不吞无关点击）。</item>
    /// </list>
    /// </summary>
    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // nCode < 0：必须原样传递，不得处理
        if (nCode < 0)
        {
            return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        // ★ 防御性修复 2：若上一次命中的配对 up 丢失（指针移出屏幕 / 被其它低级钩子吞掉），
        //   抑制态会永久卡住并误吞用户后续无关的左键 UP。此处按超时自动复位。
        //   注意：本调用只复位状态、不吞任何消息，随后仍按原有逻辑继续处理并最终放行。
        ResetSuppressIfStale();

        int msg = wParam.ToInt32();

        if (msg == NativeMethods.WM_LBUTTONDOWN)
        {
            NativeMethods.MSLLHOOKSTRUCT data =
                Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            int x = data.pt.X;
            int y = data.pt.Y;

            // ★ 只比对已缓存的矩形（≤3 条），无枚举 / 无 UIA / 无 IO
            if (_locator.HitTest(x, y))
            {
                _suppressUp = true;
                Volatile.Write(ref _suppressUpTicks, Environment.TickCount64);
                RaiseClockClicked(x, y);

                // 吞掉本次 down（阻止系统日历/系统菜单弹出）
                return (IntPtr)1;
            }
        }
        else if (msg == NativeMethods.WM_LBUTTONUP && _suppressUp)
        {
            // 吞掉配对的 up，避免系统收到「半次点击」
            _suppressUp = false;
            return (IntPtr)1;
        }
        else if (msg == NativeMethods.WM_RBUTTONDOWN && _suppressUp)
        {
            // 极端情况：down 被吞但用户换手/右击，复位抑制态避免永久卡住
            _suppressUp = false;
        }

        // 未命中一律放行
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>抑制态超时自动复位（防止配对的 WM_LBUTTONUP 丢失后永久锁死）。</summary>
    /// <remarks>仅复位状态，<b>不吞任何消息</b>：调用后调用方必须继续走原有的命中判定与放行逻辑。</remarks>
    private void ResetSuppressIfStale()
    {
        if (!_suppressUp)
        {
            return;
        }

        long since = Environment.TickCount64 - Volatile.Read(ref _suppressUpTicks);
        if (since > SuppressUpTimeoutMs)
        {
            _suppressUp = false;
        }
    }

    /// <summary>把命中事件抛到 UI 线程（BeginInvoke，绝不阻塞钩子回调）。</summary>
    private void RaiseClockClicked(int x, int y)
    {
        Dispatcher? dispatcher = _uiDispatcher;
        if (dispatcher != null)
        {
            try
            {
                if (dispatcher.CheckAccess())
                {
                    ClockClicked?.Invoke(x, y);
                }
                else
                {
                    dispatcher.BeginInvoke(
                        DispatcherPriority.Normal,
                        new Action(() => ClockClicked?.Invoke(x, y)));
                }

                return;
            }
            catch (Exception)
            {
                // Dispatcher 已关闭等异常：退化为直接触发
            }
        }

        try
        {
            ClockClicked?.Invoke(x, y);
        }
        catch (Exception)
        {
            // 事件处理器异常绝不允许逃逸到钩子回调（会导致系统摘钩）
        }
    }

    // ------------------------------------------------------------------
    // 释放
    // ------------------------------------------------------------------

    /// <summary>停止钩子并释放全部资源。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        // 释放委托引用（此时钩子已卸载，安全）
        _proc = null;
        _uiDispatcher = null;

        GC.SuppressFinalize(this);
    }
}
