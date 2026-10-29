using System.Diagnostics;
using WinCal.Core.Models;

namespace WinCal.Core.Services;

/// <summary>
/// 日期信息服务 —— 按<b>用途</b>分流的核心编排层（Q12 / 契约 C12）。
///
/// <para><b>三条解耦数据流（契约 C10）：</b></para>
/// <list type="number">
///   <item><b>休班标记层</b>：消费 <see cref="IcsPurpose.Holiday"/> 源，产出 <see cref="DayMarker"/>。
///         <b>与日程开关完全无关</b> —— 关掉日程，休班标记照常显示。</item>
///   <item><b>节气层</b>：消费 <see cref="IcsPurpose.Almanac"/> 源，产出 <see cref="SolarTerm"/>。</item>
///   <item><b>日程层</b>：消费 <see cref="IcsPurpose.Schedule"/> 源，产出 <see cref="CalendarEvent"/>；默认关停。</item>
/// </list>
///
/// <para><b>数据源诚实化（P0-8）</b>：系统日历在本机不可用（<c>WindowsCalendarService</c> 被
/// <c>&lt;Compile Remove&gt;</c> 排除），本服务<b>绝不静默降级到 Mock 假数据</b>；
/// 不可用事实通过 <see cref="GetDataSourceStatusAsync"/> 如实上报，由 UI 显示横幅。</para>
/// </summary>
public sealed class DateInfoService
{
    private readonly AppSettings _settings;
    private readonly List<IcsCalendarService> _holidaySources = new();
    private readonly List<IcsCalendarService> _almanacSources = new();
    private readonly List<IcsCalendarService> _scheduleSources = new();

    /// <summary>日程层实现（多个 Schedule 源的聚合；无源时为空实现）。</summary>
    private readonly ICalendarService _scheduleService;

    /// <summary>订阅颜色池（日程层与源标识用）。</summary>
    private static readonly string[] SubscriptionColors =
    {
        "#FF6D00", "#0078D4", "#E91E63", "#00897B", "#7B1FA2", "#C62828", "#2E7D32", "#F57F17"
    };

