# P2-1 · catalog 说明文本英译（架构调研与改法）

> 状态：**已实施**（2026-09-25）。本文是这条线的设计依据与改法记录。
> 待办总表见 `docs/dev/PLAN-backlog.md` 的 P2-1。
> 验收：**英文 locale 下优化项说明无中文，中文原文不变。**
>
> 实施结果见文末「实施记录」——**其中有 3 处与原计划的偏差，动手改这块前先读**。

## 目标

`catalog/catalog.json` 里 33 项的 `name` / `description` / `sideEffect` 目前只有中文。
界面切到英文后仍显示中文（设置页的说明文案也明说了这一点，实施时要一并改）。

## 关键架构事实（已实测，别再重新推导）

1. **catalog 只有一个数据出口**：`OptimizationEngine.DetectAsync()`
   → `DetectionService.BuildDetectJson()`。GUI 检测页把这份 JSON 解析成 `AppState.Items`
   （`Views/DetectView.xaml.cs` 的 `ApplyDetectData`），CLI 的 `-Detect -Json` 输出同一份。
   **没有第二条路径** —— 所以本地化只要接在 `BuildDetectJson` 上，GUI 与 CLI 会同时生效。
2. **CLI 天然恒为中文**：`App.OnStartup` 里 CLI 分支（`CliHost.IsCliInvocation`）在
   `LangService.Load()` **之前**就 `return` 了（前者在第 22–27 行，后者在第 31 行）。
   所以 CLI 进程里 `LangService.Current` 恒为静态默认值 `zh-CN`。
   **这是天然的隔离点，不要动它。**
3. **GUI 才加载语言**：`App.OnStartup` 第 31–32 行 `LangService.Load()` + `Apply()`。
   切换语言走 `SettingsView.Lang_Checked` → `LangService.Apply()`。
4. `LangService.Current` 是「当前界面语言」的唯一来源；`Load()` 读
   `%LOCALAPPDATA%\FpsTune\lang.txt`，默认 `zh-CN`。

## 硬约束（踩了就是回归）

| 约束 | 出处 | 说明 |
|---|---|---|
| **不改 CLI `-Json` 的输出结构** | `AGENTS.md` / `AI-WORKFLOW.md` | 不要往 detect JSON 的 item 里加 `nameEn` 之类**新键**。改**值**可以，改**键**不行 |
| **`group` 不能翻译** | `Views/OptimizeView.xaml` | `group` 同时是**筛选键**：chip 的 `Tag="键鼠"` 与 `OptimizeView.xaml.cs` 里 `vm.Group != _groupFilter` 直接比较。翻译它会**静默破坏分类筛选** |
| **不能编造技术描述** | `docs/ROADMAP.md` 红线 | 33 条描述含注册表键名、服务名、SettingID；翻译要贴着中文原意，不得加戏 |
| **测试不得依赖机器状态** | `AI-WORKFLOW.md` §二 | 语言相关断言要显式控制语言，别读环境 |

## 推荐改法

### 1. catalog 加英文三字段

每项加 `nameEn` / `descriptionEn` / `sideEffectEn`，**中文原字段一律不动**：

```json
{
  "id": "mouse-accel-off",
  "name": "关闭鼠标加速",
  "nameEn": "Disable mouse acceleration",
  "description": "……",
  "descriptionEn": "……",
  "sideEffect": "……",
  "sideEffectEn": "……"
}
```

`sideEffect` 为空串的项（`gpu-pref` / `game-mode` / `net-throttling-off` / `mmcss-games`），
`sideEffectEn` 同样留空串。

### 2. 模型加可选参数 + 解析属性

`Core/OptimizationItemDefinition.cs` 的 positional record 末尾追加三个可选参数
（默认 `null`，保证旧 catalog 仍能解析），再加解析属性：

```csharp
public string DisplayName => Pick(Name, NameEn);

private static string Pick(string zh, string? en)
    => LangService.Current == LangService.EnUs && !string.IsNullOrWhiteSpace(en) ? en! : zh;
```

**回退策略**：英文缺失一律回退中文，**绝不返回空串**——宁可露中文，也不要空白条目。
（`Core` 已经 `using FpsTune.Wpf.Services`，引用 `LangService` 无分层问题。）

### 3. `BuildDetectJson` 改用解析属性

`name` / `desc` / `sideEffect` 三个**键不动**，值换成 `DisplayName` / `DisplayDescription` /
`DisplaySideEffect`。CLI 因恒为 `zh-CN`，输出与今天逐字节一致。

### 4. group 只做显示层本地化

`group` 值保持中文键不变，新增一份「键 → 英文标签」映射，只用在显示处：

- `Views/OptimizeView.xaml` 的 6 个筛选 chip 的 `Content`（`Tag` 必须保持中文键）
- `ListView.GroupStyle` 的 `HeaderTemplate`（当前绑定 `{Binding Name}`，即分组键）
- `Views/OptimizeView.xaml.cs` 里手绘的分组标题（`"— " + item.Group + " —"`）

### 5. 设置页说明文案要改

`Resources/Strings.zh-CN.xaml` / `Strings.en-US.xaml` 的 `Str.LanguageNote` 现在写的是
*"Applies immediately. Item descriptions stay Chinese. Default is Chinese."* ——
实施后前半句不再成立。

### 6. 语言切换后的刷新（易漏）

`AppState.Items` 是**快照**（来自上一次检测）。切语言只换 ResourceDictionary，
**不会重刷已有列表**。要么在 `LangService.Apply` 之后触发一次重新解析，
要么明确写清「切语言后需重新检测才更新条目文案」——二选一，别默认它会自动刷新。

