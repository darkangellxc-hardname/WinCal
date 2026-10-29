using Microsoft.Win32;

namespace WinCal.Core.Helpers;

/// <summary>
/// 开机自启动工具：通过注册表当前用户 Run 键实现。
///
/// <para><b>品牌统一（P0-10#1）</b>：注册表值名由历史版本 <c>miniCal</c> 改为
/// <b><c>WinCal</c></b>。为兼容从旧版本升级的用户，本类会：</para>
/// <list type="bullet">
///   <item>启用时写入新值名 <c>WinCal</c>，并清理可能残留的旧值名 <c>miniCal</c>；</item>
///   <item>禁用时同时删除新旧两个值名；</item>
///   <item>查询时把旧值名 <c>miniCal</c> 也算作「已启用」，避免升级后误判为未启用。</item>
/// </list>
/// </summary>
public static class StartupHelper
{
    /// <summary>当前注册表值名（品牌统一为 WinCal）。</summary>
    private const string AppName = "WinCal";

    /// <summary>旧注册表值名（一次性兼容读取 / 清理来源）。</summary>
    private const string LegacyAppName = "miniCal";

    private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// 启用开机自启动：写入 <c>WinCal</c> 值并清理残留的旧值 <c>miniCal</c>。
    /// </summary>
    public static void Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            key.SetValue(AppName, Environment.ProcessPath ?? string.Empty);

            // 清理旧品牌残留，避免同一程序出现两条自启项
            if (key.GetValue(LegacyAppName) != null)
            {
                key.DeleteValue(LegacyAppName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: Failed to enable startup: {ex.Message}");
        }
    }

    /// <summary>
    /// 禁用开机自启动：同时删除新值 <c>WinCal</c> 与旧值 <c>miniCal</c>（幂等）。
    /// </summary>
    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;

            key.DeleteValue(AppName, throwOnMissingValue: false);
            key.DeleteValue(LegacyAppName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: Failed to disable startup: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查是否已启用开机自启动：命中新值或旧值（兼容升级场景）且路径匹配当前进程时返回 true。
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            if (key == null) return false;

            string? current = key.GetValue(AppName) as string;
            if (current == Environment.ProcessPath)
            {
                return true;
            }

            // 兼容旧品牌值
            string? legacy = key.GetValue(LegacyAppName) as string;
            return legacy == Environment.ProcessPath;
        }
        catch
        {
            return false;
        }
    }
}