    /// <summary>
    /// 构造日期信息服务：按 <see cref="IcsSubscription.Purpose"/> 把启用中的订阅分组建源。
    /// </summary>
    /// <param name="settings">应用设置（含订阅列表）。</param>
    public DateInfoService(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));

        // 始终先做一次旧字段迁移，保证从旧版本升级时订阅不丢
        _settings.MigrateLegacySubscriptions();

        var enabled = _settings.Subscriptions
            .Where(s => s != null && s.Enabled && !string.IsNullOrWhiteSpace(s.Url))
            .ToList();

        int index = 0;
        foreach (var sub in enabled)
        {
            string color = SubscriptionColors[index % SubscriptionColors.Length];
            index++;

            var service = new IcsCalendarService(sub, _settings.IcsRefreshMinutes, color);

            switch (sub.Purpose)
            {
                case IcsPurpose.Almanac:
                    _almanacSources.Add(service);
                    break;
                case IcsPurpose.Schedule:
                    _scheduleSources.Add(service);
                    break;
                case IcsPurpose.Holiday:
                default:
                    _holidaySources.Add(service);
                    break;
            }
        }

        // 日程层：Mock 仅当显式开关开启；否则聚合 Schedule 源（无源则空实现）
        if (_settings.UseMockData)
        {
#pragma warning disable CS0618 // 显式开启时使用 Mock 是预期行为
            _scheduleService = new MockCalendarService();
#pragma warning restore CS0618
        }
        else if (_scheduleSources.Count > 0)
        {
            _scheduleService = _scheduleSources.Count == 1
                ? _scheduleSources[0]
                : new AggregateCalendarService(_scheduleSources.Cast<ICalendarService>().ToArray());
        }
        else
        {
            _scheduleService = new EmptyCalendarService();
        }
    }

    /// <summary>休班标记源数量（诊断用）。</summary>
    public int HolidaySourceCount => _holidaySources.Count;

    /// <summary>节气 / 黄历源数量（诊断用）。</summary>
    public int AlmanacSourceCount => _almanacSources.Count;

    /// <summary>日程源数量（诊断用）。</summary>
    public int ScheduleSourceCount => _scheduleSources.Count;

    // ------------------------------------------------------------------
    // 休班标记层
    // ------------------------------------------------------------------

    /// <summary>
    /// 获取 [start, end) 内的休 / 班标记，按日期归并为单值字典。
    ///
    /// <para>归并规则（契约 C4）：同一天可命中多条；同名前缀去重，但保留 Kind 不同者。
    /// 若同一天同时出现休与班（理论冲突），<b>优先休（法定假日）</b>，
    /// 并把另一个名称并入 <see cref="DayMarker.Name"/> 供排查。</para>
    /// </summary>
    /// <param name="start">范围起点（含）。</param>
    /// <param name="end">范围终点（不含）。</param>
    public async Task<Dictionary<DateTime, DayMarker>> GetDayMarkersAsync(DateTime start, DateTime end)
    {
        var raws = new List<HolidayMarkerRaw>();

        foreach (var source in _holidaySources)
        {
            try
            {
                // 触发（可能的后台）加载；同步提取只在内存已就绪时有效
                await source.EnsureLoadedAsync();
                raws.AddRange(source.GetHolidayMarkers(start, end));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WinCal: 休班标记源 [{source.CalendarName}] 失败: {ex.Message}");
            }
        }

        return MergeMarkers(raws);
    }

    /// <summary>把多条原始标记归并为「日期 → 单值标记」。</summary>
    private static Dictionary<DateTime, DayMarker> MergeMarkers(List<HolidayMarkerRaw> raws)
    {
        var result = new Dictionary<DateTime, DayMarker>();

        // 先按日期分组，再在组内做「同名前缀去重 + Kind 合并」
        foreach (var group in raws.GroupBy(r => r.Date.Date))
        {
            var day = group.Key;

            // 同名前缀去重，但保留 Kind 不同的条目（契约 §3.1 第 3 步）
            var deduped = new List<HolidayMarkerRaw>();
            var seenKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in group)
            {
                var prefix = GetNamePrefix(r.Name);
                var key = $"{prefix}|{r.Kind}";
                if (seenKeys.Add(key))
                {
                    deduped.Add(r);
                }
            }

            // 冲突时优先休（法定假日）
            var chosen = deduped.FirstOrDefault(r => r.Kind == DayKind.Holiday) ?? deduped[0];

            // 名称合并（去重、保序）
            var names = deduped
                .Select(r => r.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            result[day] = new DayMarker(
                day,
                chosen.Kind,
                names.Count > 0 ? string.Join("/", names) : chosen.Name);
        }

        return result;
    }

    /// <summary>取名称前缀（第一个全/半角左括号之前），用于同日去重。</summary>
    private static string GetNamePrefix(string? name)
    {
        var s = name ?? string.Empty;
        int i1 = s.IndexOf('（');
        int i2 = s.IndexOf('(');
        int idx;
        if (i1 < 0) idx = i2;
        else if (i2 < 0) idx = i1;
        else idx = Math.Min(i1, i2);

        return idx > 0 ? s[..idx] : s;
    }

    // ------------------------------------------------------------------
    // 节气层
    // ------------------------------------------------------------------

    /// <summary>
    /// 获取 [start, end) 内的节气（日期 → 节气名）。超出订阅覆盖年份时返回空字典
    /// （不报错、不回退寿星公式，契约 C6）。
    /// </summary>
    public async Task<Dictionary<DateTime, string>> GetSolarTermsAsync(DateTime start, DateTime end)
    {
        var result = new Dictionary<DateTime, string>();

        foreach (var source in _almanacSources)
        {
            try
            {
                await source.EnsureLoadedAsync();
                foreach (var term in source.GetSolarTerms(start, end))
                {
                    // 首个源优先（多源时后到不覆盖已有）
                    result.TryAdd(term.Date.Date, term.Name);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WinCal: 节气源 [{source.CalendarName}] 失败: {ex.Message}");
            }
        }

        return result;
    }

    // ------------------------------------------------------------------
    // 黄历层（P2 预留）
    // ------------------------------------------------------------------

    /// <summary>
    /// 获取 [start, end) 内的黄历数据。<b>P2 数据预留，本次不做 UI</b>（契约 C11）。
    /// </summary>
    public async Task<Dictionary<DateTime, AlmanacInfo>> GetAlmanacAsync(DateTime start, DateTime end)
    {
        var result = new Dictionary<DateTime, AlmanacInfo>();

        foreach (var source in _almanacSources)
        {
            try
            {
                await source.EnsureLoadedAsync();
                foreach (var info in source.GetAlmanac(start, end))
                {
                    result.TryAdd(info.Date.Date, info);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WinCal: 黄历源 [{source.CalendarName}] 失败: {ex.Message}");
            }
        }

        return result;
    }

    // ------------------------------------------------------------------
    // 日程层（默认关停）
    // ------------------------------------------------------------------

    /// <summary>
    /// 获取 [start, end) 内的日程事件。<b>仅当 <see cref="AppSettings.EnableSchedule"/> 为 true</b>
    /// 时才有数据；否则返回空列表（日程层默认关停，且不影响休班标记）。
    /// </summary>
    public async Task<List<CalendarEvent>> GetScheduleEventsAsync(DateTime start, DateTime end)
    {
        if (!_settings.EnableSchedule && !_settings.UseMockData)
        {
            return new List<CalendarEvent>();
        }

        try
        {
            return await _scheduleService.GetEventsAsync(start, end);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: 日程层获取失败: {ex.Message}");
            return new List<CalendarEvent>();
        }
    }

    /// <summary>打开日程层对应的系统应用（设置页「打开日历」入口用）。</summary>
    public void OpenScheduleApp() => _scheduleService.OpenSystemCalendarApp();

    // ------------------------------------------------------------------
    // 数据源诚实状态（P0-8）
    // ------------------------------------------------------------------

    /// <summary>
    /// 获取数据源诚实状态：哪些源不可用、为什么、以及面板横幅文案。
    /// <b>绝不静默降级</b> —— 不可用必须显式暴露。
    /// </summary>
    public async Task<DataSourceStatus> GetDataSourceStatusAsync()
    {
        var status = new DataSourceStatus
        {
            // 本机 WindowsCalendarService 被 <Compile Remove> 排除（缺 Win10 SDK），系统日历不可用
            IsSystemCalendarUnavailable = !_settings.UseMockData
        };

        int totalSubs = _holidaySources.Count + _almanacSources.Count + _scheduleSources.Count;

        // 逐源探测可用性
        foreach (var source in _holidaySources)
        {
            status.Items.Add(await ProbeSourceAsync(source, IcsPurpose.Holiday));
        }

        foreach (var source in _almanacSources)
        {
            status.Items.Add(await ProbeSourceAsync(source, IcsPurpose.Almanac));
        }

        foreach (var source in _scheduleSources)
        {
            status.Items.Add(await ProbeSourceAsync(source, IcsPurpose.Schedule));
        }

        // 数据源标签
        if (_settings.UseMockData)
        {
            status.DataSourceLabel = "模拟数据（已显式开启）";
        }
        else if (totalSubs > 0)
        {
            status.DataSourceLabel = $"ICS 订阅（{totalSubs} 个源）";
        }
        else
        {
            status.DataSourceLabel = "未配置任何订阅";
        }

        // 横幅文案（诚实标注，不静默）
        var banners = new List<string>();

        if (_settings.UseMockData)
        {
            banners.Add("⚠ 当前为模拟数据（设置中已开启「使用模拟数据」），内容非真实日程。");
        }
        else
        {
            if (status.IsSystemCalendarUnavailable)
            {
                banners.Add("系统日历在本机不可用（缺少 Windows SDK），已改用 ICS 订阅提供数据。");
            }

            if (totalSubs == 0)
            {
                banners.Add("尚未配置任何 ICS 订阅，休班与节气信息将无法显示。请在设置中添加上「节假日」与「黄历」订阅。");
            }
            else
            {
                var broken = status.Items.Where(i => !i.IsAvailable).Select(i => i.Name).Distinct().ToList();
                if (broken.Count > 0)
                {
                    banners.Add($"以下订阅暂时不可用：{string.Join("、", broken)}。请检查网络或在设置中更新地址。");
                }
            }
        }

        status.BannerText = banners.Count > 0 ? string.Join(" ", banners) : null;

        return status;
    }

    /// <summary>探测单个源的可用性，异常一律转为「不可用 + 原因」而非抛出。</summary>
    private static async Task<DataSourceItem> ProbeSourceAsync(IcsCalendarService source, IcsPurpose purpose)
    {
        try
        {
            bool ok = await source.IsAvailableAsync();
            return new DataSourceItem(source.CalendarName, purpose, ok, ok ? null : "无法加载订阅内容（网络或地址问题）");
        }
        catch (Exception ex)
        {
            return new DataSourceItem(source.CalendarName, purpose, false, ex.Message);
        }
    }

    // ------------------------------------------------------------------
    // 刷新
    // ------------------------------------------------------------------

    /// <summary>强制刷新全部源（忽略缓存）。</summary>
    public async Task ForceRefreshAsync()
    {
        var all = new List<IcsCalendarService>();
        all.AddRange(_holidaySources);
        all.AddRange(_almanacSources);
        all.AddRange(_scheduleSources);

        foreach (var source in all)
        {
            try
            {
                await source.ForceRefreshAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WinCal: 刷新源 [{source.CalendarName}] 失败: {ex.Message}");
            }
        }

        // 刷新后顺带做一次缓存体积治理（T3-a）
        IcsCalendarService.TrimCache();
    }
}
