# AI 协助开发纪律（本仓库）

> 适用对象：任何在本仓库做开发的 AI 助手（Claude Code / Codex / GLM / 豆包 …）与人类协作者。
> 来源：仓库所有者在 2026-09-12 明确要求 —— **所有大的操作都必须提交 git**，不允许把已验证的改动留在工作区。
> 配套文档：本地交接文档 `HANDOFF_PROMPT*.md`（按 `.gitignore` 约定**不入库**）、`TESTING.md`、`docs/ROADMAP.md`。

---

## 一、提交纪律（硬要求）

1. **动手前先落一个检查点提交**，确保任何时刻都能回退：

   ```powershell
   git commit --allow-empty -m "chore: checkpoint before <本次目标>"
   # 需要放弃时：git reset --hard <该检查点>
   ```

   **什么算"大操作"（必须做检查点）**：改动 >1 个文件，或涉及系统写入、构建/发布流程、数据迁移、依赖升级、目录/权限调整。
   单文件纯文档或注释类改动可省检查点，但必须**从干净工作树**开始（此时干净 HEAD 本身就是锚点）。

2. **一个可独立回退的改动 = 一个提交**。改完立即提交，不要攒着。

3. **提交信息**：conventional commits 前缀（`fix:` / `feat:` / `test:` / `docs:` / `chore:` / `refactor:`），正文用中文写清三段：

   - **根因**：为什么会这样（不是"我改了什么"）；
   - **改法**：关键设计取舍，以及为什么这样改（而不是只把现象按住）；
   - **验证**：跑了什么、结果如何、**哪些没验证**。

4. **提交后必须向用户报告**：提交 hash 列表 + 测试结果（通过/失败/跳过数）+ 未验证项与残留风险。

5. **绝不提交**：`review-output/`、`work/`、`dist/`、本地 AI 交接文档、本地测试输出、任何真实用户数据或凭据。

6. **与仓库既有约定冲突时先问用户**，不要擅自突破。例：本仓库刻意用 `.gitignore` 忽略 `HANDOFF_PROMPT*.md`（本地 AI 交接文档不入库）。

---

## 二、改动前后必须做的验证

- **构建 + 全量单测**（Windows PowerShell，`-c Release`；`chcp 65001` 避免中文乱码）：

  ```powershell
  dotnet build .\FpsTune.Wpf\FpsTune.Wpf.csproj -c Release
  dotnet test  .\FpsTune.Wpf.Tests\FpsTune.Wpf.Tests.csproj -c Release
  ```

- **单测不得碰真实系统**：
  - 还原类注入 `BackupService.RestoreRecordOverride` / `BackupService.BackupDirOverride`；
  - 目录/状态类用 `PerformanceSessionStore.OverrideDir`、`AutoProfileActivityStore.OverrideDir`、`DiagnosticReportExporter.BaseDirOverride`；
  - 脚本类用 `-Simulate` 并把 `LOCALAPPDATA` 指到临时目录；
  - **会改静态注入点的测试类必须挂 `[Collection("BackupService serial")]`**，否则并行互相踩。
- **修 bug 先想"哪条测试能抓住它"**，没有就补一条防回归测试；新增行为同理。
- **自动化覆盖不到的路径要如实标注**，并给用户一个可执行的人工验证步骤（需要真机/游戏/提权会话的尤其如此）。
- 文档、`.gitignore` 这类改动不影响编译与测试时，可不必重跑，但要在提交信息里说明"已确认无测试读取这些文件"。

### 环境变量：Git Bash 里 Windows 系统变量名是全大写的

在 Git Bash / MSYS2 里，`$ProgramFiles`、`$SystemRoot` 取到的是**空值**，但
`${PROGRAMFILES}`、`$SYSTEMROOT` 有值。**这不是环境坏了，也不是变量丢了**——别去"补变量"。

