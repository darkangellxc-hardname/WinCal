using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinCal.Core.Services;

/// <summary>
/// 应用设置持久化服务，存储到 <c>%LOCALAPPDATA%\WinCal\settings.json</c>。
///
/// <para><b>目录统一（P0-10#2）</b>：历史版本把设置写在 <c>%LOCALAPPDATA%\miniCal</c>，
/// 本类在首次加载时执行一次性迁移：<c>miniCal</c> → <c>WinCal</c>，
/// 并把原目录改名为 <c>miniCal.bak</c>（保留用户数据，绝不删除）。</para>
/// </summary>
public class AppSettings
{
    /// <summary>当前数据目录名（品牌统一为 WinCal）。</summary>
    private const string AppFolderName = "WinCal";

    /// <summary>旧数据目录名（一次性迁移来源）。</summary>
    private const string LegacyFolderName = "miniCal";

    private static readonly string LocalAppData =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly string SettingsDir = Path.Combine(LocalAppData, AppFolderName);
    private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");
    private static readonly string LegacySettingsDir = Path.Combine(LocalAppData, LegacyFolderName);
    private static readonly string LegacySettingsPath = Path.Combine(LegacySettingsDir, "settings.json");
    private static readonly string LegacyBackupDir = Path.Combine(LocalAppData, LegacyFolderName + ".bak");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// 用途推断关键词（用于把 ICS 订阅归类到「农历 / 节气 / 黄历」数据流）。
    /// 对 <c>url + alias</c> 拼接后做大小写不敏感匹配，命中任一即视为 <see cref="IcsPurpose.Almanac"/>。
    /// 统一放置便于后续维护（如新增 provider 域名时只改此处）。
    /// </summary>
    private static readonly string[] AlmanacKeywords =
    {
        "almanac", "农历", "黄历", "lunar", "huangli"
    };

    // ==================================================================
    // 设置项（对应设计 §3 类图，共 12 项新增 + 原有项）
    // ==================================================================

    /// <summary>#1 颜色主题：浅色/深色/跟随系统。</summary>
    public ThemeMode ThemeMode { get; set; } = ThemeMode.FollowSystem;

    /// <summary>#2 字体大小偏移（-2, -1, 0, +1, +2）。</summary>
    public int FontSizeOffset { get; set; } = 0;

    /// <summary>#3 开机自启动。</summary>
    public bool AutoStartup { get; set; } = false;

    /// <summary>#3.1 是否已询问过自启（Q7：首启询问只问一次）。</summary>
    public bool AutoStartupAsked { get; set; } = false;

    /// <summary>#4 数据源类型。</summary>
    public DataSourceType DataSource { get; set; } = DataSourceType.IcsUrl;

    /// <summary>#4 ICS 订阅列表（含用途标记，Q12/C12）。</summary>
    public List<IcsSubscription> Subscriptions { get; set; } = new();

    /// <summary>#4 .ics 刷新频率（分钟）。</summary>
    public int IcsRefreshMinutes { get; set; } = 30;

    /// <summary>#4.1 托盘图标是否显示（默认隐藏，Q/P0-2）。</summary>
    public bool ShowTrayIcon { get; set; } = false;

    /// <summary>#4.2 是否启用日程层（默认关闭，用户不要日程管理）。</summary>
    public bool EnableSchedule { get; set; } = false;

    /// <summary>#4.3 日程区是否展开。</summary>
    public bool ScheduleExpanded { get; set; } = false;

    /// <summary>#4.4 面板宽度（DIP，默认 420）。</summary>
    public double PanelWidth { get; set; } = 420;

    /// <summary>#4.5 面板高度（DIP，默认 620）。</summary>
    public double PanelHeight { get; set; } = 620;

    /// <summary>#4.6 全局快捷键（默认 Ctrl+Alt+C，P0-9 兜底入口）。</summary>
    public string StartupHotkey { get; set; } = "Ctrl+Alt+C";

    /// <summary>#4.7 是否使用模拟数据（默认关闭；<b>仅显式开启才生效</b>，P0-8）。</summary>
    public bool UseMockData { get; set; } = false;

