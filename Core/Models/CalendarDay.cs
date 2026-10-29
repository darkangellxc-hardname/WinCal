namespace WinCal.Core.Models;

/// <summary>
/// 单个日期格子的渲染聚合数据（6×7 月历网格的单元）。
///
/// <para><b>注意</b>：这是<b>渲染聚合</b>而非数据模型（U4 决策），
/// 因此扩展它承载「休/班标记 / 节气 / 农历节日 / 黄历」不违反
/// 「休班标记层与日程层解耦」的契约（C10）—— <see cref="Events"/> 仍只由日程层填充。</para>
/// </summary>
public record CalendarDay(
    DateTime Date,
    bool IsCurrentMonth,
    bool IsToday,
    bool HasEvents,
    string LunarDate,
    List<CalendarEvent> Events
)
{
    /// <summary>休 / 班标记（渲染整格底色与胶囊）。</summary>
    public DayKind MarkKind { get; init; } = DayKind.None;

    /// <summary>休班标记名称（如「元旦」「春节」），来自休班标记层。</summary>
    public string? HolidayName { get; init; }

    /// <summary>节气名称（如「小寒」「立春」），来自节气层。</summary>
    public string? SolarTerm { get; init; }

    /// <summary>农历节日名（如「春节」「中秋节」），来自本地推算（<c>LunarCalendarHelper</c>）。</summary>
    public string? LunarFestival { get; init; }

    /// <summary>黄历数据（已在底部黄历详情面板呈现）。</summary>
    public AlmanacInfo? Almanac { get; init; }
}
