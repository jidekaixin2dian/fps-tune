# P2-1 · catalog 说明文本英译（架构调研与改法）

> 状态：**调研完成，尚未实施**。本文记录 2026-09-25 的代码调研结论，实施前先读一遍。
> 待办总表见 `docs/dev/PLAN-backlog.md` 的 P2-1。
> 验收：**英文 locale 下优化项说明无中文，中文原文不变。**

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