    /// <summary>#5 近期事件显示天数。</summary>
    public int UpcomingDays { get; set; } = 3;

    /// <summary>#6 周起始日（默认改为周一，修正「默认值不一致」缺陷）。</summary>
    public WeekStartDay WeekStartDay { get; set; } = WeekStartDay.Monday;

    /// <summary>#10 显示顶部时间日期（原先无人引用，现已实现）。</summary>
    public bool ShowHeaderDateTime { get; set; } = true;

    /// <summary>#11 时间显示格式（原先无人引用，现已实现）。</summary>
    public TimeFormatMode TimeFormat { get; set; } = TimeFormatMode.FollowSystem;

    /// <summary>#12 首启提示是否已展示（P1-6）。</summary>
    public bool FirstRunNoticeShown { get; set; } = false;

    /// <summary>#13 面板是否固定到屏幕（固定后点击别处不自动关闭）。</summary>
    public bool PinPanel { get; set; } = false;

    // ==================================================================
    // 遗留兼容字段（仅供反序列化旧 settings.json；运行时以 Subscriptions 为准）
    // ==================================================================

    /// <summary>
    /// 【遗留】旧版 ICS URL 列表。反序列化后由 <see cref="MigrateLegacySubscriptions"/> 合成为
    /// <see cref="Subscriptions"/>（用途按 URL / 别名关键词推断，见 <see cref="InferPurpose"/>），随后置空。
    ///
    /// <para>迁移后值为 <c>null</c>：加 <see cref="JsonIgnoreAttribute"/>（<c>WhenWritingNull</c>）
    /// 使该键在落盘时<b>被彻底略去</b>（不再输出 <c>"IcsUrls": null</c> 空壳）。
    /// 仅作用于本属性，<b>不影响</b>其它属性的序列化行为。</para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? IcsUrls { get; set; }

    /// <summary>【遗留】旧版 ICS 别名列表，与 <see cref="IcsUrls"/> 一一对应。
    /// 与 <see cref="IcsUrls"/> 同理：为 <c>null</c> 时落盘**不写出**该键。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? IcsAliases { get; set; }

    // ==================================================================
    // 加载 / 保存
    // ==================================================================

