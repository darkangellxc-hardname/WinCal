# ICS 订阅源字段契约（实测）

> 作者：交付总监 齐活林（实测抓取，2026-09-16）
> 性质：**硬事实基线**。以下全部为真实下载 + 逐条解析所得，非推测。
> 用途：架构设计（Task #2）与实现（Task #3）的**唯一字段依据**。
> 原始文件：`%TEMP%\wincal_probes\{holidays,almanac}.ics`（探针目录，不入库）

---

## 1. 用户实际订阅的两个源

用户原话：

> 「你看原项目是填写订阅地址的，比如我订阅节假日、调休补班用的是
> `https://cdn.jsdelivr.net/npm/chinese-days/dist/holidays.ics`，
> 黄历用的是 `https://qilpmgl.github.io/MyCalendar/chinese_almanac.ics`」

| 简称 | URL | 角色 | 实测大小 | 事件数 |
|---|---|---|---|---|
| **holidays** | `https://cdn.jsdelivr.net/npm/chinese-days/dist/holidays.ics` | **休/班 + 节假日** | 23,776 B | **41** |
| **almanac** | `https://qilpmgl.github.io/MyCalendar/chinese_almanac.ics` | **农历 + 节气 + 黄历** | 1,349,511 B | **1096** |

---

## 2. 源 A：holidays.ics（休 / 班判定契约）★ 核心

### 2.1 字段清单（41/41 条全有）

`UID` `SEQUENCE` `DTSTAMP` `DTSTART` `DTEND` `X-MICROSOFT-CDO-ALLDAYEVENT`
`X-MICROSOFT-MSNCALENDAR-ALLDAYEVENT` `SUMMARY` `TRANSP` `LOCATION`
`DESCRIPTION` `CATEGORIES` `STATUS` `X-APPLE-SPECIAL-DAY`
`X-APPLE-UNIVERSAL-ID` `CLASS`

### 2.2 ★ 休 / 班判定契约（三重冗余，任取其一即可）

这是 P0-5 的实现前提，**必须按此实现，禁止关键字猜**。

| 字段 | 「休」的取值 | 「班」的取值 | 可靠性 |
|---|---|---|---|
| **`DESCRIPTION`** | 字面值 `休` | 字面值 `班` | ★★★ **首选**（唯一二值，零歧义） |
| `X-APPLE-SPECIAL-DAY` | `WORK-HOLIDAY` | `ALTERNATE-WORKDAY` | ★★★ 首选备选（标准语义） |
| `X-APPLE-SPECIAL-DAY` 副用 | `WORK-HOLIDAY` 字面含 `HOLIDAY` | `ALTERNATE-WORKDAY` | — |
| `SUMMARY` 尾部括号 | `元旦(休)` / `春节(休)` | `元旦(班)` / `春节(班)` | ★★ 兜底（正则 `\(([休班])\)$`） |
| `CATEGORIES` | 全部为 `节假日` | 全部为 `节假日` | ✗ **不可用于区分** |

**实现要求**：三级 fallback —— `DESCRIPTION` → `X-APPLE-SPECIAL-DAY` → `SUMMARY` 括号后缀。
三级都判不出来 → **不打标记**（遵守 PRD「宁缺勿错」原则），不得用关键字瞎猜。

### 2.3 实测统计

```
DESCRIPTION : {'休': 22, '班': 19}
CATEGORIES  : {'节假日': 41}          ← 不区分，勿用
X-APPLE-SPECIAL-DAY : {'WORK-HOLIDAY': 22, 'ALTERNATE-WORKDAY': 19}
SUMMARY 后缀 : {'休': 22, '班': 19}
DTSTART 年份 : {'2024': 15, '2025': 13, '2026': 13}
```

其他固定值：`LOCATION=北京`、`TRANSP=TRANSPARENT`、`STATUS=CONFIRMED`、`CLASS=PUBLIC`、
`X-MICROSOFT-CDO-ALLDAYEVENT=TRUE`、`X-MICROSOFT-MSNCALENDAR-ALLDAYEVENT=TRUE`。

### 2.4 ⚠️ 两条必须遵守的时间语义

**(a) `DTEND` 是排他的（同 iCal 标准）**

