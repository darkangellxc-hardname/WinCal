using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WinCal.Core.Models;
using Ical.Net;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;
using WinCalEvent = WinCal.Core.Models.CalendarEvent;

namespace WinCal.Core.Services;

/// <summary>
/// 远程 ICS 日历订阅服务 —— 从指定 URL 下载 .ics 文件并解析。
///
/// <para><b>本次改造要点（T02）：</b></para>
/// <list type="bullet">
///   <item>构造改吃 <see cref="IcsSubscription"/>（携带「用途」，Q12/C12）。</item>
///   <item>新增 <see cref="GetHolidayMarkers"/> / <see cref="GetSolarTerms"/> / <see cref="GetAlmanac"/>
///         三条独立数据通道，与日程层（<see cref="GetEventsAsync"/>）解耦（C10）。</item>
///   <item><b>按月缓存</b>展开结果，消除翻月重复展开（实测源 B 有 1096 条事件）。</item>
///   <item><c>User-Agent</c> 品牌统一为 <c>WinCal/1.0</c>。</item>
///   <item>预留 <see cref="TrimCache"/> 做缓存体积治理。</item>
/// </list>
/// </summary>
public class IcsCalendarService : ICalendarService
{
    /// <summary>single-source of truth 的缓存目录：<c>%LOCALAPPDATA%\WinCal\cache</c>。</summary>
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCal", "cache");

    /// <summary>缓存体积治理：单文件上限（超过即触发清理）。</summary>
    private const long MaxCacheFileBytes = 2 * 1024 * 1024;

    /// <summary>缓存体积治理：目录内最多保留的缓存文件数。</summary>
    private const int MaxCacheFiles = 10;

    /// <summary>摘要尾部括号休/班正则（契约 C1 第三级 fallback）。</summary>
    private static readonly Regex SummarySuffixRegex = new(@"\(([休班])\)\s*$", RegexOptions.Compiled);

    /// <summary>节气方括号正则（契约 C5）。</summary>
    private static readonly Regex SolarTermRegex = new("【([^】]+)】", RegexOptions.Compiled);

    /// <summary>去除摘要尾部 (休)/(班)/( 全半角 ) 后缀，得到节日名。</summary>
    private static readonly Regex NameSuffixStripRegex = new(@"\s*[（(][休班][)）]\s*$", RegexOptions.Compiled);

    // 黄历标记行正则（C11）
    // 注意：空白匹配一律用 [ \t] 而非 \s —— .NET 的 \s 含换行符，当某字段值为空时
    // \s* 会吃穿换行、把下一行内容捕获进来（典型：🌓 月相： 为空时 80% 的日期会串行捕获
    // 到「🙏 吉神：…」），造成静默脏数据，违反 P0-5「宁缺勿错」。
    private static readonly Regex GanZhiRegex = new(@"🔄[ \t]*干支：[ \t]*(.+)", RegexOptions.Compiled);
    private static readonly Regex YiRegex = new(@"✅[ \t]*宜：[ \t]*(.+)", RegexOptions.Compiled);
    private static readonly Regex JiRegex = new(@"❌[ \t]*忌：[ \t]*(.+)", RegexOptions.Compiled);
    private static readonly Regex ChongShaRegex = new(@"⚔️[ \t]*冲煞：[ \t]*(.+)", RegexOptions.Compiled);
    private static readonly Regex JianChuRegex = new(@"📋[ \t]*建除：[ \t]*(.+)", RegexOptions.Compiled);
    private static readonly Regex XiuRegex = new(@"⭐[ \t]*二十八宿：[ \t]*(.+)", RegexOptions.Compiled);
    // 月相可为空，故用 ([^\r\n]*) 并显式排除换行（原先 (: 后的 \s* 会越行捕获）
    private static readonly Regex MoonPhaseRegex = new(@"🌓[ \t]*月相：[ \t]*([^\r\n]*)", RegexOptions.Compiled);
    private static readonly Regex JiShenRegex = new(@"🙏[ \t]*吉神：[ \t]*(.+)", RegexOptions.Compiled);
    private static readonly Regex XiongShaRegex = new(@"👹[ \t]*凶煞：[ \t]*(.+)", RegexOptions.Compiled);

