using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using WinCal.Core.Helpers;
using WinCal.Core.Models;
using WinCal.Core.Services;

namespace WinCal.ViewModels;

/// <summary>
/// 日历主 ViewModel：月份导航、日期网格生成、三日信息层次（节假日 / 节气 / 农历）聚合、日程三态。
///
/// <para><b>★ T03 关键改造（消灭静默降级）：</b>本类<b>不再</b>持有 <c>ICalendarService</c>，
/// 也<b>不再</b>在反射失败时静默返回模拟数据服务（历史头号缺陷 P0-8/G5）。</para>
/// <list type="number">
///   <item>数据统一由 <see cref="DateInfoService"/> 提供，按「用途」分流出休班标记 / 节气 / 日程三条数据流。</item>
///   <item><see cref="WeekStartDay"/> 以 <see cref="AppSettings.WeekStartDay"/> 为<b>唯一真相</b>，不再硬编码。</item>
///   <item>日程能力保留但默认关停（<see cref="EnableSchedule"/>），受 <see cref="ScheduleExpanded"/> 控制展开态。</item>
/// </list>
/// </summary>
public class CalendarViewModel : INotifyPropertyChanged
{
    // ==================================================================
    // 依赖
    // ==================================================================

    private readonly DateInfoService _dateInfoService;
    private readonly AppSettings _settings;

    // ==================================================================
    // 农历节日名集合（用于把「农历日名」与「农历节日」区分开，渲染优先级不同）
    // ==================================================================

    private static readonly HashSet<string> LunarFestivalNames = new(StringComparer.Ordinal)
    {
        "春节", "元宵节", "端午节", "七夕", "中秋节", "重阳节", "除夕"
    };

    private static readonly ChineseLunisolarCalendar Lunisolar = new();

    private static readonly string[] LunarMonthNames =
    {
        "", "正月", "二月", "三月", "四月", "五月", "六月",
        "七月", "八月", "九月", "十月", "冬月", "腊月"
    };

    private static readonly string[] LunarDayNames =
    {
        "", "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"
    };

    /// <summary>十二生肖（按农历年推算，用于在干支年上标注，如「乙巳[蛇]年」）。</summary>
    private static readonly string[] ZodiacNames =
    {
        "鼠", "牛", "虎", "兔", "龙", "蛇", "马", "羊", "猴", "鸡", "狗", "猪"
    };

    // ==================================================================
    // 字段
    // ==================================================================

    private int _year;
    private int _month;
    private string _monthDisplay = string.Empty;
    private string _yearSecondaryText = string.Empty;
    private DateTime _selectedDate = DateTime.Today;
    private int _selectedDayIndex = -1;
    private bool _isLoading;
    private int _weekStartDay = 1;
    private bool _enableSchedule;
    private bool _scheduleExpanded;
    private string _headerTimeText = string.Empty;
    private string? _bannerText;
    private string _dataSourceLabel = string.Empty;
    private string _holidaySummaryText = string.Empty;
    private bool _bannerDismissed;

    /// <summary>当前月份范围内已归并的休班标记（供速览条 / 跳转复用）。</summary>
    private Dictionary<DateTime, DayMarker> _currentMarkers = new();

    /// <summary>当前网格范围内已归并的黄历数据（供点选时即时取用，无需再异步请求）。</summary>
    private Dictionary<DateTime, AlmanacInfo> _currentAlmanac = new();

    private AlmanacInfo? _selectedDayAlmanac;
    private string _selectedAlmanacTitle = string.Empty;
    private string _selectedAlmanacMeta = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    // ==================================================================
    // 属性
    // ==================================================================

    /// <summary>当前显示的年份。</summary>
    public int Year
    {
        get => _year;
        set { _year = value; OnPropertyChanged(); }
    }

    /// <summary>当前显示的月份（1~12）。</summary>
    public int Month
    {
        get => _month;
        set { _month = value; OnPropertyChanged(); }
    }