```
DTSTART:20240101  DTEND:20240102  SUMMARY:元旦(休)   → 只表示 2024-01-01 一天
DTSTART:20260215  DTEND:20260224  SUMMARY:春节(休)   → 表示 2026-02-15 ~ 02-23 共 9 天
```
**渲染时必须 `DTEND - 1 天`**，否则每个节假日尾部会多标一天。现有 ICS 服务已有「全天事件三层防护」，
需核对是否已正确处理排他性。

**(b) 同一节日会被拆成多段 VEVENT**

实测样例：2025 年国庆因中秋夹在中间，被拆为两条 ——
```
20251001 -> 20251006  国庆节(休)
20251007 -> 20251009  国庆节(休)
```
**实现要求**：同日可能命中**多个**休/班事件，数据结构必须支持 `List`，不得用单值覆盖。
渲染到日期格子时，标题去重（现有 ICS 服务已有「同日同标题前缀去重」，可复用）。

### 2.5 覆盖年限

仅 **2024 / 2025 / 2026** 三年。**2027 起无数据** → 面板需能优雅处理「当月无休班数据」的情况
（不得报错、不得显示错误标记）。

### 2.6 真实数据样例（原文）

```
BEGIN:VEVENT
UID:87d63b1c-d00e-41a2-b9bc-7751990a4f5f
SEQUENCE:0
DTSTAMP:20260602T064013
DTSTART:20260101
DTEND:20260104
X-MICROSOFT-CDO-ALLDAYEVENT:TRUE
X-MICROSOFT-MSNCALENDAR-ALLDAYEVENT:TRUE
SUMMARY:元旦(休)
TRANSP:TRANSPARENT
LOCATION:北京
DESCRIPTION:休
CATEGORIES:节假日
STATUS:CONFIRMED
X-APPLE-SPECIAL-DAY:WORK-HOLIDAY
X-APPLE-UNIVERSAL-ID:c847e7d3b165ba31a85331b277b4b32fa3d6e4477f511f906ba15c93bf61d225
CLASS:PUBLIC
END:VEVENT
```

---

## 3. 源 B：almanac.ics（农历 + 节气 + 黄历）

### 3.1 字段清单（1086/1096 条）

`SUMMARY` `DTSTART` `DTEND` `DTSTAMP` `UID` `DESCRIPTION` `LAST-MODIFIED` `TRANSP`

**注意：没有 `CATEGORIES`，没有 `X-APPLE-*`，没有 `LOCATION`。** 与源 A 字段集完全不同，
**解析器绝不能假设两个源有相同字段**。

### 3.2 结构：一天一条 VEVENT

```json
{
  "DTSTART": "20260105",
  "DTEND":   "20260106",              // 排他，= 1 天
  "SUMMARY": "冬月十七 己卯 【小寒】 宜:恤孤茕 覃恩 雪冤",
  "DESCRIPTION": "📅 公历：2026年01月05日\n🌙 农历：乙巳年（蛇年）冬月十七\n🔄 干支：乙巳年 己丑月 己卯日\n\n🌿 节气：小寒\n\n✅ 宜：...\n❌ 忌：...\n\n⚔️ 冲煞：兔日冲鸡\n📋 建除：满\n⭐ 二十八宿：张月鹿\n🌓 月相：\n\n🙏 吉神：...\n👹 凶煞：...\n\n⏭️ 下一节气：大寒（1月20日）\n\n📖 数据来源：《钦定协纪辨方书》/ cnlunar\n⚠️ 仅供参考",
  "UID": "bd0ca14acac25efe116eb3da45334dd2@yellow-calender"
}
```

### 3.3 ★ 节气提取契约（P0-5 节气走 ICS 的实现依据）

**实测：两种提取方式都是 72 / 72 命中，100% 覆盖。**

| 方式 | 规则 | 命中数 | 可靠性 |
|---|---|---|---|
| **`SUMMARY` 方括号** | 正则 `【([^】]+)】` | **72** | ★★★ **首选**（最短，无需展开长文本） |
| `DESCRIPTION` 标记行 | 正则 `🌿\s*节气：\s*(\S+)` | **72** | ★★★ 备选（更结构化） |

**校验**：2026 / 2027 / 2028 三年各 **24** 个，共 **72** 个 —— 与「一年 24 节气」完全吻合，
证明 `【】` 中**只出现节气名，不含其他内容**，可以放心用。

