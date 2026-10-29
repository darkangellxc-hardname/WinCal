namespace WinCal.Core.Models;

/// <summary>
/// 休 / 班种类。
/// </summary>
public enum DayKind
{
    /// <summary>无标记（既不是法定休假日，也不是调休补班日）。</summary>
    None = 0,

    /// <summary>休（法定节假日 / 放假）。</summary>
    Holiday = 1,

    /// <summary>班（调休补班 / 上班）。</summary>
    Workday = 2
}

/// <summary>
/// 休班标记模型（休班标记层的输出单位，与日程层 <see cref="CalendarEvent"/> 彻底解耦，契约 C10）。
/// </summary>
public sealed class DayMarker
{
    /// <summary>该标记对应的日期（仅日期部分有意义）。</summary>
    public DateTime Date { get; set; }

    /// <summary>休 / 班种类。</summary>
    public DayKind Kind { get; set; } = DayKind.None;

    /// <summary>节日 / 假期名称（如「元旦」「春节」），已去除尾部 (休)/(班) 后缀。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>默认构造。</summary>
    public DayMarker()
    {
    }

    /// <summary>便捷构造。</summary>
    public DayMarker(DateTime date, DayKind kind, string name)
    {
        Date = date.Date;
        Kind = kind;
        Name = name ?? string.Empty;
    }

    /// <summary>便于日志输出。</summary>
    public override string ToString() => $"{Date:yyyy-MM-dd} {Kind} {Name}";
}

/// <summary>
/// 休班标记的解析中间态（直接来自 ICS 原始对象，支持同一天多条 —— 契约 C4）。
///
/// <para>之所以与 <see cref="DayMarker"/> 分开：一天可能命中多个 ICS 事件
/// （实测 2025 国庆被拆为 <c>1001→1006</c> 与 <c>1007→1009</c> 两段），
/// 解析阶段必须保留全部原始条目，再由上层做去重 / 归并。</para>
/// </summary>
public sealed class HolidayMarkerRaw
{
    /// <summary>覆盖到的某一天（已按 DTEND 排他展开）。</summary>
    public DateTime Date { get; set; }

    /// <summary>休 / 班种类。</summary>
    public DayKind Kind { get; set; } = DayKind.None;

    /// <summary>节日 / 假期名称（已去除尾部 (休)/(班) 后缀）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>来源订阅名称（别名），便于多源追溯。</summary>
    public string SourceName { get; set; } = string.Empty;

    /// <summary>判定证据（命中的第几级 fallback 及其取值），便于排查「为什么标成休/班」。</summary>
    public string Evidence { get; set; } = string.Empty;

    /// <summary>默认构造。</summary>
    public HolidayMarkerRaw()
    {
    }

    /// <summary>便捷构造。</summary>
    public HolidayMarkerRaw(DateTime date, DayKind kind, string name, string sourceName, string evidence)
    {
        Date = date.Date;
        Kind = kind;
        Name = name ?? string.Empty;
        SourceName = sourceName ?? string.Empty;
        Evidence = evidence ?? string.Empty;
    }

    /// <summary>便于日志输出。</summary>
    public override string ToString() => $"{Date:yyyy-MM-dd} {Kind} {Name} [{SourceName}] {Evidence}";
}

/// <summary>
/// 节气数据模型（节气层输出单位）。来源：ICS <c>SUMMARY</c> 中的 <c>【】</c> 提取（契约 C5）。
/// </summary>
public sealed class SolarTerm
{
    /// <summary>节气所在日期。</summary>
    public DateTime Date { get; set; }

    /// <summary>节气名称（如「小寒」「立春」）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>默认构造。</summary>
    public SolarTerm()
    {
    }

    /// <summary>便捷构造。</summary>
    public SolarTerm(DateTime date, string name)
    {
        Date = date.Date;
        Name = name ?? string.Empty;
    }

    /// <summary>便于日志输出。</summary>
    public override string ToString() => $"{Date:yyyy-MM-dd} {Name}";
}
