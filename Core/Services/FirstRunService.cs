using System.Windows;
using WinCal.Core.Helpers;
using WinCal.Core.Interop;

namespace WinCal.Core.Services;

/// <summary>
/// 首启引导状态管理（Q6 + Q7 / P1-6）。负责两件「只发生一次」的事：
/// <list type="number">
///   <item><b>首启提示</b>（<see cref="AppSettings.FirstRunNoticeShown"/>）：告知用户面板由点击任务栏时钟唤起、
///         <b>托盘图标默认隐藏</b>，并给出<b>兜底快捷键</b>，避免用户以为程序没启动。</item>
///   <item><b>开机自启询问</b>（<see cref="AppSettings.AutoStartupAsked"/>）：询问是否随系统启动，
///         用户同意才写入自启项；<b>无论同意与否都置位</b>，绝不重复打扰。</item>
/// </list>
///
/// <para>两条引导可<b>合并为一条</b>提示以减少打扰（首次运行时二者通常同时待办）。</para>
///
/// <para><b>不改变默认值</b>：<see cref="AppSettings.AutoStartup"/> 保持 <c>false</c>（Q7：默认关闭 + 首启询问）。</para>
/// </summary>
public sealed class FirstRunService
{
    private readonly AppSettings _settings;

    /// <summary>构造首启服务。</summary>
    /// <param name="settings">应用设置（读写两个「已引导」标志）。</param>
    public FirstRunService(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>是否需要显示首启提示条（尚未展示过）。</summary>
    public bool ShouldShowNotice => !_settings.FirstRunNoticeShown;

    /// <summary>是否需要询问开机自启（尚未询问过）。</summary>
    public bool ShouldAskAutoStartup => !_settings.AutoStartupAsked;

    /// <summary>是否仍有任一首启引导待办。</summary>
    public bool HasPending => ShouldShowNotice || ShouldAskAutoStartup;

    /// <summary>
    /// 构造首启提示文案：说明面板唤起方式、托盘默认隐藏，并包含<b>兜底快捷键</b>。
    /// </summary>
    /// <param name="effectiveHotkey">实际生效的快捷键；为 null 时使用默认组合文案。</param>
    /// <returns>人类可读的提示文本。</returns>
    public string BuildNoticeText(string? effectiveHotkey)
    {
        string hotkey = string.IsNullOrWhiteSpace(effectiveHotkey)
            ? GlobalHotkeyService.DefaultHotkey
            : effectiveHotkey!;

        return "欢迎使用 WinCal —— 任务栏日历。\n\n"
             + "· 点击任务栏上的「时钟」即可唤起日历面板；再次点击关闭。\n"
             + $"· 也可按快捷键 {hotkey} 唤起面板。\n"
             + "· 托盘图标默认隐藏；如需显示，可在「设置」中开启「显示托盘图标」。\n"
             + "· 退出程序请使用「设置」窗口中的「退出程序」按钮。";
    }

    /// <summary>
    /// 按需执行首启引导。两条引导同时待办时<b>合并为一条</b>弹窗；否则分别处理。
    /// 处理完成后置位对应标志并持久化。绝不重复打扰。
    /// </summary>
    /// <param name="owner">可选宿主窗口（无则无所有者）。</param>
    /// <param name="effectiveHotkey">实际生效的快捷键（供提示文案）。</param>
    public void RunIfNeeded(Window? owner, string? effectiveHotkey)
    {
        bool needNotice = ShouldShowNotice;
        bool needAsk = ShouldAskAutoStartup;

        if (!needNotice && !needAsk)
        {
            return;
        }

        try
        {
            if (needNotice && needAsk)
            {
                // 合并为一条：提示 + 询问（Yes=随系统启动，No=不启用）
                string text = BuildNoticeText(effectiveHotkey) + "\n\n是否随系统启动（开机自启）？";
                var result = ShowMessage(owner, text, "WinCal 首次运行",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    EnableAutoStartup();
                }

                _settings.AutoStartupAsked = true;
                _settings.FirstRunNoticeShown = true;
            }
            else if (needNotice)
            {
                ShowMessage(owner, BuildNoticeText(effectiveHotkey), "WinCal 提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                _settings.FirstRunNoticeShown = true;
            }
            else // needAsk
            {
                var result = ShowMessage(owner,
                    "是否随系统启动（开机自启）？", "WinCal 首次运行",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    EnableAutoStartup();
                }

                _settings.AutoStartupAsked = true;
            }
        }
        finally
        {
            // 无论用户如何选择（甚至弹窗异常），一律置位并落盘，保证不再重复打扰
            _settings.AutoStartupAsked = true;
            _settings.FirstRunNoticeShown = true;
            _settings.Save();
        }
    }

    /// <summary>写入自启项并把设置里的 <see cref="AppSettings.AutoStartup"/> 同步为 true。</summary>
    private void EnableAutoStartup()
    {
        try
        {
            StartupHelper.Enable();
            _settings.AutoStartup = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: 首启启用自启失败: {ex.Message}");
        }
    }

    /// <summary>显示消息框（owner 为 null 时无所有者）。</summary>
    private static MessageBoxResult ShowMessage(
        Window? owner, string text, string caption,
        MessageBoxButton button, MessageBoxImage icon)
    {
        return owner != null
            ? MessageBox.Show(owner, text, caption, button, icon)
            : MessageBox.Show(text, caption, button, icon);
    }
}
