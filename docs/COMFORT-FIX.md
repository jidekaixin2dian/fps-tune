# 界面微调与优化页分组异常修复

- 去掉设置分类、主题和界面模式选中时的对勾，仅用青色底色表示选中；保留键盘焦点边框。
- 设置说明文字 12 → 13，主题标题 15 → 16，开关内容使用 14 号中文字体。
- 顶栏模式按钮最小宽度 136、高度 32，增加左右留白。
- 常用按钮统一 13 号中文字体，提示框 12 号中文字体，默认次要说明 13 号；页面结构保持不变。

## 优化页异常

用户导出的 fps-tune-log.txt 仅有占位提示。实际异常位于本机应用 logs/error.log，2026-09-07 08:55:54 和 09:20:32 的调用栈均指向 OptimizeView.ReloadFromState。

此前把 ObservableCollection 的逐项添加放入 ItemsView.DeferRefresh 作用域。分组 ListCollectionView 处理集合变化时需要读取 CurrentPosition，触发“延迟 Refresh 时无法更改或检查 CollectionView 的内容或 Current 位置”。这属于此前性能改动引入的回归。

修复移除不适用的 DeferRefresh，仍仅在检测快照变化时重建小规模列表，并在更新成功后才保存快照引用，避免异常后把未完成更新误判为已完成。

## 验证

- 完整回归测试 185 / 185 通过。
- 独立 STA 验证使用真实 App 资源与 OptimizeView：首次分组加载、移动当前位置、重复加载、更新快照和保留自定义勾选全部通过。
- 本地验证代码和结果保留在 review-output/optimize-probe，测试日志位于 review-output/console-refresh/comfort.trx。
- 生图按本轮请求重试两次，两次均连接失败，无候选图片产生，也未替换现有图标。可直接使用 docs/ICON-PROMPT.md 中的提示词。