    // ------------------------------------------------------------------
    // 字段
    // ------------------------------------------------------------------

    private readonly IcsSubscription _subscription;
    private readonly int _refreshMinutes;
    private readonly string _calendarName;
    private readonly string _color;

    // 缓存原始 Calendar 对象（含 RRULE 定义），不缓存展开后的事件
    private Calendar? _cachedCalendar;
    private DateTime _lastRefreshTime = DateTime.MinValue;
    private readonly HttpClient _httpClient;
    private readonly string _diskCachePath; // 磁盘缓存文件路径
    private bool _isBackgroundRefreshing;   // 防止重复后台刷新

    // 按月缓存：key = "yyyy-MM"，value = 该月展开后的轻量条目（展开结果缓存，T4-a）
    private readonly Dictionary<string, List<ExpandedItem>> _monthCache = new();
    private readonly object _monthCacheLock = new();

    /// <summary>订阅用途（由构造注入，决定该源进入哪条数据流）。</summary>
    public IcsPurpose Purpose => _subscription.Purpose;

    /// <summary>订阅别名（展示名）。</summary>
    public string CalendarName => _calendarName;

    // ------------------------------------------------------------------
    // 构造
    // ------------------------------------------------------------------

    /// <summary>
    /// 构造 ICS 订阅服务（设计签名，携「用途」的 <see cref="IcsSubscription"/>）。
    /// </summary>
    /// <param name="subscription">订阅配置（URL / 别名 / 用途 / 启用态）。</param>
    /// <param name="refreshMinutes">刷新频率（分钟）。</param>
    /// <param name="color">事件颜色（日程层用）。</param>
    public IcsCalendarService(IcsSubscription subscription, int refreshMinutes = 30, string color = "#0078D4")
    {
        _subscription = subscription ?? throw new ArgumentNullException(nameof(subscription));

        if (string.IsNullOrWhiteSpace(subscription.Url))
        {
            throw new ArgumentException("订阅 URL 不能为空。", nameof(subscription));
        }

        _refreshMinutes = refreshMinutes;
        _calendarName = string.IsNullOrWhiteSpace(subscription.Alias)
            ? InferNameFromUrl(subscription.Url)
            : subscription.Alias;
        _color = color;

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        // 品牌统一为 WinCal/1.0（P0-10#1；原为 miniCal/1.0）
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "WinCal/1.0 (Calendar Client)");