## 守卫测试（实施时必须一起加）

1. **英文完整性**：33 项都有非空 `nameEn` / `descriptionEn`；`sideEffect` 非空的项
   必须有非空 `sideEffectEn`。
2. **英文里不能有中日韩字符**：对 `*En` 字段断言不匹配 `[\u4e00-\u9fff\u3040-\u30ff]`。
3. **回退正确**：`*En` 为空时返回中文原文（用最小 catalog JSON 构造验证）。
4. **CLI 不受语言影响**：显式设为 `en-US` 后 `BuildDetectJson` 仍应输出中文
   （因为 CLI 不调 `Load()`）。

> ⚠️ **竞态提醒**：`FpsTune.Wpf.Tests/CoreLogicTests.cs` 里有测试会调
> `LangService.Save("en-US")` 再复位 `zh-CN`，而 xUnit 默认并行跑不同测试类。
> 任何依赖「当前语言」的断言都会与它竞态——务必显式设置语言并在 finally 里复位。

## 落地顺序建议

1. 先写守卫测试 1（此时会红）→ 再补 33 条英文 → 转绿。
2. 再接模型与 `BuildDetectJson`（补守卫测试 2 / 3 / 4）。
3. 最后做 group 显示层与设置页文案（纯 UI，无守卫测试，需人工目检英文界面）。
4. 收工按 `AGENTS.md` 更新 `AGENTS.md` + `docs/HANDOFF.md` + `PLAN-backlog.md` 并提交。

---

## 实施记录（2026-09-25）

### 改了什么

| 改动 | 文件 |
|---|---|
| 33 项各加 `nameEn` / `descriptionEn` / `sideEffectEn`；键顺序统一为 id → name → description → sideEffect → admin → default → reboot → kind → group | `catalog/catalog.json` |
| record 末尾加 3 个可选参数 + `DisplayName` / `DisplayDescription` / `DisplaySideEffect`（英文缺失回退中文） | `Core/OptimizationItemDefinition.cs` |
| `name` / `desc` / `sideEffect` 三个键的**值**改用 `Display*`（**键名与顺序不变**） | `Core/DetectionService.cs` |
| 分组**显示标签**映射（中文键 → 英文标签） | `Core/CatalogGroups.cs`（新） |
| 分组键 → 显示标签的 `IValueConverter` | `Views/CatalogGroupLabelConverter.cs`（新） |
| 筛选 chip 的 `Content` 改 `DynamicResource`（**`Tag` 仍是中文键**）；GroupStyle 标题走转换器；两处手绘分组标题走 `CatalogGroups.Display` | `Views/OptimizeView.xaml` / `.xaml.cs` |
| 6 个分组文案键；`Str.LanguageNote` 措辞更正（原写"优化项说明仍为中文"，实施后已不成立） | `Resources/Strings.zh-CN.xaml` / `Strings.en-US.xaml` |
| 不落盘的测试钩子 `SetCurrentForTest`（避免单测污染本机 `lang.txt`） | `Services/LangService.cs` |
| 4 条守卫测试 | `FpsTune.Wpf.Tests/CatalogConsistencyTests.cs` |

### 与原计划的 3 处偏差

1. **分组标签做了，但 `OptimizeView` 的其余页面文案仍是中文。**
   调研中发现该页 chrome（标题「系统优化」、预设名「均衡推荐」「保守优化」等）**从未做过本地化**
   —— 那属于 P1-2 的遗留范围，已拆成新的待办 **P2-7**。
   所以英文界面下：**条目名称/说明/分组标签是英文，页面 chrome 仍是中文**。这是有意分步，不是漏做。
2. **分组标题字体由等宽改为正文**（`FontMono` → `FontBody`）：英文标签是短语
   （"Keyboard & mouse"），等宽下观感不对。
3. **多了一条原计划外的架构守卫** `Cli_startup_path_never_loads_the_ui_language`，
   锁定「CLI 分支必须先于 `LangService.Load()`」这一不变量。
   它**首次运行时误报了自己**——正则匹配到了本文档提到的 `LangService.Load()` 字样所在的
   文档注释。修法是先 `StripCommentLines` 剥掉整行注释再匹配。

### 验证（原始输出）

```
dotnet test FpsTune.Wpf.Tests/FpsTune.Wpf.Tests.csproj -c Release
-> 已通过! - 失败: 0，通过: 270，已跳过: 0，总计: 270，持续时间: 45 s
```

端到端验证 CLI 机器协议未变（把 `%LOCALAPPDATA%\FpsTune\lang.txt` 临时设为 `en-US` 后跑 `-Detect -Json`）：

```
lang.txt = zh-CN  ->  name: 关闭鼠标加速
lang.txt = en-US  ->  name: 关闭鼠标加速   ← 仍是中文，符合设计预期
item 键 = ['id','name','desc','sideEffect','admin','default','reboot','optimized','current','group']
键结构与改动前一致: True
```

`catalog.json` 数据自检（脚本 `work/validate-catalog.py`，不入库）：
33 项 × 3 字段完整、英文无中日韩字符、空白项一致、无 BOM；
admin 22 / reboot 13 / balanced 27 / safe-only 5 —— 与 README 口径一致。

### 未验证

- **英文界面下的 GUI 人工目检**（需要人在场看窗口）。单元测试覆盖了语言取值路径，
  但 XAML 的 `DynamicResource` 渲染、分组标题与筛选 chip 的实际观感未经肉眼确认。
  切语言后需**重新检测**才会刷新条目列表（见上文"语言切换后的刷新"）。

