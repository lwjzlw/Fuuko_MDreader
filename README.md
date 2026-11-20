# Fuuko MDreader

**Markdown 阅读器 0.6.0** — 一个轻量、克制的 Windows 桌面 Markdown 阅读与写作工具，C# / WPF 原生渲染（非 Electron / WebView2）。
预览由 [MdXaml](https://github.com/whistyun/MdXaml) 生成 WPF `FlowDocument` 并经自定义排版与主题管线后处理，源码编辑基于 `AvalonEdit`。

## 界面

- **一行顶栏**：左侧侧栏 / 后退 / 前进图标，中间文件名与所在目录（目录先截断），右侧「阅读 · 分栏 · 源码」分段、打开、保存、深浅色与「更多」菜单。不常用的命令（新建、另存为、导出为 PDF、重新加载、复制路径、在资源管理器中显示、复制全文、字号、各类开关）都收进「更多」菜单；右键文件名可复制路径、定位文件或导出 PDF。
- **居中版心**：阅读视图正文宽约 46 个字号（16px 时约 736px），宽窗口两侧留白均分；纯源码视图同样居中。字号变化时版心同步放宽。
- **侧栏**：「大纲 / 文档」文字标签，筛选框。大纲按层级缩进、当前章节高亮；文档页列出「此文件夹」与「最近」（不重复列出）。
- **状态栏**：左侧临时提示（约 4 秒），右侧阅读进度或行列、字数与预计阅读时间、编码、换行。
- **配色**：浅色纯白画布 + 中性浅灰侧栏，深色为中性炭灰；顶栏与画布同色，层级只靠 1px 细线和少量底色差。深色时系统标题栏同步变深（Windows 10 2004+/11）。
- **状态覆盖**：悬停 150ms 淡入、键盘焦点环、大文件骨架屏、空状态（打开 / 新建 / 剪贴板新建 + 最近文件）、内联错误与外部修改冲突横幅；窄屏（<860px）自动收起侧栏和目录路径，极窄（<640px）分栏改为上下堆叠。

## 功能与便捷措施

- **打开与切换**：支持 `.md` / `.markdown` / `.mdown` / `.mkd` / `.mmd`，拖放、命令行参数、`Ctrl+O`。
  - `Ctrl+P` 快速切换：最近文档优先、其次同文件夹文档，输入即筛选（文件名支持跳字匹配）；输入 `#` 开头则跳转到当前文档的标题。
  - `Ctrl+PgUp` / `Ctrl+PgDn`：同文件夹上一篇 / 下一篇（按文件名循环）。
  - `Alt+←` / `Alt+→`：在文档与章节位置之间后退 / 前进；本地 Markdown 相对链接与 `#锚点` 可直接跳转。
- **三种视图**：`Ctrl+1` 阅读、`Ctrl+2`（或 `Ctrl+\`）分栏、`Ctrl+3`（或 `Ctrl+E`）源码。分栏模式下预览按源码滚动比例跟随，重新排版后不再跳回顶部。
- **专注模式**（`F11`）：只留正文，隐藏顶栏、侧栏、格式栏、状态栏；`F11` 或 `Esc` 退出。
- **导出为 PDF**（`Ctrl+Shift+E`）：在「更多」菜单、文件名右键菜单或按 `Ctrl+Shift+E`，将当前文档按 A4 白底纸面排版直接导出为原生矢量 PDF（内嵌子集化 TrueType 字体与 `ToUnicode` 映射，文字可选中与搜索，保留标题、列表、表格、代码块与图片，无论当前界面处于深色或浅色主题均按白底纸面输出）。
- **阅读位置**：按文档记忆滚动位置、源码行号和当前章节，切换文档或重启后恢复（最多 30 篇）。
- **编辑辅助**：
  - 格式栏：H1–H3（同级再点取消、异级直接替换）、粗体 / 斜体 / 删除线 / 行内代码（再点一次取消）、代码块、链接、插入图片、引用、无序 / 有序 / 任务列表（可作用于多行选区）、表格；折行开关与查找。
  - 回车续写列表与引用（有序列表自动递增，勾选项续写为未勾选）；空列表项回车结束列表；代码块内不续写。
  - `Tab` / `Shift+Tab` 调整列表层级。
  - 选中文字后输入 `*` `_` `` ` `` `~` `"` `(` `[` `“` `《`，直接包裹选区。
  - 粘贴截图：剪贴板只有图片时，存为 `assets/<文档名>-<时间>.png`（重名追加序号，不覆盖）并插入引用；文档需先保存。
  - 选中文字后粘贴网址，自动生成 `[文字](网址)`；`Ctrl+K` 插入链接时优先使用剪贴板中的网址。
  - 拖入图片（可多张）插入相对路径引用。
  - 快捷键：`Ctrl+B` / `Ctrl+I` / `Ctrl+K` / `Ctrl+Shift+X`（删除线）/ `` Ctrl+` ``（行内代码）/ `Alt+Z`（折行）。
- **无损读写与外部变更保护**：
  - 保留 UTF-8（含 / 无 BOM）、GBK / GB18030（无 BOM 且非合法 UTF-8 时按严格 GB18030 解码，必须逐字节还原才接受）、带 BOM 的 UTF-16 / UTF-32，以及 LF / CRLF / CR / 混合换行。
  - 状态栏点击编码：UTF-8 ⇄ UTF-8 BOM，其他编码一键转为 UTF-8；点击换行在 LF / CRLF 间切换；保存后生效。
  - 保存使用同目录临时文件、跨进程命名互斥锁与 SHA-256 指纹复核后原子替换；无改动时跳过写盘。
  - 监听外部修改：无本地改动且开启自动同步时自动刷新；有未保存改动时显示冲突横幅（加载磁盘版本 / 另存当前版本）。
- **其他**：`Ctrl+Shift+B` 侧栏、`Ctrl+Shift+L` 深浅色、`Ctrl+=` / `Ctrl+-` / `Ctrl+0` 字号（12–30px）、`F1` 快捷键速查、`Ctrl+W` 关闭窗口。`Esc` 逐层退出（浮层 → 专注模式 → 横幅 → 编辑回到阅读），不再关闭窗口。

## 运行与构建

运行需要 Windows 10/11 与 [.NET 7 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/7.0)。把 `.md` 文件拖到 `MarkdownReader.exe` 上，或用命令行 `MarkdownReader.exe 文件.md` 打开。

从源码构建需要 .NET 7 SDK：

```powershell
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

发布后得到约 1 MB 的单文件 `publish\MarkdownReader.exe`。设置保存在 `%APPDATA%\MarkdownReader\settings.json`。

## 目录结构

- `MainWindow.xaml` / `MainWindow.xaml.cs`：主窗口、样式与菜单、侧栏、三视图、格式栏、快速切换与快捷键浮层、专注模式、PDF 导出入口、编辑辅助、响应式布局与外部变更监测
- `PdfExporter.cs`：原生矢量 PDF 导出器（A4 分页、子集化 TrueType 字体与 `ToUnicode` CMap、矢量几何与图片 XObject 内嵌）
- `MarkdownFile.cs`：编码 / 换行识别与转换（含 GB18030）、SHA-256 指纹、原子替换、大纲解析、列表续写规则、文本统计与同目录文档扫描
- `AppSettings.cs`：窗口几何、字号、主题、视图模式、侧栏状态、最近文件与阅读位置（`%APPDATA%\MarkdownReader\settings.json`）
- `App.xaml` / `App.xaml.cs`：应用入口（样式集中在主窗口）
- `app.ico` / `app.manifest`：多尺寸应用图标（16–256px）与 PerMonitorV2 DPI 感知

## 已知限制

- 除 UTF-8 / GB18030 / 带 BOM 的 UTF-16、UTF-32 外，Shift-JIS、Big5 等编码需先转换；无法解码时在顶部横幅提示。
- 文件大小上限 16 MiB。
- LaTeX 数学公式与复杂内嵌 HTML 块按纯文本处理。
- 分栏滚动同步按整体比例对齐，图片或长表格较多时可能有偏差。
- 图标使用系统字体 Segoe Fluent Icons / Segoe MDL2 Assets（Windows 10/11 自带）。

## 许可证

[MIT](LICENSE)