- **根因**：MSYS2 运行时（`winsup/cygwin/environ.cc`）在进程启动时走
  `win32env_to_cygenv()` → `ucenv()`，按一张硬编码表 `renv_arr[]` 把下列 Windows 变量名
  **改写为全大写**：`PROGRAMFILES` / `COMMONPROGRAMFILES` / `COMSPEC` / `SYSTEMDRIVE` /
  `SYSTEMROOT` / `WINDIR` / `PATH` / `TEMP` / `TMP`（MSYS 下另有 `MSYSTEM`）。
  源码注释原文："Minimal list of Windows vars which must be converted to uppercase.
  Either for POSIX compatibility of for backward compatibility with existing applications."
  匹配是大小写不敏感的（`strncasematch`），命中后把名字 `strncpy` 成大写形式。
- **不在表里的名字不受影响**：`ProgramFiles(x86)` / `ProgramW6432` / `CommonProgramFiles(x86)` /
  `CommonProgramW6432` / `ProgramData` 都保持原样（2026-09-24 实测确认，原因就是它们不匹配表里
  带 `=` 的条目）。
- **bash 变量名区分大小写**，所以 `$ProgramFiles` 取不到；PowerShell 的 `$env:ProgramFiles` 与
  .NET 的 `Environment.GetEnvironmentVariable` 都是大小写不敏感的，**完全不受影响**。
- **结论：不需要给任何命令补 env。** 2026-09-24 实测：不做任何修补时
  `dotnet restore --force` 退出码 0、`dotnet test -c Release` **266/266 通过**。
  仓库内只有两处引用该变量——`build-installer.ps1`（PowerShell）与
  `FpsTune.Wpf/Core/NativeSystem.cs`（.NET API），两者都不受影响。
- **只有在 bash 里显式读这些变量时才需要处理**。用大写名，或显式归一化：

  ```bash
  echo "${PROGRAMFILES}"          # C:\Program Files
  export ProgramFiles="${PROGRAMFILES}"   # 需要小写名时（少见）
  ```

  带括号的名字（`ProgramFiles(x86)`）在 bash 里无法用 `$` 直接引用，也不建议 `eval` 硬凑；
  真要取就用 PowerShell 的 `${env:ProgramFiles(x86)}` 或 .NET API。

---

## 三、代码与写法的既有约定（摘要，完整背景见交接文档）

- **诚实报告优先**：宁可显示"未还原 / 失败 / 结果不确定"，也绝不谎报成功。任何"判定为成功"的显示都必须有对应的事实依据（`changed` 标记、`restored` 列表这类）。
- **根因修复优先**：不要用吞异常、兜底默认值、放宽断言来掩盖问题。
- **catalog 是优化项唯一数据源**：`catalog/catalog.json` ↔ 原生引擎 ↔ 预设必须一致，由 `CatalogConsistencyTests` 守卫；新增/改名优化项要同时改引擎与测试。
- **备份/还原语义不可弱化**：目标白名单、未决还原标记（journal）、`.restored` 审计改名、"原本不存在"状态都要保留；还原必须锚定调用方指定的那份快照，**绝不回退去动更早的备份**。
- **脚本执行路径**：只能执行"与内置资源逐字节一致、且执行期被租约锁住"的副本；由真正执行的子进程按可信哈希复校验；**禁止**重新引入 `%TEMP%` 包装脚本、禁止 `File.Create` 覆盖式释放、**禁止用数组 splatting 传具名参数**（会把 `-Name` 当位置参数）。
- **行尾**：仓库存 LF。Windows 检出若出现大批"纯行尾"差异（增删完全对称），**不要提交**；根治已用仓库根 `.gitattributes`（`* text=auto eol=lf`）。
- **UI 文案与产品口径**：不要自行改产品定位、红线或对外承诺；改文案前先问。

---

## 四、相关文档索引

| 文档 | 用途 |
|---|---|
| `TESTING.md` | 用户侧测试流程与数据回传模板 |
| `docs/ROADMAP.md` | 产品定位、五条红线、版本节奏 |
| `docs/REVIEW-1.6.1.md` | 上一轮公开审查的范围、修复项与**已知边界**（其中"不隔离已控制本机账户的攻击者"是本项目的既有声明） |
| `RELEASE.md` / `publish-release.ps1` / `build-installer.ps1` | 发布流程与产物校验 |
| `SKILL.md` | 面向"帮用户优化机器"的助手技能流程（**不是开发文档**） |
| `catalog/catalog.json` | 33 项优化定义与预设（唯一数据源） |
