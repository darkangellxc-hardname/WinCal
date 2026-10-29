namespace WinCal.Core.Services;

/// <summary>
/// ICS 订阅的用途（Q12 / 契约 C12）。决定该订阅的数据进入哪一条数据流。
/// </summary>
public enum IcsPurpose
{
    /// <summary>节假日 / 休班标记（休班标记层）。</summary>
    Holiday = 0,

    /// <summary>农历 / 节气 / 黄历（日期辅助层）。</summary>
    Almanac = 1,

    /// <summary>日程（日程层，默认关停）。</summary>
    Schedule = 2
}

/// <summary>
/// 单条 ICS 订阅配置项（Q12：两级配置 —— 订阅本身 + 用途）。
/// </summary>
public sealed class IcsSubscription
{
    /// <summary>订阅 URL。</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>别名（展示用；为空时按 URL 域名推断）。</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>用途：决定该源进入哪条数据流（默认节假日）。</summary>
    public IcsPurpose Purpose { get; set; } = IcsPurpose.Holiday;

    /// <summary>是否启用。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>默认构造。</summary>
    public IcsSubscription()
    {
    }

    /// <summary>便捷构造。</summary>
    public IcsSubscription(string url, string alias, IcsPurpose purpose = IcsPurpose.Holiday, bool enabled = true)
    {
        Url = url ?? string.Empty;
        Alias = alias ?? string.Empty;
        Purpose = purpose;
        Enabled = enabled;
    }

    /// <summary>便于日志输出。</summary>
    public override string ToString() => $"{Alias} ({Purpose}) {Url} enabled={Enabled}";
}