        // 磁盘缓存路径：%LOCALAPPDATA%/WinCal/cache/{url_hash}.ics
        Directory.CreateDirectory(CacheDir);
        var urlHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subscription.Url)))[..16];
        _diskCachePath = Path.Combine(CacheDir, $"{urlHash}.ics");
    }

    /// <summary>
    /// 【向后兼容重载】旧签名（URL + 别名），用途固定为 <see cref="IcsPurpose.Holiday"/>。
    /// 保留以兼容尚未迁移的调用方，避免一次性破坏编译。
    /// </summary>
    /// <param name="icsUrl">订阅 URL。</param>
    /// <param name="refreshMinutes">刷新频率（分钟）。</param>
    /// <param name="calendarName">订阅别名。</param>
    /// <param name="color">事件颜色。</param>
    public IcsCalendarService(string icsUrl, int refreshMinutes = 30, string calendarName = "ICS 订阅", string color = "#0078D4")
        : this(new IcsSubscription(icsUrl, calendarName, IcsPurpose.Holiday, true), refreshMinutes, color)
    {
    }

    // ------------------------------------------------------------------
    // 日程层（仅用途=Schedule 的源应由上层调用）
    // ------------------------------------------------------------------

    /// <summary>获取指定范围内的事件（日程层）。使用按月缓存的展开结果。</summary>
    public async Task<List<WinCalEvent>> GetEventsAsync(DateTime start, DateTime end)
    {
        await EnsureCacheAsync();

        if (_cachedCalendar == null)
        {
            return new List<WinCalEvent>();
        }

        var items = GetExpandedItems(start, end);

        var events = new List<WinCalEvent>();
        foreach (var item in items)
        {
            try
            {
                var calEvent = ConvertItem(item);
                if (calEvent != null)
                {
                    events.Add(calEvent);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WinCal: Failed to convert ICS occurrence: {ex.Message}");
            }
        }

        // 去重：同日起始的事件，如果标题前缀相同（如"端午节"和"端午节（休）"），只保留跨度最长的版本
        var deduped = new List<WinCalEvent>();
        var groups = events.GroupBy(e => e.StartTime.Date);

        foreach (var group in groups)
        {
            // 按标题前缀分组：取第一个'（'或'('之前的部分作为前缀
            var prefixGroups = group.ToList().GroupBy(e => e.Title.Split('（', '(')[0]);

            foreach (var pg in prefixGroups)
            {
                // 保留跨度最长的（EndTime 最晚的）
                var best = pg.OrderByDescending(e => e.EndTime).First();
                deduped.Add(best);
            }
        }

        return deduped.OrderBy(e => e.StartTime).ToList();
    }

    // ------------------------------------------------------------------
    // 休班标记层（契约 C1 / C3 / C4）
    // ------------------------------------------------------------------

    /// <summary>
    /// 获取指定范围内的休 / 班标记（休班标记层）。
    ///
    /// <para>三级 fallback（契约 C1）：<c>DESCRIPTION</c>(休/班) → <c>X-APPLE-SPECIAL-DAY</c> →
    /// <c>SUMMARY</c> 尾部括号；三级皆失败则<b>丢弃</b>（宁缺勿错）。</para>
    /// <para>按 <c>DTSTART..DTEND-1</c> 展开覆盖的每一天（契约 C3）；同一天可返回多条（契约 C4）。</para>
    /// <para><b>前置条件</b>：请先 <c>await EnsureLoadedAsync()</c>；未加载时返回空列表。</para>
    /// </summary>
    /// <param name="start">范围起点（含）。</param>
    /// <param name="end">范围终点（不含）。</param>
    public List<HolidayMarkerRaw> GetHolidayMarkers(DateTime start, DateTime end)
    {
        var result = new List<HolidayMarkerRaw>();

        if (_cachedCalendar == null)
        {
            return result;
        }

        foreach (var item in GetExpandedItems(start, end))
        {
            var icalEvent = item.Source;
            if (icalEvent == null)
            {
                continue;
            }

            var (kind, evidence) = ResolveKindWithEvidence(
                icalEvent.Description,
                ReadAppleSpecialDay(icalEvent),
                icalEvent.Summary);

            if (kind == DayKind.None)
            {
                // 三级皆失败 → 丢弃（不打标记）
                continue;
            }

            var name = CleanName(icalEvent.Summary);

            // 依 DTSTART..DTEND-1 逐日展开（DTEND 排他，C3）
            foreach (var day in EnumerateCoveredDays(item.Start, item.End))
            {
                if (day >= start.Date && day < end.Date)
                {
                    result.Add(new HolidayMarkerRaw(day, kind, name, _calendarName, evidence));
                }
            }
        }

        return result;
    }

    // ------------------------------------------------------------------
    // 节气层（契约 C5 / C6）
    // ------------------------------------------------------------------

    /// <summary>
    /// 获取指定范围内的节气（节气层）。规则：<c>SUMMARY</c> 命中 <c>【】</c> 即节气日；
    /// 无则非节气。不维护 24 节气名表。超出订阅覆盖年份时返回空集（不报错、不回退）。
    /// </summary>
    /// <param name="start">范围起点（含）。</param>
    /// <param name="end">范围终点（不含）。</param>
    public List<SolarTerm> GetSolarTerms(DateTime start, DateTime end)
    {
        var result = new List<SolarTerm>();

        if (_cachedCalendar == null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in GetExpandedItems(start, end))
        {
            var summary = item.Source?.Summary;
            if (string.IsNullOrEmpty(summary))
            {
                continue;
            }

            var match = SolarTermRegex.Match(summary);
            if (!match.Success)
            {
                continue;
            }

            var name = match.Groups[1].Value.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            var date = item.Start.Date;
            if (date < start.Date || date >= end.Date)
            {
                continue;
            }

            var key = $"{date:yyyy-MM-dd}|{name}";
            if (!seen.Add(key))
            {
                continue;
            }

            result.Add(new SolarTerm(date, name));
        }

        return result.OrderBy(t => t.Date).ToList();
    }

    // ------------------------------------------------------------------
    // 黄历层（C11）
    // ------------------------------------------------------------------

    /// <summary>
    /// 获取指定范围内的黄历数据（宜/忌/冲煞/干支/建除/二十八宿/月相/吉神/凶煞）。
    /// 已用于面板底部黄历详情面板。数据直接来自黄历源的 <c>DESCRIPTION</c>。
    /// </summary>
    /// <param name="start">范围起点（含）。</param>
    /// <param name="end">范围终点（不含）。</param>
    public List<AlmanacInfo> GetAlmanac(DateTime start, DateTime end)
    {
        var result = new List<AlmanacInfo>();

        if (_cachedCalendar == null)
        {
            return result;
        }

        foreach (var item in GetExpandedItems(start, end))
        {
            var description = item.Source?.Description;
            if (string.IsNullOrWhiteSpace(description))
            {
                continue;
            }

            var date = item.Start.Date;
            if (date < start.Date || date >= end.Date)
            {
                continue;
            }

            var info = new AlmanacInfo
            {
                Date = date,
                GanZhi = MatchFirst(GanZhiRegex, description),
                Yi = MatchFirst(YiRegex, description),
                Ji = MatchFirst(JiRegex, description),
                ChongSha = MatchFirst(ChongShaRegex, description),
                JianChu = MatchFirst(JianChuRegex, description),
                Xiu = MatchFirst(XiuRegex, description),
                MoonPhase = NormalizeOptional(MatchFirst(MoonPhaseRegex, description)),
                JiShen = NormalizeOptional(MatchFirst(JiShenRegex, description)),
                XiongSha = NormalizeOptional(MatchFirst(XiongShaRegex, description))
            };

            if (info.HasAny)
            {
                result.Add(info);
            }
        }

        return result.OrderBy(a => a.Date).ToList();
    }

    // ------------------------------------------------------------------
    // 可用性 / 账户 / 刷新
    // ------------------------------------------------------------------

    /// <summary>确保缓存已加载（供上层在调用同步提取方法前预热）。返回是否已加载到日历。</summary>
    public async Task<bool> EnsureLoadedAsync()
    {
        await EnsureCacheAsync();
        return _cachedCalendar != null;
    }

    /// <summary>检查该订阅是否可用（能加载到日历内容）。</summary>
    public async Task<bool> IsAvailableAsync()
    {
        if (string.IsNullOrWhiteSpace(_subscription.Url))
        {
            return false;
        }

        try
        {
            await EnsureCacheAsync();
            return _cachedCalendar != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>获取日历账户信息（用于设置页展示）。</summary>
    public async Task<List<CalendarAccountInfo>> GetCalendarAccountsAsync()
    {
        var available = await IsAvailableAsync();
        return new List<CalendarAccountInfo>
        {
            new(_calendarName, _subscription.Purpose.ToString(), "ics-subscription", _color, available)
        };
    }

    /// <summary>强制刷新：跳过所有缓存，直接从网络下载。</summary>
    public async Task ForceRefreshAsync()
    {
        _isBackgroundRefreshing = false; // 取消可能正在进行的后台刷新
        await RefreshFromNetworkAsync();
    }

    /// <summary>打开该订阅对应的系统应用（即用浏览器打开订阅 URL）。</summary>
    public void OpenSystemCalendarApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_subscription.Url) { UseShellExecute = true });
        }
        catch
        {
            // 打开失败忽略
        }
    }

    // ------------------------------------------------------------------
    // 缓存体积治理（T3-a）
    // ------------------------------------------------------------------

    /// <summary>
    /// 清理磁盘缓存：当单文件 &gt; 2MB 或目录内文件数 &gt; 10 时，删除最旧的缓存文件。
    /// 返回删除的文件数。
    /// </summary>
    public static int TrimCache()
    {
        try
        {
            if (!Directory.Exists(CacheDir))
            {
                return 0;
            }

            var files = new DirectoryInfo(CacheDir)
                .GetFiles("*.ics")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            bool oversize = files.Any(f => f.Length > MaxCacheFileBytes);
            bool overcount = files.Count > MaxCacheFiles;

            if (!oversize && !overcount)
            {
                return 0;
            }

            int deleted = 0;
            // 保留最新 MaxCacheFiles 个；其余删除
            for (int i = MaxCacheFiles; i < files.Count; i++)
            {
                try
                {
                    files[i].Delete();
                    deleted++;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"WinCal: TrimCache 删除失败 {files[i].Name}: {ex.Message}");
                }
            }

            if (deleted > 0)
            {
                Debug.WriteLine($"WinCal: TrimCache 删除 {deleted} 个过期缓存文件");
            }

            return deleted;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: TrimCache 异常: {ex.Message}");
            return 0;
        }
    }

    // ------------------------------------------------------------------
    // 内部：缓存
    // ------------------------------------------------------------------

    /// <summary>
    /// 确保缓存有效。优先级：内存缓存 → 磁盘缓存 → 网络下载。
    /// 磁盘缓存命中后，如已过期则在后台异步刷新（不阻塞调用方）。
    /// </summary>
    private async Task EnsureCacheAsync()
    {
        // 1. 内存缓存仍然有效 → 直接返回（最快路径）
        if (_cachedCalendar != null && (DateTime.Now - _lastRefreshTime).TotalMinutes < _refreshMinutes)
        {
            return;
        }

        // 2. 内存缓存为空 → 尝试从磁盘加载（快速路径）
        if (_cachedCalendar == null)
        {
            await LoadFromDiskCacheAsync();
        }

        // 3. 已有数据（内存或磁盘），检查是否需要后台刷新
        if (_cachedCalendar != null)
        {
            if ((DateTime.Now - _lastRefreshTime).TotalMinutes >= _refreshMinutes && !_isBackgroundRefreshing)
            {
                _ = RefreshFromNetworkAsync(); // 后台刷新，不阻塞
            }

            return;
        }

        // 4. 无任何缓存 → 必须同步网络下载（仅首次启动会发生）
        await RefreshFromNetworkAsync();
    }

    /// <summary>从本地磁盘缓存文件加载 ICS 数据（毫秒级）。</summary>
    private async Task LoadFromDiskCacheAsync()
    {
        try
        {
            if (!File.Exists(_diskCachePath))
            {
                return;
            }

            var icsContent = await File.ReadAllTextAsync(_diskCachePath);
            if (string.IsNullOrWhiteSpace(icsContent))
            {
                return;
            }

            _cachedCalendar = ParseIcsContent(icsContent);
            if (_cachedCalendar != null)
            {
                _lastRefreshTime = File.GetLastWriteTime(_diskCachePath);
                InvalidateMonthCache();
                Debug.WriteLine($"WinCal: Loaded ICS from disk cache ({_cachedCalendar.Events.Count} events, cached at {_lastRefreshTime:HH:mm:ss})");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: Failed to load disk cache: {ex.Message}");
        }
    }

    /// <summary>从网络下载并更新缓存（内存 + 磁盘）。</summary>
    private async Task RefreshFromNetworkAsync()
    {
        _isBackgroundRefreshing = true;
        try
        {
            var icsContent = await DownloadIcsAsync();
            var calendar = ParseIcsContent(icsContent);

            if (calendar != null)
            {
                _cachedCalendar = calendar;
                _lastRefreshTime = DateTime.Now;
                InvalidateMonthCache();

                // 保存到磁盘缓存
                try
                {
                    await File.WriteAllTextAsync(_diskCachePath, icsContent);
                    Debug.WriteLine($"WinCal: ICS disk cache updated ({calendar.Events.Count} events)");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"WinCal: Failed to write disk cache: {ex.Message}");
                }

                Debug.WriteLine($"WinCal: ICS refreshed from network ({calendar.Events.Count} events)");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: Network refresh failed: {ex.Message}");
            // 保留旧缓存（如果有）
            if (_cachedCalendar == null)
            {
                throw;
            }
        }
        finally
        {
            _isBackgroundRefreshing = false;
        }
    }

    /// <summary>下载远程 .ics 文件内容。</summary>
    private async Task<string> DownloadIcsAsync()
    {
        var response = await _httpClient.GetAsync(_subscription.Url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    /// <summary>解析 ICS 文本为 Calendar 对象（保留 RRULE 等重复规则，不展开）。</summary>
    private static Calendar? ParseIcsContent(string icsContent)
    {
        try
        {
            // Ical.Net 已按 RFC 5545 自动处理 fold 展开与文本反转义（契约 T2/C8）
            var calendar = Calendar.Load(icsContent);
            Debug.WriteLine($"WinCal: Parsed ICS calendar with {calendar.Events.Count} event definitions");
            return calendar;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: Failed to parse ICS calendar: {ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------
    // 内部：按月缓存展开
    // ------------------------------------------------------------------

    /// <summary>展开后的轻量条目（保留源事件引用，便于提取标记）。</summary>
    private sealed record ExpandedItem(DateTime Start, DateTime End, IcalEvent? Source);

    private void InvalidateMonthCache()
    {
        lock (_monthCacheLock)
        {
            _monthCache.Clear();
        }
    }

    /// <summary>
    /// 取得 [start, end) 内所有展开条目，命中按月缓存；未命中的月份即时展开并缓存。
    /// </summary>
    private List<ExpandedItem> GetExpandedItems(DateTime start, DateTime end)
    {
        if (_cachedCalendar == null)
        {
            return new List<ExpandedItem>();
        }

        var all = new List<ExpandedItem>();
        var month = new DateTime(start.Year, start.Month, 1);
        var lastMonth = new DateTime(end.Year, end.Month, 1);

        // 保证不越界（end 恰为月初时无需包含该月）
        while (month <= lastMonth)
        {
            all.AddRange(GetMonthItems(month));
            month = month.AddMonths(1);
        }

        // 过滤到查询范围（重叠即命中）并去重
        var result = new List<ExpandedItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var it in all)
        {
            if (it.End <= start || it.Start >= end)
            {
                continue;
            }

            var key = $"{it.Start.Ticks}|{it.End.Ticks}|{it.Source?.Uid}|{it.Source?.Summary}";
            if (seen.Add(key))
            {
                result.Add(it);
            }
        }

        return result;
    }

    /// <summary>取得某月（<paramref name="monthStart"/> 所在月）的展开条目，优先读缓存。</summary>
    private List<ExpandedItem> GetMonthItems(DateTime monthStart)
    {
        var key = $"{monthStart:yyyy-MM}";

        lock (_monthCacheLock)
        {
            if (_monthCache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var built = BuildMonthItems(monthStart);

        lock (_monthCacheLock)
        {
            _monthCache[key] = built;
        }

        return built;
    }

    /// <summary>
    /// 实际展开逻辑：对该月窗口调用一次 <c>GetOccurrences</c>（避免翻月重复展开，T4-a），
    /// 并额外纳入「上月开始但跨入本月」的长事件（非重复事件的边界安全网）。
    /// </summary>
    private List<ExpandedItem> BuildMonthItems(DateTime monthStart)
    {
        var items = new List<ExpandedItem>();
        if (_cachedCalendar == null)
        {
            return items;
        }

        var monthEnd = monthStart.AddMonths(1);

        // (a) 本月窗口内的 occurrences（覆盖 RRULE）
        try
        {
            foreach (var occurrence in _cachedCalendar.GetOccurrences(monthStart, monthEnd))
            {
                var period = occurrence.Period;
                if (period?.StartTime == null)
                {
                    continue;
                }

                var src = occurrence.Source as IcalEvent;
                var s = period.StartTime.AsSystemLocal;
                var e = ResolveItemEnd(period, s, src?.IsAllDay ?? false);

                items.Add(new ExpandedItem(s, e, src));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: GetOccurrences 展开失败: {ex.Message}");
        }

        // (b) 上月开始、跨入本月仍未结束的长事件（GetOccurrences 只返回窗口内起始者）
        foreach (var e in _cachedCalendar.Events)
        {
            if (e?.DtStart?.Value == null)
            {
                continue;
            }

            var s = e.DtStart.Value;
            if (s >= monthStart)
            {
                continue; // 本月起始者已由 (a) 覆盖
            }

            var en = ResolveItemEndFromEvent(e, s);
            if (en > monthStart)
            {
                items.Add(new ExpandedItem(s, en, e));
            }
        }

        // 去重（(a)/(b) 可能重复）
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ExpandedItem>();
        foreach (var it in items)
        {
            var k = $"{it.Start.Ticks}|{it.End.Ticks}|{it.Source?.Uid}|{it.Source?.Summary}";
            if (seen.Add(k))
            {
                result.Add(it);
            }
        }

        return result;
    }

    /// <summary>由 period 解析条目结束时间（DTEND 排他保持不变）。</summary>
    private static DateTime ResolveItemEnd(Period period, DateTime start, bool isAllDay)
    {
        DateTime end;
        if (period.EndTime != null)
        {
            end = period.EndTime.AsSystemLocal;
        }
        else if (period.Duration != default)
        {
            end = start + period.Duration;
        }
        else
        {
            end = isAllDay ? start.AddDays(1) : start.AddHours(1);
        }

        if (isAllDay && end.Date <= start.Date)
        {
            end = start.AddDays(1);
        }

        return end;
    }

    /// <summary>由原始事件解析结束时间（用于长事件边界安全网）。</summary>
    private static DateTime ResolveItemEndFromEvent(IcalEvent ev, DateTime start)
    {
        DateTime end;
        if (ev.DtEnd?.Value != null)
        {
            end = ev.DtEnd.Value;
        }
        else if (ev.Duration != default && ev.Duration > TimeSpan.Zero)
        {
            end = start + ev.Duration;
        }
        else
        {
            end = ev.IsAllDay ? start.AddDays(1) : start.AddHours(1);
        }

        if (ev.IsAllDay && end.Date <= start.Date)
        {
            end = start.AddDays(1);
        }

        return end;
    }

    // ------------------------------------------------------------------
    // 内部：转换与提取
    // ------------------------------------------------------------------

    /// <summary>把展开条目转换为日程事件（日程层）。</summary>
    private WinCalEvent? ConvertItem(ExpandedItem item)
    {
        var icalEvent = item.Source;
        if (icalEvent == null)
        {
            return null;
        }

        var startTime = item.Start;
        var endTime = item.End;
        var isAllDay = icalEvent.IsAllDay;

        var title = !string.IsNullOrWhiteSpace(icalEvent.Summary) ? icalEvent.Summary : "(无标题)";
        var location = icalEvent.Location ?? string.Empty;
        var description = icalEvent.Description ?? string.Empty;

        return new WinCalEvent(
            Title: title,
            StartTime: startTime,
            EndTime: endTime,
            IsAllDay: isAllDay,
            CalendarName: _calendarName,
            Color: _color,
            Location: location,
            Description: description
        );
    }

    /// <summary>
    /// 休 / 班三级 fallback（契约 C1）。三级皆失败返回 <see cref="DayKind.None"/>（丢弃）。
    /// </summary>
    /// <param name="description">DESCRIPTION 原文。</param>
    /// <param name="appleSpecialDay">X-APPLE-SPECIAL-DAY 原文。</param>
    /// <param name="summary">SUMMARY 原文。</param>
    public static DayKind ResolveKind(string? description, string? appleSpecialDay, string? summary)
        => ResolveKindWithEvidence(description, appleSpecialDay, summary).Kind;

    /// <summary>与 <see cref="ResolveKind"/> 同逻辑，额外返回判定证据（用于排查与审计）。</summary>
    private static (DayKind Kind, string Evidence) ResolveKindWithEvidence(
        string? description, string? appleSpecialDay, string? summary)
    {
        // L1: DESCRIPTION 去空白后恰为 "休" / "班"（唯一二值，零歧义，首选）
        var desc = (description ?? string.Empty).Trim();
        if (desc == "休")
        {
            return (DayKind.Holiday, "L1:DESCRIPTION=休");
        }

        if (desc == "班")
        {
            return (DayKind.Workday, "L1:DESCRIPTION=班");
        }

        // L2: X-APPLE-SPECIAL-DAY（标准语义，首选备选）
        var apple = (appleSpecialDay ?? string.Empty).Trim();
        if (string.Equals(apple, "WORK-HOLIDAY", StringComparison.OrdinalIgnoreCase))
        {
            return (DayKind.Holiday, "L2:X-APPLE-SPECIAL-DAY=WORK-HOLIDAY");
        }

        if (string.Equals(apple, "ALTERNATE-WORKDAY", StringComparison.OrdinalIgnoreCase))
        {
            return (DayKind.Workday, "L2:X-APPLE-SPECIAL-DAY=ALTERNATE-WORKDAY");
        }

        // L3: SUMMARY 尾部括号 (休) / (班)（兜底）
        var sum = (summary ?? string.Empty).Trim();
        var match = SummarySuffixRegex.Match(sum);
        if (match.Success)
        {
            var ch = match.Groups[1].Value;
            return ch == "休"
                ? (DayKind.Holiday, "L3:SUMMARY=(休)")
                : (DayKind.Workday, "L3:SUMMARY=(班)");
        }

        // 三级皆失败 → 丢弃（宁缺勿错，禁止关键字瞎猜）
        return (DayKind.None, "NONE:三级 fallback 均未命中");
    }

    /// <summary>读取 X-APPLE-SPECIAL-DAY 自定义属性（契约 C1 第二级 fallback 的数据来源）。</summary>
    private static string? ReadAppleSpecialDay(IcalEvent ev)
    {
        try
        {
            foreach (var prop in ev.Properties)
            {
                if (string.Equals(prop.Name, "X-APPLE-SPECIAL-DAY", StringComparison.OrdinalIgnoreCase))
                {
                    return prop.Value?.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"WinCal: 读取 X-APPLE-SPECIAL-DAY 失败: {ex.Message}");
        }

        return null;
    }

    /// <summary>去除摘要尾部 (休)/(班) 后缀得到节日名；无后缀时返回原摘要去空白。注意不改写 <c>CATEGORIES</c>（契约 C2）。</summary>
    private static string CleanName(string? summary)
    {
        var s = (summary ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return string.Empty;
        }

        return NameSuffixStripRegex.Replace(s, string.Empty).Trim();
    }

    /// <summary>依 DTSTART..DTEND-1 逐日枚举（DTEND 排他，契约 C3）。</summary>
    private static IEnumerable<DateTime> EnumerateCoveredDays(DateTime start, DateTime end)
    {
        var s = start.Date;
        var e = end.Date;

        // DTEND 排他：最后一天 = end - 1；若 end <= start 视为单天
        var last = e > s ? e.AddDays(-1) : s;

        for (var d = s; d <= last; d = d.AddDays(1))
        {
            yield return d;
        }
    }

    /// <summary>正则取第一个捕获组，未命中或空返回 null。</summary>
    private static string? MatchFirst(Regex regex, string text)
    {
        try
        {
            var m = regex.Match(text);
            if (!m.Success || m.Groups.Count < 2)
            {
                return null;
            }

            var v = m.Groups[1].Value.Trim();
            return v.Length == 0 ? null : v;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把仅含空白的字符串归一化为 null。</summary>
    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>从 URL 域名推断订阅名。</summary>
    private static string InferNameFromUrl(string url)
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
