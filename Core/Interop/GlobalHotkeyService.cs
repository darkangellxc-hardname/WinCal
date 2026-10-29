using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace WinCal.Core.Interop;

/// <summary>
/// 全局快捷键服务（默认 <c>Ctrl+Alt+C</c>）：作为「点任务栏时钟」之外的<b>兜底入口</b>（P0-9 / Q8）。
///
/// <para><b>实现要点：</b></para>
/// <list type="bullet">
///   <item>本文件<b>自建隐藏 <c>HwndSource</c></b>（message-only 窗口）挂载 <c>WM_HOTKEY</c>，
///         不依赖 <c>App.xaml.cs</c>（该文件由 T05 独占），因此可独立装配。</item>
///   <item>注册失败<b>绝不静默吞掉</b>（Q8）：通过 <see cref="RegistrationFailed"/> 事件显式上报，
///         并由 <see cref="TryRegister"/> 的 <c>error</c> 出参返回原因。</item>
///   <item>主组合被占用时，允许尝试备选组合，并通过 <see cref="ActiveHotkey"/> 报告<b>实际生效</b>的组合。</item>
///   <item><see cref="Dispose"/> / <see cref="Unregister"/> 必须 <c>UnregisterHotKey</c>。</item>
/// </list>
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    // ------------------------------------------------------------------
    // 常量
    // ------------------------------------------------------------------

    /// <summary>默认主快捷键。</summary>
    public const string DefaultHotkey = "Ctrl+Alt+C";

    /// <summary>主快捷键被占用时按顺序尝试的备选组合。</summary>
    public static readonly IReadOnlyList<string> DefaultFallbacks = new[]
    {
        "Ctrl+Shift+C",
        "Ctrl+Alt+K",
        "Ctrl+Alt+F10"
    };

    /// <summary>热键 ID（单实例服务，固定值即可）。</summary>
    private const int HotkeyId = 0x5701;

    /// <summary>WS_CHILD，配合 <see cref="NativeMethods.HWND_MESSAGE"/> 构成 message-only 窗口。</summary>
    private const int WsChild = 0x40000000;

    // ------------------------------------------------------------------
    // 状态
    // ------------------------------------------------------------------

    private readonly object _sync = new();
    private HwndSource? _source;
    private bool _registered;
    private bool _disposed;

    /// <summary>销毁标记回调交给 <c>HwndSource.Dispose()</c> 使用。</summary>
    private HwndSourceHook? _hookDelegate;

    // ------------------------------------------------------------------
    // 事件与属性
    // ------------------------------------------------------------------

    /// <summary>快捷键被按下（在 <c>WM_HOTKEY</c> 到达的线程上触发；即创建本服务的 UI 线程）。</summary>
    public event Action? Pressed;

    /// <summary>注册失败时触发，参数为人类可读的失败原因（绝不静默，Q8）。</summary>
    public event Action<string>? RegistrationFailed;

    /// <summary>实际注册成功的快捷键组合（未注册为 null）。</summary>
    public string? ActiveHotkey { get; private set; }

    /// <summary>是否已成功注册热键。</summary>
    public bool IsRegistered
    {
        get
        {
            lock (_sync)
            {
                return _registered;
            }
        }
    }

    // ------------------------------------------------------------------
    // 注册
    // ------------------------------------------------------------------

    /// <summary>
    /// 注册指定快捷键。<paramref name="owner"/> 仅用于兼容旧签名（本实现自建隐藏窗口，可传 null）。
    /// </summary>
    /// <param name="hotkey">组合键文本，如 <c>"Ctrl+Alt+C"</c>。</param>
    /// <param name="owner">可选的宿主窗口（本实现不依赖，允许 null）。</param>
    /// <returns>注册成功返回 true；失败返回 false 并触发 <see cref="RegistrationFailed"/>。</returns>
    public bool Register(string hotkey, Window? owner = null)
    {
        _ = owner; // 自建 message-only HwndSource，无需宿主窗口

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GlobalHotkeyService));
        }

        Unregister();

        if (!TryParseHotkey(hotkey, out uint modifiers, out uint vk, out string? parseError))
        {
            string message = $"快捷键「{hotkey}」格式无法解析：{parseError}";
            RaiseRegistrationFailed(message);
            return false;
        }

        if (!EnsureSource())
        {
            string message = $"无法创建热键消息窗口，快捷键「{hotkey}」注册失败。";
            RaiseRegistrationFailed(message);
            return false;
        }

        IntPtr hwnd = _source!.Handle;

        // MOD_NOREPEAT：按住不放不重复触发（避免面板疯狂 toggle）
        bool ok = NativeMethods.RegisterHotKey(hwnd, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, vk);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            string message = $"快捷键「{hotkey}」注册失败：组合已被其它程序占用（Win32 错误码 {err}：{new Win32Exception(err).Message}）。";
            RaiseRegistrationFailed(message);
            return false;
        }

        lock (_sync)
        {
            _registered = true;
            ActiveHotkey = NormalizeHotkey(hotkey);
        }

        return true;
    }

    /// <summary>
    /// 按「主组合 → 备选组合」顺序尝试注册。<b>失败必须显式上报</b>（Q8），不静默降级。
    /// </summary>
    /// <param name="error">全部组合均失败时的原因；成功时为 null。</param>
    /// <returns>是否有任一组合注册成功。</returns>
    public bool TryRegister(out string? error) => TryRegister(DefaultHotkey, DefaultFallbacks, out error);

    /// <summary>
    /// 按「主组合 → <paramref name="fallbacks"/>」顺序尝试注册。
    /// </summary>
    /// <param name="primary">首选快捷键文本。</param>
    /// <param name="fallbacks">备选快捷键文本列表。</param>
    /// <param name="error">全部失败时的原因；成功时为 null。</param>
    /// <returns>是否有任一组合注册成功。</returns>
    public bool TryRegister(string primary, IReadOnlyList<string> fallbacks, out string? error)
    {
        var failures = new List<string>();

        if (Register(primary))
        {
            error = null;
            return true;
        }

        failures.Add(primary);

        foreach (string candidate in fallbacks)
        {
            if (string.Equals(candidate, primary, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (Register(candidate))
            {
                error = null;
                return true;
            }

            failures.Add(candidate);
        }

        error = $"所有快捷键组合均注册失败（已被占用或不可用）：{string.Join("、", failures)}。"
                + $"请在设置中更换快捷键，或关闭占用这些组合的程序。";
        return false;
    }

    /// <summary>注销当前热键（幂等）。</summary>
    public void Unregister()
    {
        lock (_sync)
        {
            if (!_registered)
            {
                ActiveHotkey = null;
                return;
            }

            _registered = false;
            ActiveHotkey = null;
        }

        try
        {
            if (_source != null && _source.Handle != IntPtr.Zero)
            {
                NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
            }
        }
        catch (Exception)
        {
            // 忽略：窗口可能已销毁
        }
    }

    // ------------------------------------------------------------------
    // 隐藏消息窗口
    // ------------------------------------------------------------------

    /// <summary>确保 message-only <see cref="HwndSource"/> 已创建并挂上 <c>WM_HOTKEY</c> 钩子。</summary>
    private bool EnsureSource()
    {
        if (_source != null && _source.Handle != IntPtr.Zero)
        {
            return true;
        }

        try
        {
            var parameters = new HwndSourceParameters("WinCal.HotkeySink")
            {
                // WS_CHILD + 0 尺寸 + 无 WS_VISIBLE：配合 HWND_MESSAGE 父窗口即为 message-only 窗口，永不显示
                WindowStyle = WsChild,
                ExtendedWindowStyle = 0,
                Width = 0,
                Height = 0,
                PositionX = 0,
                PositionY = 0,
                ParentWindow = NativeMethods.HWND_MESSAGE // 成为 message-only 窗口
            };

            _source = new HwndSource(parameters);
            _hookDelegate = WndProc;
            _source.AddHook(_hookDelegate);
            return true;
        }
        catch (Exception ex)
        {
            RaiseRegistrationFailed($"创建热键消息窗口失败：{ex.Message}");
            _source = null;
            _hookDelegate = null;
            return false;
        }
    }

    /// <summary>隐藏窗口的消息过程：捕获 <c>WM_HOTKEY</c> 并触发 <see cref="Pressed"/>。</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            try
            {
                Pressed?.Invoke();
            }
            catch (Exception)
            {
                // 事件处理器异常不得逃逸到消息泵
            }
        }

        return IntPtr.Zero;
    }

    // ------------------------------------------------------------------
    // 解析与辅助
    // ------------------------------------------------------------------

    /// <summary>
    /// 解析组合键文本（如 <c>"Ctrl+Alt+C"</c>）为 Win32 修饰键位掩码与虚拟键码。
    /// 支持 <c>Ctrl/Control</c>、<c>Alt</c>、<c>Shift</c>、<c>Win</c> 修饰键，
    /// 主键支持字母、数字、<c>F1–F24</c> 及其它 <see cref="Key"/> 名称。
    /// </summary>
    /// <param name="hotkey">组合键文本。</param>
    /// <param name="modifiers">输出：MOD_* 位掩码（不含 MOD_NOREPEAT）。</param>
    /// <param name="vk">输出：虚拟键码。</param>
    /// <param name="error">解析失败原因。</param>
    /// <returns>解析成功返回 true。</returns>
    public static bool TryParseHotkey(string hotkey, out uint modifiers, out uint vk, out string? error)
    {
        modifiers = 0;
        vk = 0;
        error = null;

        if (string.IsNullOrWhiteSpace(hotkey))
        {
            error = "组合键为空。";
            return false;
        }

        string[] parts = hotkey.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            error = "组合键为空。";
            return false;
        }

        for (int i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= NativeMethods.MOD_CONTROL;
                    break;
                case "alt":
                    modifiers |= NativeMethods.MOD_ALT;
                    break;
                case "shift":
                    modifiers |= NativeMethods.MOD_SHIFT;
                    break;
                case "win":
                case "windows":
                    modifiers |= NativeMethods.MOD_WIN;
                    break;
                default:
                    error = $"无法识别的修饰键「{parts[i]}」。";
                    return false;
            }
        }

        if (modifiers == 0)
        {
            error = "必须至少包含一个修饰键（Ctrl / Alt / Shift / Win）。";
            return false;
        }

        string keyToken = parts[^1];
        Key key;
        try
        {
            var converter = new KeyConverter();
            object? converted = converter.ConvertFromInvariantString(keyToken);
            if (converted is not Key parsedKey || parsedKey == Key.None)
            {
                error = $"无法识别的主键「{keyToken}」。";
                return false;
            }

            key = parsedKey;
        }
        catch (Exception ex)
        {
            error = $"主键「{keyToken}」解析异常：{ex.Message}";
            return false;
        }

        int virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0)
        {
            error = $"主键「{keyToken}」没有对应虚拟键码。";
            return false;
        }

        vk = (uint)virtualKey;
        return true;
    }

    /// <summary>把组合键文本规范化为「Modifier+Modifier+Key」形式（用于界面展示实际生效组合）。</summary>
    private static string NormalizeHotkey(string hotkey)
    {
        if (!TryParseHotkey(hotkey, out uint modifiers, out uint vk, out _))
        {
            return hotkey;
        }

        var sb = new System.Text.StringBuilder();
        if ((modifiers & NativeMethods.MOD_CONTROL) != 0)
        {
            sb.Append("Ctrl+");
        }

        if ((modifiers & NativeMethods.MOD_ALT) != 0)
        {
            sb.Append("Alt+");
        }

        if ((modifiers & NativeMethods.MOD_SHIFT) != 0)
        {
            sb.Append("Shift+");
        }

        if ((modifiers & NativeMethods.MOD_WIN) != 0)
        {
            sb.Append("Win+");
        }

        Key key = KeyInterop.KeyFromVirtualKey((int)vk);
        sb.Append(key.ToString());
        return sb.ToString();
    }

    private void RaiseRegistrationFailed(string message)
    {
        try
        {
            RegistrationFailed?.Invoke(message);
        }
        catch (Exception)
        {
            // 订阅方异常不得向外扩散
        }
    }

    // ------------------------------------------------------------------
    // 释放
    // ------------------------------------------------------------------

    /// <summary>注销热键并销毁隐藏消息窗口。</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Unregister();

        try
        {
            if (_source != null)
            {
                if (_hookDelegate != null)
                {
                    _source.RemoveHook(_hookDelegate);
                }

                _source.Dispose();
            }
        }
        catch (Exception)
        {
            // 忽略
        }
        finally
        {
            _source = null;
            _hookDelegate = null;
        }

        GC.SuppressFinalize(this);
    }
}