    /// <summary>
    /// 加载设置。顺序：数据目录迁移 → 读取 <c>WinCal\settings.json</c> →
    /// 兼容读取旧 <c>miniCal\settings.json</c> → 订阅迁移 → 返回。
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            MigrateLegacyFolderIfNeeded();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: 数据目录迁移失败（忽略，继续加载）: {ex.Message}");
        }

        AppSettings? settings = null;

        try
        {
            // 1) 优先读取新目录
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            }

            // 2) 新目录不存在时，兼容读取旧目录（迁移失败或备份目录场景）
            if (settings == null && File.Exists(LegacySettingsPath))
            {
                var json = File.ReadAllText(LegacySettingsPath);
                settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                System.Diagnostics.Debug.WriteLine("WinCal: 已从旧 miniCal 目录兼容读取设置");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: Failed to load settings: {ex}");
        }

        settings ??= new AppSettings();

        // 3) 旧字段 → 新订阅列表（向后兼容，用户数据不丢）；用途按关键词推断。
        //    返回 true 表示本次确实发生了迁移（走了 legacy 且确有订阅被合成）。
        bool changed = settings.MigrateLegacySubscriptions();

        // 4) 幂等纠正已落盘的错误用途（Holiday → Almanac），无论是否走过 legacy 迁移都执行。
        changed |= settings.RepairMisclassifiedSubscriptions();

        // 5) 仅当本次确实产生变更时才写回磁盘（幂等：第二次启动 legacy 已清空、用途已正确，
        //    changed 必为 false，不再重复写盘）。避免「迁移只在内存生效、磁盘永不更新」。
        if (changed)
        {
            settings.Save();
            System.Diagnostics.Debug.WriteLine("WinCal: 检测到设置迁移/纠正，已写回 settings.json");
        }

        return settings;
    }

    /// <summary>
    /// 把旧的 <see cref="IcsUrls"/> / <see cref="IcsAliases"/> 两个平行列表合成为
    /// <see cref="Subscriptions"/>，然后清空遗留字段。
    ///
    /// <para><b>用途推断（P0-5 修复）</b>：旧字段没有用途信息，本方法按 URL / 别名关键词
    /// 调用 <see cref="InferPurpose"/> 推断（Almanac 关键词 → <see cref="IcsPurpose.Almanac"/>，
    /// 其余 → <see cref="IcsPurpose.Holiday"/>），<b>不再一律硬编码 Holiday</b>，否则
    /// 「农历 / 节气」源会被错误归入节假日流导致节气永远为空。</para>
    ///
    /// <para>若新字段已有值，则只清空遗留字段、不覆盖。</para>
    /// </summary>
    /// <returns>
    /// 本次是否<b>实际发生了变更</b>：走了 legacy（<see cref="IcsUrls"/> 非空）且确有订阅被合成 → <c>true</c>；
    /// 无 legacy、或 legacy 为空 → <c>false</c>。<see cref="Load"/> 依此决定是否写回磁盘。
    /// </returns>
    public bool MigrateLegacySubscriptions()
    {
        bool changed = false;
        bool hasLegacy = IcsUrls != null && IcsUrls.Count > 0;

        if (hasLegacy)
        {
            if (Subscriptions.Count == 0)
            {
                var aliases = IcsAliases ?? new List<string>();

                for (int i = 0; i < IcsUrls!.Count; i++)
                {
                    var url = IcsUrls[i];
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    var rawAlias = i < aliases.Count ? aliases[i] : string.Empty;
                    var alias = string.IsNullOrWhiteSpace(rawAlias) ? InferAlias(url) : rawAlias;
                    var purpose = InferPurpose(url, alias);

                    Subscriptions.Add(new IcsSubscription(url.Trim(), alias, purpose, true));
                    changed = true;

                    System.Diagnostics.Debug.WriteLine($"WinCal: 迁移旧订阅 {url.Trim()} → {purpose}");
                }

                System.Diagnostics.Debug.WriteLine($"WinCal: 已迁移 {Subscriptions.Count} 条旧 ICS 订阅（用途按关键词推断）");
            }
        }

        // 清空遗留字段，避免下次保存时残留
        IcsUrls = null;
        IcsAliases = null;

        return changed;
    }

    /// <summary>
    /// 幂等纠正已落盘的错误用途：把被误标为 <see cref="IcsPurpose.Holiday"/>、
    /// 但实际按关键词应属 <see cref="IcsPurpose.Almanac"/> 的订阅就地修正为 Almanac。
    ///
    /// <para>只纠正「Holiday → Almanac」这一个已知错误方向，<b>不改动</b>用户手动设为
    /// <see cref="IcsPurpose.Schedule"/> 的项，也不把 Almanac 反向改回 Holiday。</para>
    /// </summary>
    /// <returns>是否发生了变更（true 表示至少修正了一条）。</returns>
    private bool RepairMisclassifiedSubscriptions()
    {
        bool changed = false;

        foreach (var sub in Subscriptions)
        {
            if (sub == null)
            {
                continue;
            }

            if (sub is { Purpose: IcsPurpose.Holiday } &&
                InferPurpose(sub.Url, sub.Alias) == IcsPurpose.Almanac)
            {
                sub.Purpose = IcsPurpose.Almanac;
                changed = true;
                System.Diagnostics.Debug.WriteLine(
                    $"WinCal: 已纠正订阅用途 {sub.Url} → {IcsPurpose.Almanac}（原误标 Holiday）");
            }
        }

        return changed;
    }

    /// <summary>
    /// 按 URL / 别名关键词推断订阅用途。对 <c>url + " " + alias</c> 拼接后做
    /// 大小写不敏感匹配：命中 <see cref="AlmanacKeywords"/> 任一 → <see cref="IcsPurpose.Almanac"/>；
    /// 其余 → <see cref="IcsPurpose.Holiday"/>（默认）。
    /// </summary>
    /// <param name="url">订阅 URL。</param>
    /// <param name="alias">订阅显示别名（可为空）。</param>
    private static IcsPurpose InferPurpose(string url, string alias)
    {
        string haystack = $"{url} {alias}";

        foreach (var keyword in AlmanacKeywords)
        {
            if (haystack.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return IcsPurpose.Almanac;
            }
        }

        return IcsPurpose.Holiday;
    }

    /// <summary>
    /// 保存设置到 <c>%LOCALAPPDATA%\WinCal\settings.json</c>。
    /// </summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: Failed to save settings: {ex}");
        }
    }

    /// <summary>
    /// 一次性迁移：把 <c>%LOCALAPPDATA%\miniCal</c> 整体改名到
    /// <c>%LOCALAPPDATA%\WinCal</c>（若后者尚不存在），或者把原目录备份为 <c>miniCal.bak</c>。
    ///
    /// <para>策略（保证用户数据不丢）：</para>
    /// <list type="number">
    ///   <item>旧目录不存在 → 无事可做。</item>
    ///   <item>旧目录存在、新目录不存在 → 直接把 <c>miniCal</c> 改名为 <c>WinCal</c>（settings.json + cache 一并迁移）。</item>
    ///   <item>新旧目录都存在 → 不改名，仅把旧目录备份为 <c>miniCal.bak</c>（避免覆盖新目录已有数据），
    ///         旧 settings.json 由 <see cref="Load"/> 的兼容读取兜底。</item>
    ///   <item>旧目录已存在但已备份过（<c>miniCal.bak</c> 存在）→ 跳过，保证幂等。</item>
    /// </list>
    /// </summary>
    private static void MigrateLegacyFolderIfNeeded()
    {
        if (!Directory.Exists(LegacySettingsDir))
        {
            return;
        }

        // 已备份过 → 幂等跳过
        if (Directory.Exists(LegacyBackupDir))
        {
            return;
        }

        if (!Directory.Exists(SettingsDir))
        {
            // 情况 2：直接改名（最干净，用户设置与缓存一并迁移）
            Directory.Move(LegacySettingsDir, SettingsDir);
            System.Diagnostics.Debug.WriteLine($"WinCal: 数据目录已迁移 {LegacySettingsDir} → {SettingsDir}");
            return;
        }

        // 情况 3：新目录已存在 → 备份旧目录，保留数据留痕
        Directory.Move(LegacySettingsDir, LegacyBackupDir);
        System.Diagnostics.Debug.WriteLine($"WinCal: 旧数据目录已备份 {LegacySettingsDir} → {LegacyBackupDir}");
    }

    /// <summary>从 URL 域名推断订阅别名（旧数据无别名时使用）。</summary>
    private static string InferAlias(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
            if (host.StartsWith("calendar.", StringComparison.OrdinalIgnoreCase)) host = host["calendar.".Length..];
            if (host.StartsWith("cal.", StringComparison.OrdinalIgnoreCase)) host = host[4..];
            return string.IsNullOrWhiteSpace(host) ? "ICS 订阅" : host;
        }
        catch
        {
            return "ICS 订阅";
        }
    }
}

/// <summary>颜色主题。</summary>
public enum ThemeMode
{
    /// <summary>跟随系统。</summary>
    FollowSystem,

    /// <summary>浅色。</summary>
    Light,

    /// <summary>深色。</summary>
    Dark
}

/// <summary>数据源类型。</summary>
public enum DataSourceType
{
    /// <summary>系统日历（本机不可用，UI 需诚实标注）。</summary>
    SystemCalendar,

    /// <summary>ICS 订阅。</summary>
    IcsUrl,

    /// <summary>两者兼用。</summary>
    Both
}

/// <summary>周起始日。</summary>
public enum WeekStartDay
{
    /// <summary>周日。</summary>
    Sunday,

    /// <summary>周一（默认）。</summary>
    Monday
}

/// <summary>时间显示格式。</summary>
public enum TimeFormatMode
{
    /// <summary>跟随系统。</summary>
    FollowSystem,

    /// <summary>12 小时制。</summary>
    Hour12,

    /// <summary>24 小时制。</summary>
    Hour24
}
