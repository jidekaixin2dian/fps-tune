# V4 flash vision GUI 设计提示词

> 把下面「提示词正文」整段复制，发给 V4 flash vision 版本使用。
> 可选：附上 `delta-gui.ps1` 文件内容，或当前功能版截图。

---

## 提示词正文

你是一位资深 UI/UX 设计师，请为 Windows PowerShell WinForms 桌面应用
**delta-force-tune** 重新设计视觉方案，风格参考 <https://21st.dev/> 和
shadcn/ui 暗色主题 dashboard。

### 项目背景

- delta-force-tune 是《三角洲行动》的 Windows 系统层帧率优化工具。
- 现有 GUI 是功能版：PowerShell 5.1 + WinForms，入口文件 `delta-gui.ps1`。
- 你的任务只做视觉/布局设计，功能逻辑不动。
- 用户是竞技游戏玩家，喜欢暗色、科技感、高效信息呈现。

### 风格要求

- 暗色为主：背景接近 `#09090b` / `#0a0a0a`，面板 `#111113` / `#131316`，
  使用 1px 半透明边框。
- 主色用 emerald → cyan 渐变（如 `#10b981` → `#22d3ee`），代表
  “安全 / 已应用 / 达标 / 可用”。
- 状态色：ok = 绿色，attention = 琥珀色，danger = 红色，disabled = 灰色。
- 字体：中文用微软雅黑；数字与英文用 Inter / JetBrains Mono；
  FPS 数字使用大号等宽字体。
- 圆角 10–14px，卡片式布局；hover 时边框轻微增亮，按钮有渐变或高亮边。
- 参考 21st.dev 的 dark dashboard、cards、tabs、status badges 风格。
- 输出必须能映射回 WinForms 控件（Button / Label / Panel / TabControl /
  CheckedListBox / TextBox），不要使用 HTML/CSS 运行时；HTML 只可作为静态 mockup。

### 需要设计的 5 个页面（Tab）

1. **检测页**
   - 硬件信息卡片：CPU / GPU / 内存 / 系统版本 / 是否笔记本 / 是否管理员。
   - 游戏路径卡片。
   - 3 项只读体检：VC++ 运行库、内存频率、PCIe 链路（状态用 ok / attention 徽章）。
   - 按钮：运行检测、加载到优化页。

2. **优化页**
   - 预设选择：full / balanced / safe-only / 自定义勾选。
   - 22 个优化项列表：每项显示 id、说明、是否需要管理员、是否需要重启。
   - 同意确认复选框（执行前必须勾选）。
   - 按钮：应用、还原全部。
   - 结果区：成功/失败/跳过摘要、备份文件路径、需要重启的项列表。

3. **A/B 实验页**
   - 步骤按钮：1 基线、2 group-1、3 group-2、4 group-3、5 报告。
   - 运行状态提示（采样中，请保持场景固定）。
   - 结果卡片：平均 FPS / 1% low / P99 / 卡顿 / CV / keep 或 revert。
   - 按钮：打开实验目录。

4. **朋友测试页**
   - 表单：昵称、场景/画质/设置、优化前平均 FPS、优化前 1% low、
     优化后平均 FPS、优化后 1% low、备注。
   - 按钮：生成记录表、打开输出目录。
   - 预览区：生成的 Markdown 表格内容。

5. **备份 / 日志页**
   - 按钮：列出可还原项、还原全部、打开备份目录、打开 GUI 临时目录。
   - 日志/JSON 输出文本框。

### 输出要求

1. 一套设计 tokens：颜色、字体、字号、圆角、间距、状态色。
2. 每个 Tab 的 ASCII 线框图和组件说明。
3. 关键状态视觉说明：默认、hover、点击、运行中、成功、失败、禁用。
4. 可选：一个 HTML 暗色 mockup（静态即可），作为最终视觉参考。
5. 红线：不要修改任何功能逻辑；不要加入显卡伪装、外置准星、Overlay 相关功能。