    /// <summary>月份显示文本，如 "2026年9月"。</summary>
    public string MonthDisplay
    {
        get => _monthDisplay;
        set { _monthDisplay = value; OnPropertyChanged(); }
    }

    /// <summary>Header 次级文本，如 "农历八月初五"（受本地农历推算驱动）。</summary>
    public string YearSecondaryText
    {
        get => _yearSecondaryText;
        set { _yearSecondaryText = value; OnPropertyChanged(); }
    }

    /// <summary>当前选中日期。</summary>
    public DateTime SelectedDate
    {
        get => _selectedDate;
        set { _selectedDate = value; OnPropertyChanged(); }
    }

    /// <summary>选中日期在 <see cref="CalendarDays"/> 中的索引（-1 表示无选中）。</summary>
    public int SelectedDayIndex
    {
        get => _selectedDayIndex;
        set { _selectedDayIndex = value; OnPropertyChanged(); }
    }

    /// <summary>是否正在加载。</summary>
    public bool IsLoading
    {
        get => _isLoading;
        set { _isLoading = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 周起始日（0=周日, 1=周一）。取值来自 <see cref="AppSettings.WeekStartDay"/>，
    /// 是 UI 与网格生成的<b>单一数据源</b>（修正「硬编码 1」缺陷 P0-10#4）。
    /// </summary>
    public int WeekStartDay
    {
        get => _weekStartDay;
        set { _weekStartDay = value; OnPropertyChanged(); }
    }

    /// <summary>是否启用日程层（默认关闭，P0-7）。关闭时面板底部不显示任何日程痕迹。</summary>
    public bool EnableSchedule
    {
        get => _enableSchedule;
        set
        {
            if (_enableSchedule == value) return;
            _enableSchedule = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSchedule));
            OnPropertyChanged(nameof(IsScheduleListVisible));
            OnPropertyChanged(nameof(ScheduleToggleText));
        }
    }