实测节气序列（2026 全年，24 个全覆盖）：
```
20260105 小寒   20260120 大寒   20260204 立春   20260218 雨水
20260305 惊蛰   20260320 春分   20260405 清明   20260420 谷雨
20260505 立夏   20260521 小满   20260605 芒种   20260621 夏至
20260707 小暑   20260723 大暑   20260807 立秋   20260823 处暑
20260907 白露   20260923 秋分   20261008 寒露   20261023 霜降
20261107 立冬   20261122 小雪   20261207 大雪   20261222 冬至
```

**判定规则**：`SUMMARY` 命中 `【】` **即是**节气日；无 `【】` 则不是。**不需要**维护 24 节气名字表。

### 3.4 ★ 农历提取契约

- **`SUMMARY` 前缀** = 农历月日，格式 `{月}{日}`，如 `冬月十七`、`腊月初二`、`正月初二`、`二月十八`
- 月名：正月/二月/…/十月/冬月/腊月（含闰月时可能为「闰二月」等，需容错）
- 日名：初一…初十 / 十一…十九 / 二十 / 廿一…廿九 / 三十
- 完整农历（含年干支）在 `DESCRIPTION` 的 `🌙 农历：乙巳年（蛇年）冬月十七`

**⚠️ 与现有实现的取舍**：

现有 `Core/Helpers/LunarCalendarHelper.cs` 用 `ChineseLunisolarCalendar` **本地推算**，能力可靠
且**不依赖订阅源**。用户说节气也走 ICS，但农历**未被要求**走 ICS。

**建议（待架构师确认）**：
- **农历**：**保留本地推算**（`LunarCalendarHelper`）为主 —— 订阅源只覆盖 2026-2028，
  本地推算覆盖全时段，且现有代码已验证可用。ICS 农历作为交叉校验，不覆盖本地结果。
- **节气**：按用户明确要求**走 ICS**（`【】` 提取）。ICS 无数据时（如 2029 年）**不显示节气**，
  不回退到有误差的寿星公式（遵守「宁缺勿错」）。

### 3.5 超预期发现：黄历数据可白拿（P2 可选）

`DESCRIPTION` 中除农历/节气外，还完整提供：

| 数据 | 标记 | 样例 |
|---|---|---|
| 干支 | `🔄 干支：` | `乙巳年 己丑月 己卯日` |
| **宜** | `✅ 宜：` | `祭祀 沐浴 剃头 捕摄` |
| **忌** | `❌ 忌：` | `祭祀 出行 结婚姻…` |
| 冲煞 | `⚔️ 冲煞：` | `兔日冲鸡` |
| 建除十二神 | `📋 建除：` | `满` |
| 二十八宿 | `⭐ 二十八宿：` | `张月鹿` |
| 月相 | `🌓 月相：` | `望` / `下弦`（可能为空） |
| 吉神 / 凶煞 | `🙏 吉神：` / `👹 凶煞：` | 长串 |
| 下一节气预告 | `⏭️ 下一节气：` | `大寒（1月20日）` |

**用户订阅黄历源本身即表达了「想看」的意图**，且数据已在同一个 ICS 里，**解析成本近乎为零**。
建议作为 **P2** 在事件详情浮层里展示「宜/忌 + 冲煞」。**架构师需预留数据结构**，本次可不实现 UI。
（不改 PRD 的 P0 范围，仅做数据层预留。）

### 3.6 ⚠️ 两个解析陷阱

**(a) `DESCRIPTION` 中的换行是字面转义序列 `\n`（反斜杠 + n 两个字符），不是真换行。**

ICS 规范要求文本值中的换行转义为 `\n`。解析时需 `.Replace("\\n", "\n")`；
同理 `\,` `\;` `\\` 亦需反转义。**现有 ICS 解析器需核对是否已做反转义**。

**(b) 折叠行（line folding）必须展开。**
1.3MB 的长 DESCRIPTION 必然被折行（续行以单个空格或 Tab 开头）。
现有服务若未做 unfold，会截断数据。**架构师需确认。**

### 3.7 覆盖年限

**2026 / 2027 / 2028**（365 + 365 + 366 = 1096 天），与源 A 的 2024-2026 **只有 2026 一年重叠**。
→ 农历走本地推算的决策更显必要（源 A 覆盖的 2024/2025 在源 B 里没有）。

---

## 4. 两源协同：数据流契约

