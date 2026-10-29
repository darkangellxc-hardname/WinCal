# WinCal — Windows 任务栏日历

> 一款轻量、优雅的 Windows 任务栏日历工具，灵感来自 macOS 平台的 Itsycal。
> 点击任务栏右下角的时间即可唤出月历面板，支持农历、节气、休班标记与黄历详情。

本仓库 fork 自 [ha0719/WinCal](https://github.com/ha0719/WinCal)，在原版基础上做了本地化定制，
详见文末[与上游的关系](#与上游的关系)。

## 下载与使用

前往 [Releases](../../releases) 下载，**把该版本的全部文件（`WinCal.exe` + 5 个 DLL）放进同一个目录**，
双击 `WinCal.exe` 即可运行。

- 主程序 `WinCal.exe` 约 65 MB，**已自包含 .NET 8 运行时**，无需另装 .NET；
- 但 `dotnet publish` 单文件模式**不会把原生库打进 exe**，因此还需以下 5 个文件同目录存放：
  `PresentationNative_cor3.dll`、`wpfgfx_cor3.dll`、`D3DCompiler_47_cor3.dll`、
  `PenImc_cor3.dll`、`vcruntime140_cor3.dll`
- **缺任何一个 DLL，进程仍会启动但面板画不出来**（表现为点时钟没反应，且不报错）；
- 支持 Windows 10 1903+ / Windows 11（x64）；
- 首次运行会询问是否开机自启；
- 配置文件位于 `%LOCALAPPDATA%\WinCal\settings.json`。

> ⚠️ 本程序为绿色免安装工具，**没有代码签名证书**。若 SmartScreen 提示「已保护你的电脑」，
> 点击「更多信息」→「仍要运行」即可。

## 功能特性

- 🗓️ **点击时钟唤起** — 点任务栏时间弹出面板，再点一次收起；支持 Win10 风格与 Win11 原生 XAML 任务栏
- 🖥️ **多显示器跟随** — 在哪个屏幕上点的时钟，面板就出现在哪个屏幕
- 📅 **农历与节气** — 日期格下方显示农历，节气与农历节日高亮
- 🏖️ **休班标记** — 「休 / 班」角标与当月休班速览条，节假日调休一目了然
- 📖 **黄历详情** — 宜忌、冲煞、建除十二神、二十八宿、吉神凶煞、月相
- 🔍 **事件与详情** — 有事件的日期显示彩色圆点，悬停事件项弹出详情浮层
- 🌐 **ICS 订阅** — 可添加多条订阅，并为每条指定用途（节假日 / 农历节气 / 日程）
- 📌 **面板固定** — 固定后失去焦点也不自动关闭
- ⏱️ **年月快速跳转** — 点标题栏年月，可在月份网格与年份网格间切换
- 🎨 **深浅主题** — 跟随系统或手动指定，全部控件随主题联动
- ⌨️ **快捷键唤起** — 默认 `Ctrl+Alt+C`，可自定义
- 🚀 **开机自启** — 一键写入注册表启动项

## 技术栈

| 技术 | 说明 |
|------|------|
| C# 12 + .NET 8 | 主开发语言和运行时（`net8.0-windows`） |
| WPF | UI 框架，矢量渲染 + 自定义控件模板 |
| MVVM | CommunityToolkit.Mvvm |
| Hardcodet.NotifyIcon.Wpf | 系统托盘图标 |
| Ical.Net | ICS 日历解析 |
| `WH_MOUSE_LL` 低级鼠标钩子 | 捕获任务栏时钟点击 |
| UI Automation | 定位 Win11 原生任务栏的时钟元素 |

## 项目结构

```
WinCal/
├── App.xaml / App.xaml.cs          # 应用入口、托盘图标、快捷键装配
├── WinCal.csproj                   # 项目配置（单文件发布、图标、DPI 清单）
├── app.manifest                    # PerMonitorV2 DPI 感知
│
├── Core/                           # 核心层
│   ├── Models/                     #   模型（事件、日期格、黄历、休班标记）
│   ├── Services/                   #   数据源分层、ICS 解析、设置、首启引导
│   ├── Helpers/                    #   定位、主题、农历、指标、开机自启
│   └── Interop/                    #   鼠标钩子、任务栏时钟定位、全局热键
│
├── ViewModels/                     # ViewModel 层
│   ├── CalendarViewModel.cs        #   月历网格与事件聚合
│   └── EventListViewModel.cs       #   事件列表
│
├── Views/
│   ├── PopupWindow.xaml            #   主面板
│   ├── SettingsWindow.xaml         #   设置窗口
│   ├── EventDetailWindow.xaml      #   事件详情浮层
│   ├── Controls/                   #   月历、日期格、事件项、黄历面板、休班速览条
│   └── Themes/                     #   主题与控件样式
│       ├── Light.xaml / Dark.xaml  #     配色（只含颜色与画刷）
│       ├── Controls.xaml           #     全部内置控件的主题化模板
│       └── Metrics.*.xaml          #     紧凑 / 标准 / 宽屏三档尺寸
│
├── Assets/WinCal.ico               # 应用图标（16–256，9 档）
└── docs/                           # 设计文档
```

## 构建与运行

### 前置要求

- .NET 8 SDK
- Windows 10 1903+ 或 Windows 11
- Visual Studio 2022 或 VS Code + C# Dev Kit

### 开发调试

```bat
build.bat
```

### 发布单文件

```bat
publish.bat
```

产物：`dist/WinCal.exe`（约 65 MB，托管程序集全部内嵌，无需安装 .NET 运行时）
**＋ 5 个原生 DLL**（`PresentationNative_cor3.dll` / `wpfgfx_cor3.dll` / `D3DCompiler_47_cor3.dll` /
`PenImc_cor3.dll` / `vcruntime140_cor3.dll`），共 6 个文件，分发时必须整体给出。

> 若想打成真正的单文件，在 `WinCal.csproj` 加一行
> `<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>`，
> 重新发布后 `dist/` 只剩一个 exe（约 69 MB，原生库在首次启动时解压到 `%TEMP%\.net\`）。

## 配置

设置文件位于 `%LOCALAPPDATA%\WinCal\settings.json`，界面内可调整：

- 界面主题（浅色 / 深色 / 跟随系统）与字体大小
- 顶部时间日期显示与时间格式
- 弹出快捷键、开机自启动、托盘图标显示
- 日历数据源（仅 ICS 订阅可用，原因见下）
- ICS 订阅列表（每条可设用途与别名）与自动刷新间隔
- 启用日程、日程区默认展开、近期事件显示范围
- 周起始日、是否使用模拟数据

### 关于「系统日历」数据源

原版支持读取 Windows 系统日历，但它依赖 WinRT 日历 API，需要 Windows 10 SDK 才能编译。
本分支未启用该依赖，因此**「系统日历」数据源在本构建中不可用**，设置界面会明确标注。
请使用 ICS 订阅作为数据来源，程序不会静默降级。

### 可用的 ICS 订阅源

| 用途 | 示例 |
|---|---|
| 节假日 / 休班 | `https://cdn.jsdelivr.net/npm/chinese-days/dist/holidays.ics` |
| 农历 / 节气 / 黄历 | `https://qilpmgl.github.io/MyCalendar/chinese_almanac.ics` |

更多订阅源可参考 <https://yangh9.github.io/ChinaCalendar/>。

## 开发文档

- [产品与开发方案](WinCal_产品与开发方案.md)
- [开发进度记录](PROGRESS.md)
- [设计文档目录](docs/)
- [第三方组件声明](THIRD-PARTY-NOTICES.md)

## License

[MIT](LICENSE)。原始版本版权归 [ha0719](https://github.com/ha0719/WinCal) 所有，
本分支的修改同样以 MIT 授权。

## 与上游的关系

本仓库是 [ha0719/WinCal](https://github.com/ha0719/WinCal) 的 fork，主要差异：

1. **任务栏交互重做** — 上游依赖 `SetWinEventHook` 隐藏并替换系统日历弹窗，该方案在 Windows 11
   的 XAML 任务栏上失效；本分支改为 `WH_MOUSE_LL` 鼠标钩子 + UIA 定位时钟元素，兼容 Win10 风格与原生 Win11 任务栏。
2. **数据源分层** — 按用途把 ICS 订阅拆为「节假日 / 农历节气 / 日程」三条独立数据流，互不干扰。
3. **界面重构** — 新增黄历详情面板、当月休班速览条、面板固定、年月快速跳转，并重做了设置窗口与全部控件主题。
4. **多显示器修复** — 面板改为跟随点击所在屏幕弹出，跨屏定位改用物理像素直置。
5. **本地化细节** — 数据目录由 `miniCal` 更名为 `WinCal`（含一次性迁移），构建脚本改为自动探测 .NET SDK 路径。