    /// <summary>日程区是否展开（P0-7 态 B / 态 C 的切换）。</summary>
    public bool ScheduleExpanded
    {
        get => _scheduleExpanded;
        set
        {
            if (_scheduleExpanded == value) return;
            _scheduleExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsScheduleListVisible));
            OnPropertyChanged(nameof(ScheduleToggleText));
        }
    }

    /// <summary>日程折叠栏文本，如 "▸ 日程 (3)"。</summary>
    public string ScheduleToggleText =>
        (ScheduleExpanded ? "\u25BE" : "\u25B8") + $" 日程 ({UpcomingEvents.Count})";

    /// <summary>Header 右侧时间文本（受 <see cref="AppSettings.ShowHeaderDateTime"/> / <see cref="AppSettings.TimeFormat"/> 控制）。</summary>
    public string HeaderTimeText
    {
        get => _headerTimeText;
        set { _headerTimeText = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsHeaderTimeVisible)); }
    }

    /// <summary>是否显示 Header 时间文本。</summary>
    public bool IsHeaderTimeVisible => !string.IsNullOrEmpty(_headerTimeText);

    /// <summary>面板顶部诚实横幅文案（P0-8；为空表示不显示）。</summary>
    public string? BannerText
    {
        get => _bannerText;
        set { _bannerText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasBanner)); }
    }

    /// <summary>是否显示诚实横幅（用户关闭后不再显示）。</summary>
    public bool HasBanner => !_bannerDismissed && !string.IsNullOrWhiteSpace(_bannerText);

    /// <summary>数据源标签（诊断用）。</summary>
    public string DataSourceLabel
    {
        get => _dataSourceLabel;
        set { _dataSourceLabel = value; OnPropertyChanged(); }
    }

    /// <summary>当月放假速览文案（P1-1；为空表示该月无放假安排）。</summary>
    public string HolidaySummaryText
    {
        get => _holidaySummaryText;
        set { _holidaySummaryText = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasHolidaySummary)); }
    }

    /// <summary>是否存在当月放假速览（决定速览条是否占高度）。</summary>
    public bool HasHolidaySummary => !string.IsNullOrWhiteSpace(_holidaySummaryText);

    /// <summary>当前选中日期的黄历数据（无数据时为 null）。</summary>
    public AlmanacInfo? SelectedDayAlmanac
    {
        get => _selectedDayAlmanac;
        private set
        {
            _selectedDayAlmanac = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelectedAlmanac));
        }
    }

    /// <summary>选中日期是否有黄历数据（决定面板显示内容还是"无数据"占位）。</summary>
    public bool HasSelectedAlmanac => _selectedDayAlmanac?.HasAny == true;

    /// <summary>选中日期主标题，如「农历九月十六 · 乙巳[蛇]年 戊子月 乙亥日」。</summary>
    public string SelectedAlmanacTitle
    {
        get => _selectedAlmanacTitle;
        private set { _selectedAlmanacTitle = value; OnPropertyChanged(); }
    }

    /// <summary>选中日期元信息行，如「冲煞：猪日冲蛇 · 建除：闭 · 宿：井木犴 · 月相：望」。</summary>
    public string SelectedAlmanacMeta
    {
        get => _selectedAlmanacMeta;
        private set { _selectedAlmanacMeta = value; OnPropertyChanged(); }
    }

    /// <summary>是否配置了黄历源。为 false 时整个黄历面板隐藏，把高度还给月历网格。</summary>
    public bool HasAlmanacSource => _dateInfoService.AlmanacSourceCount > 0;

    /// <summary>是否显示日程区（等价于 <see cref="EnableSchedule"/>，供 XAML 绑定）。</summary>
    public bool HasSchedule => _enableSchedule;

    /// <summary>是否显示事件列表（开启日程且展开态，P0-7 态 C）。</summary>
    public bool IsScheduleListVisible => _enableSchedule && _scheduleExpanded;

    /// <summary>日历网格数据（6 行 × 7 列 = 42 天，含上下月补位）。</summary>
    public ObservableCollection<CalendarDay> CalendarDays { get; } = new();

    /// <summary>近期日程列表（日程层；仅当 <see cref="EnableSchedule"/> 时填充）。</summary>
    public ObservableCollection<CalendarEvent> UpcomingEvents { get; } = new();

    /// <summary>近期事件天数范围。</summary>
    public int UpcomingDays { get; set; } = 3;

    // ==================================================================
    // 构造
    // ==================================================================

    /// <summary>默认构造：从 <see cref="AppSettings.Load"/> 读取配置。</summary>
    public CalendarViewModel() : this(AppSettings.Load())
    {
    }

    /// <summary>
    /// 使用给定设置构造。内部创建 <see cref="DateInfoService"/> 作为唯一数据入口。
    /// </summary>
    /// <param name="settings">应用设置。</param>
    public CalendarViewModel(AppSettings settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _dateInfoService = new DateInfoService(_settings);

        // WeekStartDay：以设置为唯一真相（不再硬编码 1）
        _weekStartDay = _settings.WeekStartDay == Core.Services.WeekStartDay.Monday ? 1 : 0;

        _enableSchedule = _settings.EnableSchedule;
        _scheduleExpanded = _settings.ScheduleExpanded;
        UpcomingDays = _settings.UpcomingDays;

        HeaderTimeText = FormatHeaderTime(DateTime.Now);

        UpcomingEvents.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ScheduleToggleText));
        };

        NavigateToToday();
    }

    // ==================================================================
    // 导航
    // ==================================================================

    /// <summary>导航到今天。</summary>
    public void NavigateToToday()
    {
        Year = DateTime.Today.Year;
        Month = DateTime.Today.Month;
        SelectedDate = DateTime.Today;
        UpdateMonthDisplay();
        UpdateSelectedAlmanac();
        _ = RefreshDataAsync();
    }

    /// <summary>上一月（不改变 SelectedDate）。到达支持范围下界后忽略本次导航（不抛异常、不改状态）。</summary>
    public void NavigatePreviousMonth()
    {
        int year = Year;
        int month = Month - 1;
        if (month < 1)
        {
            month = 12;
            year--;
        }

        if (year < LunarCalendarHelper.MinSupportedYear)
        {
            return; // 到支持范围下界：忽略本次导航（不抛异常、不改状态）
        }

        Year = year;
        Month = month;
        UpdateMonthDisplay();
        _ = RefreshDataAsync();
    }

    /// <summary>下一月（不改变 SelectedDate）。到达支持范围上界后忽略本次导航。</summary>
    public void NavigateNextMonth()
    {
        int year = Year;
        int month = Month + 1;
        if (month > 12)
        {
            month = 1;
            year++;
        }

        if (year > LunarCalendarHelper.MaxSupportedYear)
        {
            return; // 到支持范围上界：忽略本次导航
        }

        Year = year;
        Month = month;
        UpdateMonthDisplay();
        _ = RefreshDataAsync();
    }

    /// <summary>
    /// 跳转到指定年月（保留当前「日」跨月跳转，与逐月导航行为一致；目标月无该日则取月末）。
    /// 年份按 <see cref="LunarCalendarHelper.MinSupportedYear"/>~<see cref="LunarCalendarHelper.MaxSupportedYear"/> 钳制。
    /// </summary>
    /// <param name="year">目标年（越界将被钳制到支持范围）。</param>
    /// <param name="month">目标月（1~12，越界将被钳制）。</param>
    public void NavigateToMonth(int year, int month)
    {
        int targetYear = Math.Clamp(year, LunarCalendarHelper.MinSupportedYear, LunarCalendarHelper.MaxSupportedYear);
        int targetMonth = Math.Clamp(month, 1, 12);
        int day = Math.Min(SelectedDate.Day, DateTime.DaysInMonth(targetYear, targetMonth));

        Year = targetYear;
        Month = targetMonth;
        SelectedDate = new DateTime(targetYear, targetMonth, day);

        UpdateMonthDisplay();
        UpdateSelectedAlmanac();
        _ = RefreshDataAsync();
    }

    /// <summary>选中某个日期，并刷新近期日程。</summary>
    public void SelectDate(DateTime date)
    {
        SelectedDate = date;
        UpdateSelectedDayIndex();
        UpdateSelectedAlmanac();
        UpdateMonthDisplay();
        _ = UpdateUpcomingEventsAsync();
    }

    /// <summary>折叠 / 展开日程区，并持久化展开态（P0-7 验收 4）。</summary>
    public void ToggleSchedule()
    {
        ScheduleExpanded = !ScheduleExpanded;
        _settings.ScheduleExpanded = ScheduleExpanded;
        _settings.Save();
    }

    /// <summary>跳转到当月第一个法定假日（P1-1 速览条点击行为）。</summary>
    public void NavigateToFirstHoliday()
    {
        DateTime? first = _currentMarkers
            .Where(kv => kv.Key.Year == Year && kv.Key.Month == Month && kv.Value.Kind == DayKind.Holiday)
            .OrderBy(kv => kv.Key)
            .Select(kv => (DateTime?)kv.Key)
            .FirstOrDefault();

        if (first is null) return;

        SelectDate(first.Value);
    }

    /// <summary>关闭诚实横幅（用户点击 ✕ 后不再显示）。</summary>
    public void DismissBanner()
    {
        _bannerDismissed = true;
        OnPropertyChanged(nameof(HasBanner));
    }

    /// <summary>刷新 Header 时间文本（由面板 1s 定时器驱动）。</summary>
    public void RefreshHeaderTime()
    {
        HeaderTimeText = FormatHeaderTime(DateTime.Now);
    }

    // ==================================================================
    // 数据刷新
    // ==================================================================

    /// <summary>
    /// 强制刷新（忽略缓存，重新拉取所有源），随后重建界面数据。
    /// </summary>
    public async Task ForceRefreshAsync()
    {
        await _dateInfoService.ForceRefreshAsync();
        await RefreshDataAsync();
    }

    /// <summary>
    /// 刷新所有数据：网格立即渲染，休班标记 / 节气 / 日程异步加载后再重建网格。
    /// </summary>
    public async Task RefreshDataAsync()
    {
        // 第一阶段：立即生成网格（纯日期计算，不依赖任何数据源）
        GenerateCalendarGrid(new List<CalendarEvent>(), null, null);

        IsLoading = true;
        try
        {
            var firstOfMonth = new DateTime(Year, Month, 1);
            var gridStart = firstOfMonth.AddDays(-GetGridOffset(firstOfMonth));
            var gridEnd = gridStart.AddDays(42);

            var markers = new Dictionary<DateTime, DayMarker>();
            var solarTerms = new Dictionary<DateTime, string>();
            var events = new List<CalendarEvent>();
            var almanac = new Dictionary<DateTime, AlmanacInfo>();

            try { markers = await _dateInfoService.GetDayMarkersAsync(gridStart, gridEnd); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WinCal: 获取休班标记失败: {ex.Message}"); }

            try { solarTerms = await _dateInfoService.GetSolarTermsAsync(gridStart, gridEnd); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WinCal: 获取节气失败: {ex.Message}"); }

            try { events = await _dateInfoService.GetScheduleEventsAsync(gridStart, gridEnd); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WinCal: 获取日程失败: {ex.Message}"); }

            try { almanac = await _dateInfoService.GetAlmanacAsync(gridStart, gridEnd); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"WinCal: 获取黄历失败: {ex.Message}"); }

            _currentMarkers = markers;
            _currentAlmanac = almanac;

            // 第二阶段：用真实数据重建网格（休 / 班 / 节气 / 农历节日 / 黄历）
            GenerateCalendarGrid(events, markers, solarTerms, almanac);

            HolidaySummaryText = BuildHolidaySummary(markers) ?? string.Empty;

            UpdateSelectedAlmanac();

            await UpdateUpcomingEventsAsync();
            await UpdateBannerAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>从选中日期起更新近期日程列表。</summary>
    private async Task UpdateUpcomingEventsAsync()
    {
        UpcomingEvents.Clear();

        if (!EnableSchedule)
        {
            OnPropertyChanged(nameof(ScheduleToggleText));
            return;
        }

        List<CalendarEvent> events;
        try
        {
            events = await _dateInfoService.GetScheduleEventsAsync(
                SelectedDate.Date, SelectedDate.Date.AddDays(UpcomingDays));
        }
        catch
        {
            events = new List<CalendarEvent>();
        }

        foreach (var evt in events.OrderBy(e => e.StartTime).Take(100))
        {
            UpcomingEvents.Add(evt);
        }

        OnPropertyChanged(nameof(ScheduleToggleText));
    }

    /// <summary>刷新诚实横幅（P0-8）。</summary>
    private async Task UpdateBannerAsync()
    {
        if (_bannerDismissed) return;

        try
        {
            var status = await _dateInfoService.GetDataSourceStatusAsync();
            DataSourceLabel = status.DataSourceLabel;
            BannerText = string.IsNullOrWhiteSpace(status.BannerText) ? null : status.BannerText;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"WinCal: 获取数据源状态失败: {ex.Message}");
        }
    }

    // ==================================================================
    // 网格生成
    // ==================================================================

    /// <summary>计算当月第一天在网格中的偏移（考虑周起始日）。</summary>
    private int GetGridOffset(DateTime firstOfMonth)
    {
        int firstDayOfWeek = (int)firstOfMonth.DayOfWeek;
        return (firstDayOfWeek - WeekStartDay + 7) % 7;
    }

    /// <summary>
    /// 生成 6×7 日历网格。
    /// </summary>
    /// <param name="events">日程事件（日程层）。</param>
    /// <param name="markers">休 / 班标记（休班标记层）。</param>
    /// <param name="solarTerms">节气（节气层）。</param>
    /// <param name="almanac">黄历数据（黄历层，可选）。</param>
    private void GenerateCalendarGrid(
        List<CalendarEvent> events,
        Dictionary<DateTime, DayMarker>? markers,
        Dictionary<DateTime, string>? solarTerms,
        Dictionary<DateTime, AlmanacInfo>? almanac = null)
    {
        CalendarDays.Clear();

        var firstOfMonth = new DateTime(Year, Month, 1);
        var gridStart = firstOfMonth.AddDays(-GetGridOffset(firstOfMonth));

        for (int i = 0; i < 42; i++)
        {
            var date = gridStart.AddDays(i);
            bool isCurrentMonth = date.Year == Year && date.Month == Month;
            bool isToday = date.Date == DateTime.Today;

            // 日程层：找出落在此格的事件（全天事件按覆盖区间判定）
            var dayEvents = events.Where(e =>
                e.IsAllDay
                    ? date.Date >= e.StartTime.Date &&
                      date.Date < (e.EndTime.Date > e.StartTime.Date ? e.EndTime.Date : e.StartTime.Date.AddDays(1))
                    : date.Date == e.StartTime.Date
            ).ToList();

            // 农历层（本地推算）
            string lunarText = LunarCalendarHelper.GetLunarDateText(date);

            // 休班标记层
            DayMarker? marker = null;
            if (markers is not null && markers.TryGetValue(date.Date, out var m))
            {
                marker = m;
            }

            // 节气层
            string? solarTerm = null;
            if (solarTerms is not null && solarTerms.TryGetValue(date.Date, out var st))
            {
                solarTerm = st;
            }

            // 农历节日（本地推算与节日集合比对）
            string? festival = LunarFestivalNames.Contains(lunarText) ? lunarText : null;

            CalendarDays.Add(new CalendarDay(
                Date: date,
                IsCurrentMonth: isCurrentMonth,
                IsToday: isToday,
                HasEvents: dayEvents.Count > 0,
                LunarDate: lunarText,
                Events: dayEvents)
            {
                MarkKind = marker?.Kind ?? DayKind.None,
                HolidayName = marker?.Name,
                SolarTerm = solarTerm,
                LunarFestival = festival,
                Almanac = almanac is not null && almanac.TryGetValue(date.Date, out var alm) ? alm : null
            });
        }

        UpdateSelectedDayIndex();
    }

    /// <summary>根据 SelectedDate 在当前网格中的位置更新索引（不在网格中则为 -1）。</summary>
    private void UpdateSelectedDayIndex()
    {
        SelectedDayIndex = -1;
        for (int i = 0; i < CalendarDays.Count; i++)
        {
            if (CalendarDays[i].Date.Date == SelectedDate.Date)
            {
                SelectedDayIndex = i;
                break;
            }
        }
    }

    /// <summary>
    /// 依据 <see cref="SelectedDate"/> 从当前网格黄历缓存中取出数据，刷新底部黄历详情面板。
    /// <b>纯内存查找、零 IO</b>，因此点击任意日期可即时刷新（"点哪天显示哪天"）。
    /// </summary>
    private void UpdateSelectedAlmanac()
    {
        _currentAlmanac.TryGetValue(SelectedDate.Date, out var almanac);
        SelectedDayAlmanac = almanac;

        string lunar = LunarCalendarHelper.GetLunarDateText(SelectedDate);

        var title = new List<string>();
        if (!string.IsNullOrWhiteSpace(lunar))
        {
            title.Add("农历" + lunar);
        }

        string? ganzhi = DecorateGanZhiWithZodiac(almanac?.GanZhi, SelectedDate);
        if (!string.IsNullOrWhiteSpace(ganzhi))
        {
            title.Add(ganzhi!);
        }

        SelectedAlmanacTitle = title.Count > 0
            ? string.Join(" · ", title)
            : SelectedDate.ToString("yyyy年M月d日", CultureInfo.InvariantCulture);

        var meta = new List<string>();
        if (!string.IsNullOrWhiteSpace(almanac?.ChongSha)) meta.Add($"冲煞：{almanac!.ChongSha}");
        if (!string.IsNullOrWhiteSpace(almanac?.JianChu)) meta.Add($"建除：{almanac!.JianChu}");
        if (!string.IsNullOrWhiteSpace(almanac?.Xiu)) meta.Add($"宿：{almanac!.Xiu}");
        if (!string.IsNullOrWhiteSpace(almanac?.MoonPhase)) meta.Add($"月相：{almanac!.MoonPhase}");
        SelectedAlmanacMeta = string.Join(" · ", meta);
    }

    /// <summary>
    /// 在干支年上插入生肖标注，把「乙巳年 戊子月 乙亥日」变成「乙巳[蛇]年 戊子月 乙亥日」。
    /// 解析失败时原样返回（绝不抛异常、绝不丢信息）。
    /// </summary>
    private static string? DecorateGanZhiWithZodiac(string? ganZhi, DateTime date)
    {
        if (string.IsNullOrWhiteSpace(ganZhi))
        {
            return ganZhi;
        }

        int yearIndex = ganZhi.IndexOf('年');
        if (yearIndex <= 0)
        {
            return ganZhi;
        }

        try
        {
            int lunarYear = Lunisolar.GetYear(date);
            int index = ((lunarYear - 4) % 12 + 12) % 12;
            return ganZhi[..yearIndex] + "[" + ZodiacNames[index] + "]" + ganZhi[yearIndex..];
        }
        catch
        {
            return ganZhi;
        }
    }

    // ==================================================================
    // 速览条文案
    // ==================================================================

    /// <summary>
    /// 由休班标记推导「当月放假速览」文案（P1-1）。无放假安排时返回 null（该条隐藏、不占高度）。
    /// </summary>
    private string? BuildHolidaySummary(Dictionary<DateTime, DayMarker> markers)
    {
        var monthStart = new DateTime(Year, Month, 1);
        var monthEnd = monthStart.AddMonths(1);

        var monthMarkers = markers
            .Where(kv => kv.Key >= monthStart && kv.Key < monthEnd)
            .OrderBy(kv => kv.Key)
            .ToList();

        if (monthMarkers.Count == 0) return null;

        var parts = new List<string>();

        // 休：连续同名前缀的日期段合并为「国庆节 10/1–10/8 休 8 天」
        var holidayDays = monthMarkers.Where(kv => kv.Value.Kind == DayKind.Holiday).ToList();
        foreach (var seg in MergeSegments(holidayDays))
        {
            var name = GetNamePrefix(seg.Name);
            int days = (seg.End - seg.Start).Days + 1;
            var prefix = name.Length > 0 ? name + " " : string.Empty;
            parts.Add($"{prefix}{seg.Start:M/d}\u2013{seg.End:M/d} 休{days}天");
        }

        // 班：逐个列出「10/10(六) 上班」
        foreach (var kv in monthMarkers.Where(kv => kv.Value.Kind == DayKind.Workday))
        {
            parts.Add($"{kv.Key:M/d}({WeekChar(kv.Key.DayOfWeek)}) 上班");
        }

        if (parts.Count == 0) return null;

        return "\U0001F4C5 本月：" + string.Join(" · ", parts);
    }

    /// <summary>把连续的休假日合并为日期段（同名前缀且日期相邻）。</summary>
    private static IEnumerable<Segment> MergeSegments(List<KeyValuePair<DateTime, DayMarker>> days)
    {
        if (days.Count == 0) yield break;

        var start = days[0].Key;
        var end = days[0].Key;
        var name = days[0].Value.Name;

        for (int i = 1; i < days.Count; i++)
        {
            var kv = days[i];
            if (kv.Key == end.AddDays(1) && GetNamePrefix(kv.Value.Name) == GetNamePrefix(name))
            {
                end = kv.Key;
                continue;
            }

            yield return new Segment(start, end, name);
            start = kv.Key;
            end = kv.Key;
            name = kv.Value.Name;
        }

        yield return new Segment(start, end, name);
    }

    /// <summary>取名称前缀（首个全/半角左括号之前），用于同日 / 连续段去重。</summary>
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

    /// <summary>星期中文单字。</summary>
    private static string WeekChar(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        _ => "日"
    };

    /// <summary>连续休假日段。</summary>
    private sealed record Segment(DateTime Start, DateTime End, string Name);

    // ==================================================================
    // Header / 其它
    // ==================================================================

    private void UpdateMonthDisplay()
    {
        MonthDisplay = $"{Year}年{Month}月";

        // 跟随 SelectedDate（而非 DateTime.Today）：跳转到历史 / 未来年月时 Header 农历同步变化，
        // 否则跳到 1989 年 12 月却显示今天的农历，信息割裂。
        // 调用点：NavigateToToday / NavigateToMonth / NavigatePreviousMonth / NavigateNextMonth
        //         / SelectDate。逐月翻页不改变 SelectedDate，故其 Header 农历与改动前一致。
        // 空值保护：农历推算区间外（1901-01-01 ~ 1901-02-18，早于
        // ChineseLunisolarCalendar.MinSupportedDateTime = 1901-02-19）返回空串时，
        // 整体清空而不是留下孤立的「农历」前缀。
        string lunar = GetLunarMonthDay(SelectedDate);
        YearSecondaryText = string.IsNullOrEmpty(lunar) ? string.Empty : "农历" + lunar;
    }

    /// <summary>格式化 Header 时间文本（受 ShowHeaderDateTime / TimeFormat 控制）。</summary>
    private string FormatHeaderTime(DateTime now)
    {
        if (!_settings.ShowHeaderDateTime) return string.Empty;

        return _settings.TimeFormat switch
        {
            TimeFormatMode.Hour12 => now.ToString("hh:mm tt", CultureInfo.InvariantCulture),
            TimeFormatMode.Hour24 => now.ToString("HH:mm", CultureInfo.InvariantCulture),
            _ => now.ToString("HH:mm", CultureInfo.InvariantCulture)
        };
    }

    /// <summary>以本地农历推算生成「八月初五」样式文本（闰月加「闰」）。</summary>
    private static string GetLunarMonthDay(DateTime date)
    {
        try
        {
            if (date < Lunisolar.MinSupportedDateTime || date > Lunisolar.MaxSupportedDateTime)
            {
                return string.Empty;
            }

            int month = Lunisolar.GetMonth(date);
            int day = Lunisolar.GetDayOfMonth(date);
            int leapMonth = Lunisolar.GetLeapMonth(Lunisolar.GetYear(date));
            bool isLeap = leapMonth > 0 && month == leapMonth;
            int normalMonth = leapMonth > 0 && month >= leapMonth ? month - 1 : month;

            string monthText = (isLeap ? "闰" : string.Empty)
                + LunarMonthNames[Math.Clamp(normalMonth, 1, 12)];
            string dayText = day >= 1 && day < LunarDayNames.Length
                ? LunarDayNames[day]
                : day.ToString(CultureInfo.InvariantCulture);

            return monthText + dayText;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>暴露数据源诚实状态（供设置页 / 诊断使用）。</summary>
    public Task<DataSourceStatus> GetDataSourceStatusAsync() => _dateInfoService.GetDataSourceStatusAsync();

    /// <summary>打开日程层对应的系统应用。</summary>
    public void OpenScheduleApp() => _dateInfoService.OpenScheduleApp();

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
