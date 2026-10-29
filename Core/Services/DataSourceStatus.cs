namespace WinCal.Core.Services;

/// <summary>
/// 数据源诚实状态模型（P0-8）。用于向 UI 如实呈现「哪个源不可用、为什么」，
/// <b>绝不允许静默降级</b>（例如静默改用 Mock 假数据）。
/// </summary>
public sealed class DataSourceStatus
{
    /// <summary>系统日历是否不可用（本机因缺 Win10 SDK，恒为 true）。</summary>
    public bool IsSystemCalendarUnavailable { get; set; }

    /// <summary>面板顶部横幅文案；为空表示无横幅。</summary>
    public string? BannerText { get; set; }

    /// <summary>数据源标签（如「ICS 订阅（2 个源）」）。</summary>
    public string DataSourceLabel { get; set; } = string.Empty;

    /// <summary>各订阅源的逐条可用性（源名 → 是否可用）。</summary>
    public List<DataSourceItem> Items { get; set; } = new();

    /// <summary>是否存在任何不可用项（决定是否显示横幅）。</summary>
    public bool HasProblem => IsSystemCalendarUnavailable || Items.Any(i => !i.IsAvailable);

    /// <summary>默认构造。</summary>
    public DataSourceStatus()
    {
    }

    /// <summary>便于日志输出。</summary>
    public override string ToString() =>
        $"SystemUnavailable={IsSystemCalendarUnavailable} Label={DataSourceLabel} Banner={BannerText} Items={Items.Count}";
}

/// <summary>
/// 单个数据源的可用性条目。
/// </summary>
public sealed class DataSourceItem
{
    /// <summary>源名称（别名）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>用途。</summary>
    public IcsPurpose Purpose { get; set; }

    /// <summary>是否可用。</summary>
    public bool IsAvailable { get; set; }

    /// <summary>不可用原因（可用时为空）。</summary>
    public string? Reason { get; set; }

    /// <summary>默认构造。</summary>
    public DataSourceItem()
    {
    }

    /// <summary>便捷构造。</summary>
    public DataSourceItem(string name, IcsPurpose purpose, bool isAvailable, string? reason = null)
    {
        Name = name ?? string.Empty;
        Purpose = purpose;
        IsAvailable = isAvailable;
        Reason = reason;
    }

    /// <summary>便于日志输出。</summary>
    public override string ToString() => $"{Name} ({Purpose}) available={IsAvailable} {(Reason is null ? "" : "reason=" + Reason)}";
}