```
┌─────────────────────────────────────────────────────────────┐
│ 用户订阅（设置界面可增删，每条带「用途」标记）                  │
│                                                             │
│  源A holidays.ics  ──用途: 节假日/休班──┐                     │
│  源B almanac.ics   ──用途: 农历/节气───┤                     │
│  源C 用户自加 ICS  ──用途: 日程(可选)───┤                     │
└────────────────────────────────────────┼─────────────────────┘
                                         ▼
                   ICS 解析 → 按「用途」分流（PRD Q12 的两级配置）
                                         │
        ┌────────────────────────────────┼────────────────────────────┐
        ▼                                ▼                            ▼
  休班标记层                        日期辅助层                    日程层（默认关）
  DayMarker                         DateInfo                     Event
  · IsHoliday  (休)                 · SolarTerm  节气              · 现有事件列表
  · IsWorkday  (班)                 · LunarText  农历              · 现有详情浮层
  · HolidayName 节日名              · Almanac    (P2 预留)         · 现有去重/缓存
        │                                │                            │
        └────────────────┬───────────────┘                            │
                         ▼                                            ▼
              月历格子渲染（红休/蓝班胶囊）                    收起态/展开态
                         │
                         └──► 当月放假速览条（P1-1）
```

**关键解耦（PRD 核心判断，此处落为契约）**：
- 「休/班标记」由**用途=节假日**的源提供，**与日程开关完全无关** —— 关掉日程，休班标记照常显示
- 「日程层」只消费**用途=日程**的源，默认关停
- 关掉日程后，holidays.ics 的事件**绝不进入**事件列表（避免「端午(休)」污染日程观感）

---

## 5. 给架构师的硬约束清单（不可违反）

| # | 约束 | 依据 |
|---|---|---|
| C1 | 休/班判定按 `DESCRIPTION`(`休`/`班`) → `X-APPLE-SPECIAL-DAY` → `SUMMARY` 括号 三级 fallback | §2.2 实测 |
| C2 | `CATEGORIES` 恒为「节假日」，**禁止**用于区分休班 | §2.3 实测 |
| C3 | `DTEND` 排他，渲染需 `-1 天` | §2.4(a) iCal 标准 + 实测样例 |
| C4 | 同日可命中多条休班事件，数据结构必须支持多值 | §2.4(b) 实测（国庆拆段） |
| C5 | 节气从 `SUMMARY` 的 `【】` 提取，无需 24 节气名表 | §3.3 实测 72/72 |
| C6 | 节气/休班数据缺失（超出订阅年份）时**不显示、不报错、不瞎猜** | PRD「宁缺勿错」 |
| C7 | 农历**保留本地推算**为主，ICS 农历仅作校验 | §3.4 |
| C8 | `DESCRIPTION` 需反转义 `\n`；折叠行需 unfold | §3.6 实测 |
| C9 | 解析器**不得假设两个源字段集相同**（源 B 无 `CATEGORIES`） | §3.1 |
| C10 | 休班标记层与日程层**必须解耦**，日程开关不影响休班标记 | §4 + PRD |
| C11 | 预留黄历（宜/忌/冲煞）数据结构，本次不做 UI | §3.5 |
| C12 | 每条订阅带「用途」标记，日程列表按用途过滤 | PRD Q12 |

---

## 6. 现有代码能力核查结论（已读源码，非推测）

> 核查对象：`Core/Services/IcsCalendarService.cs`、`Core/Models/CalendarEvent.cs`、
> `Core/Services/AppSettings.cs`、`ViewModels/CalendarViewModel.cs`、`Views/Controls/EventItem.xaml.cs`
> 结论性质：**已确认**，架构师可直接采信，不必重复核查。

| # | 问题 | **结论** | 证据 |
|---|---|---|---|
| T1 | `DTEND` 排他性是否已处理？ | ✅ **已正确处理，无需修** | `Ical.Net` 的 `period.EndTime` 本身即排他语义；`CalendarViewModel.cs:407` 用 `date.Date < e.EndTime.Date` 判定覆盖，`EventItem.xaml.cs:44` 显示时 `AddDays(-1)`。实测样例（元旦 `0101→0102` 渲染为 1 天、春节 `0215→0224` 渲染为 9 天）与预期一致 |
| T2 | fold 展开与 `\n` 反转义是否已做？ | ✅ **由 `Ical.Net` 4.3.1 自动处理，无需自己写** | `IcsCalendarService.cs:266` 用 `Calendar.Load(icsContent)` 解析，库内部按 RFC 5545 完成 unfold 与文本反转义。1.3MB 源不会截断 |
| T3 | 缓存能否扛住 1.3MB / 1096 事件？ | ⚠️ **机制可用，但有两个隐患** | 缓存路径 `%LOCALAPPDATA%\WinCal\cache\{url_hash}.ics`（`IcsCalendarService.cs:52-55`），SHA256 前 16 位做 key，**多源天然隔离** ✅；隐患见下方 T3-a / T3-b |
| T4 | 解析耗时是否可接受？ | ⚠️ **需实测，且现有实现有浪费** | `GetOccurrences(start, end)` 会为范围内每一天展开事件；源 B 是**一天一条**，跨 3 年 1096 条，每条带 1-3KB DESCRIPTION。见 T4-a |
| T5 | 「用途」标记如何持久化？ | ❌ **现有结构不支持，必须扩展** | `AppSettings` 的订阅配置目前只有 URL + 名称 + 颜色，无「用途」字段 |

