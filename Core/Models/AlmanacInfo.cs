namespace WinCal.Core.Models;

/// <summary>
/// 黄历数据模型（契约 C11；已在面板底部黄历详情面板中呈现）。
///
/// <para>数据全部已包含在黄历 ICS 源的 <c>DESCRIPTION</c> 中，解析成本近乎为零，
/// 因此黄历层与休班标记层 / 节气层一样，直接从订阅源逐日提取，无需额外网络请求。</para>
/// </summary>
public sealed class AlmanacInfo
{
    /// <summary>干支，如「乙巳年 己丑月 己卯日」。来源：<c>🔄 干支：</c></summary>
    public string? GanZhi { get; set; }

    /// <summary>宜，如「祭祀 沐浴 剃头」。来源：<c>✅ 宜：</c></summary>
    public string? Yi { get; set; }

    /// <summary>忌，如「出行 结婚姻」。来源：<c>❌ 忌：</c></summary>
    public string? Ji { get; set; }

    /// <summary>冲煞，如「兔日冲鸡」。来源：<c>⚔️ 冲煞：</c></summary>
    public string? ChongSha { get; set; }

    /// <summary>建除十二神，如「满」。来源：<c>📋 建除：</c></summary>
    public string? JianChu { get; set; }

    /// <summary>二十八宿，如「张月鹿」。来源：<c>⭐ 二十八宿：</c></summary>
    public string? Xiu { get; set; }

    /// <summary>月相，如「望」「下弦」（可能为空）。来源：<c>🌓 月相：</c></summary>
    public string? MoonPhase { get; set; }

    /// <summary>吉神，如「岁德合、四相、王日」。来源：<c>🙏 吉神：</c></summary>
    public string? JiShen { get; set; }

    /// <summary>凶煞，如「岁破、大祸、朱雀」。来源：<c>👹 凶煞：</c></summary>
    public string? XiongSha { get; set; }

    /// <summary>该黄历信息对应日期。</summary>
    public DateTime Date { get; set; }

    /// <summary>是否有任一字段被填充（避免渲染空对象）。</summary>
    public bool HasAny =>
        !string.IsNullOrWhiteSpace(GanZhi) ||
        !string.IsNullOrWhiteSpace(Yi) ||
        !string.IsNullOrWhiteSpace(Ji) ||
        !string.IsNullOrWhiteSpace(ChongSha) ||
        !string.IsNullOrWhiteSpace(JianChu) ||
        !string.IsNullOrWhiteSpace(Xiu) ||
        !string.IsNullOrWhiteSpace(MoonPhase) ||
        !string.IsNullOrWhiteSpace(JiShen) ||
        !string.IsNullOrWhiteSpace(XiongSha);

    /// <summary>便于日志输出。</summary>
    public override string ToString() =>
        $"{Date:yyyy-MM-dd} 干支={GanZhi} 宜={Yi} 忌={Ji} 冲煞={ChongSha} 建除={JianChu} 宿={Xiu} 月相={MoonPhase} 吉神={JiShen} 凶煞={XiongSha}";
}