### ★ 关键缺陷 T6（最严重，必须修）

**`CalendarEvent` 模型不承载任何 ICS 分类信息 —— 休/班判定当前根本无法实现。**

`Core/Models/CalendarEvent.cs` 全文只有 8 个字段：
```csharp
public record CalendarEvent(
    string Title, DateTime StartTime, DateTime EndTime, bool IsAllDay,
    string CalendarName, string Color,
    string? Location = null, string? Description = null
);
```

`IcsCalendarService.ConvertOccurrence()`（`:280-331`）只映射了 `Summary / StartTime / EndTime / IsAllDay / Location / Description`，
**完全没有读取 `CATEGORIES`、`X-APPLE-SPECIAL-DAY`、`TRANSP`、`CLASS`、`UID`**。

**影响**：
- C1 契约里的第二级 fallback（`X-APPLE-SPECIAL-DAY`）**当前拿不到数据**
- 唯一的可用通道是 `Description` —— 实测源 A 的 `DESCRIPTION` 恰好就是 `休`/`班` 二值，
  **因此第一级 fallback 可以立即工作，不需要改模型就能跑通基本功能** ✅
- 但要实现完整三级 fallback、以及**把「休班标记」与「日程事件」彻底分层（C10）**，
  必须扩展模型或新建独立数据通道

**架构师决策点**：建议**新建独立数据通道**而非给 `CalendarEvent` 加字段 ——
因为休班标记的消费方是「月历格子」，与「事件列表」是两个不同视图，
强行塞进同一个模型会让 C10 的解耦要求落空。推荐形态：新增 `DayMarker` 模型 + 从 ICS 原始
`CalendarEvent`（`Ical.Net`）直接提取标记，绕过 `WinCalEvent` 转换链路。

### T3-a 隐患：缓存无大小上限

1.3MB × N 个订阅源全部落盘，无清理策略。现有代码仅在 `EnsureCacheAsync` 里读写，**从不删除过期缓存**。
→ 建议：加缓存大小/数量上限，或至少提供设置页「清空缓存」。

### T3-b 隐患：`User-Agent` 写的是 `miniCal/1.0`

`IcsCalendarService.cs:49`：`"miniCal/1.0 (Calendar Client)"` —— 与 README 声称的 `WinCal` 品牌不一致
（关联 `00 文档 §4` 的数据目录名分裂问题）。建议品牌统一时一并改。

### T4-a 隐患：`GetOccurrences` 会重复展开

`_cachedCalendar.GetOccurrences(start, end)` 每次调用都重新展开。
面板每次翻月都调用 → 对 1096 条事件重复展开。建议按月缓存展开结果，或改为只查 `SUMMARY` 的轻量通道。

---

## 7. 对 00 文档的增量修正

00 文档 `§4` 列出的「现有 6 个代码问题」基础上，本契约新增 4 项：

| # | 问题 | 位置 | 严重度 |
|---|---|---|---|
| 7 | `CalendarEvent` 不承载 `CATEGORIES`/`X-APPLE-*`，休班判定需新通道 | `Core/Models/CalendarEvent.cs` | ★★★ 阻塞 P0-5 |
| 8 | 磁盘缓存无上限、无清理 | `IcsCalendarService.cs:52` | ★★ |
| 9 | `User-Agent` 品牌为 `miniCal` | `IcsCalendarService.cs:49` | ★ |
| 10 | `GetOccurrences` 无按月缓存，翻月重复展开 | `IcsCalendarService.cs:66` | ★★ |

---

**文档结束。** 本契约所有数字与样例均来自真实抓取 + 源码通读，可作为实现与验收的唯一依据。
