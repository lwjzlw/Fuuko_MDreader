using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Search;
using MdXaml;
using Microsoft.Win32;

namespace MarkdownReader;

public partial class MainWindow : Window
{
    private const double NarrowBreakpoint = 860;
    private const double MobileSplitBreakpoint = 640;

    private readonly AppSettings _settings;
    private Palette _palette;
    private readonly Markdown _engine = new();
    private readonly SearchPanel _editorSearch;
    private readonly DispatcherTimer _splitRenderTimer;
    private readonly DispatcherTimer _watcherDebounceTimer;
    private readonly Dictionary<string, Paragraph> _headings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<NavEntry> _backStack = new();
    private readonly Stack<NavEntry> _forwardStack = new();

    private const string WelcomeText = """
        # 排版示例

        把 `.md` 文件拖进窗口，或按 **Ctrl+O** 打开。按 **Ctrl+P** 可在同文件夹和最近的文档之间快速切换。

        ## 阅读与写作

        1. `Ctrl+1` 阅读，`Ctrl+2` 左写右看，`Ctrl+3` 源码；`F11` 专注模式只留正文。
        2. 编辑时回车会自动续写列表，`Tab` / `Shift+Tab` 调整列表层级；选中文字再输入 `*`、`` ` ``、`(` 会直接包裹。
        3. 粘贴截图会存到文档旁的 `assets` 文件夹并插入引用；选中文字后粘贴网址，自动生成链接。

        > 文件按原编码与换行保存（UTF-8 / GBK / UTF-16 / UTF-32），写盘前复核磁盘版本，避免覆盖外部修改。

        ## 表格

        | 操作 | 快捷键 | 说明 |
        | :--- | :--- | :--- |
        | 切换视图 | `Ctrl+1 / 2 / 3` | 阅读 / 分栏 / 源码 |
        | 查找 | `Ctrl+F` | 源码与阅读视图都可用 |
        | 字号 | `Ctrl+= / - / 0` | 12–30 px |

        ```csharp
        // 代码块使用等宽字体
        Console.WriteLine("Hello, Markdown");
        ```
        """;

    private double _fontSize;
    private string? _currentFile;
    private string _viewMode = "Preview";
    private bool _isEditing;
    private bool _isDirty;
    private bool _welcome = true;
    private bool _welcomeGuideDismissed;
    private bool _previewCurrent;
    private string? _previewError;
    private Encoding _encoding = MarkdownFile.Utf8;
    private string? _newLine = "\n";
    private string? _loadedFingerprint;
    private string _savedText = "";
    private bool _isSaving;
    private bool _suppressNavPush;
    private bool _suppressPositionCapture;
    private ScrollViewer? _viewerScroller;

    // 侧边栏与响应式状态
    private bool _sidebarOpen = true;
    private string _sidebarTab = "Outline";
    private bool _isNarrowViewport;
    private bool _isMobileViewport;
    private bool? _manualSidebarOverrideInNarrow;
    private IReadOnlyList<OutlineItem> _outlineItems = Array.Empty<OutlineItem>();
    private string? _activeOutlineSlug;

    // 外部文件监控与内联横幅动作
    private FileSystemWatcher? _fileWatcher;
    private string? _watchedFilePath;
    private Action? _errorPrimaryAction;
    private Action? _errorSecondaryAction;

    // 专注模式、临时状态提示
    private bool _focusMode;
    private DateTime _statusHoldUntil = DateTime.MinValue;

    private sealed record NavEntry(string? FilePath, string? Anchor, int Line);
    private sealed record QuickItem(string Title, string Detail, string? Path, OutlineItem? Heading);

    private static readonly (string Keys, string Action)[] ShortcutList =
    {
        ("Ctrl+O", "打开文件"),
        ("Ctrl+P", "快速切换文档（输入 # 跳到标题）"),
        ("Ctrl+N", "新建"),
        ("Ctrl+S / Ctrl+Shift+S", "保存 / 另存为"),
        ("Ctrl+Shift+E", "导出为 PDF"),
        ("F5", "重新加载"),
        ("Ctrl+PgUp / PgDn", "同文件夹上一篇 / 下一篇"),
        ("Alt+← / →", "后退 / 前进"),
        ("Ctrl+1 / 2 / 3", "阅读 / 分栏 / 源码"),
        ("Ctrl+E", "阅读与源码互切"),
        ("Ctrl+Shift+B", "显示 / 隐藏侧栏"),
        ("F11", "专注模式"),
        ("Ctrl+Shift+L", "深色 / 浅色"),
        ("Ctrl+= / - / 0", "字号"),
        ("Ctrl+F", "查找"),
        ("Ctrl+B / I / K", "粗体 / 斜体 / 链接"),
        ("Ctrl+Shift+X", "删除线"),
        ("Ctrl+`", "行内代码"),
        ("Enter", "续写列表；空项回车结束列表"),
        ("Tab / Shift+Tab", "列表缩进 / 反缩进"),
        ("Ctrl+V", "粘贴截图存为图片；选中文字粘贴网址生成链接"),
        ("Alt+Z", "源码自动折行"),
        ("Ctrl+W", "关闭窗口"),
    };

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private string DisplayName => _currentFile == null ? "未命名" : Path.GetFileName(_currentFile);

    public MainWindow(AppSettings settings, string? path)
    {
        _settings = settings;
        _settings.Normalize();
        _fontSize = _settings.FontSize;
        _palette = _settings.Theme == "Dark" ? Palette.Dark : Palette.Light;
        _sidebarOpen = _settings.SidebarOpen;
        _sidebarTab = _settings.SidebarTab;
        _viewMode = _settings.ViewMode;

        _splitRenderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _splitRenderTimer.Tick += (_, _) =>
        {
            _splitRenderTimer.Stop();
            if (_viewMode == "Split" && !_previewCurrent)
            {
                RenderPreview();
                RefreshOutline();
                UpdateStatus();
                // 重新排版后预览会回到顶部，按源码滚动比例恢复位置
                Dispatcher.BeginInvoke(SyncPreviewToEditor, DispatcherPriority.Loaded);
            }
        };

        _watcherDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _watcherDebounceTimer.Tick += (_, _) =>
        {
            _watcherDebounceTimer.Stop();
            HandleExternalFileChange();
        };

        InitializeComponent();
        ApplyTheme();

        SidebarColumn.Width = new GridLength(_settings.SidebarWidth);
        Editor.FontSize = _fontSize;
        Editor.WordWrap = _settings.WordWrap;
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        _editorSearch = SearchPanel.Install(Editor);
        Editor.TextChanged += OnEditorTextChanged;
        Editor.TextArea.TextEntering += OnEditorTextEntering;
        Editor.TextArea.PreviewKeyDown += OnEditorPreviewKeyDown;
        Editor.TextArea.TextView.ScrollOffsetChanged += (_, _) => SyncPreviewToEditor();
        BuildShortcutsGrid();
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            if (_isEditing) UpdateStatus();
            RememberCurrentReadingPosition();
        };
        _ = ViewerScroller;

        var linkCommand = new RoutedCommand();
        CommandBindings.Add(new CommandBinding(linkCommand, OnOpenLink,
            (_, args) => args.CanExecute = args.Parameter is string { Length: > 0 }));
        _engine.HyperlinkCommand = linkCommand;

        Width = Math.Max(680, _settings.Width);
        Height = Math.Max(520, _settings.Height);
        if (_settings.Left is double left && _settings.Top is double top)
        {
            Left = left;
            Top = top;
            WindowStartupLocation = WindowStartupLocation.Manual;
            var screen = SystemParameters.WorkArea;
            if (Left > screen.Right - 100 || Top > screen.Bottom - 100
                || Left < screen.Left - Width + 100 || Top < screen.Top - Height + 100)
            {
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (_settings.Maximized) WindowState = WindowState.Maximized;

        ApplySidebarTab(_sidebarTab);
        UpdateWordWrapUI();
        UpdateAutoReloadUI();
        SetViewMode(_viewMode, renderImmediate: false);
        ApplyResponsiveLayout(Width);

        void OpenInitialFile(object sender, RoutedEventArgs args)
        {
            Loaded -= OpenInitialFile;
            var initial = path ?? (File.Exists(_settings.LastFile) ? _settings.LastFile : null);
            var hadExplicitFailure = false;
            if (initial != null)
            {
                if (LoadFile(initial)) return;
                hadExplicitFailure = true;
            }
            RenderPreview();
            RefreshOutline();
            RefreshFilesPanel();
            UpdateEmptyStateVisibility();
            UpdateFileLabel();
            if (!hadExplicitFailure) UpdateStatus();
        }
        Loaded += OpenInitialFile;
    }

    // ---------- 主题与排版样式系统 ----------

    /// <summary>正文版心宽度：约 46 个字号宽，16px 时约 736px，字号放大时同步放宽。</summary>
    private double ReadingMeasure => Math.Round(_fontSize * 46);

    private Thickness GetResponsivePagePadding()
    {
        double side, top, bottom;
        if (_isMobileViewport) { side = 18; top = 18; bottom = 32; }
        else if (_isNarrowViewport || _viewMode == "Split") { side = 32; top = 24; bottom = 56; }
        else { side = 56; top = 40; bottom = 80; }

        // 宽窗口下正文居中，左右留白均分，避免一行过长难读
        var width = PreviewHost.ActualWidth - SystemParameters.VerticalScrollBarWidth;
        if (width > 0) side = Math.Max(side, Math.Floor((width - ReadingMeasure) / 2));
        return new Thickness(side, top, side, bottom);
    }

    private void UpdateEditorPadding()
    {
        var basePad = _isMobileViewport ? 10 : (_isNarrowViewport || _viewMode == "Split") ? 14 : 24;
        double side = basePad;
        // 纯源码视图同样居中，行宽约 52 个字号宽
        if (_viewMode == "Edit" && EditorHost.ActualWidth > 0)
            side = Math.Max(basePad, Math.Floor((EditorHost.ActualWidth - _fontSize * 52) / 2));
        Editor.Padding = new Thickness(side, _isMobileViewport ? 10 : 18, side, 48);
    }

    private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged) return;
        if (ReferenceEquals(sender, PreviewHost) && Viewer.Document is FlowDocument doc)
            doc.PagePadding = GetResponsivePagePadding();
        else if (ReferenceEquals(sender, EditorHost))
            UpdateEditorPadding();
    }

    private Style BuildStyle(Palette? paletteOverride = null, Thickness? paddingOverride = null)
    {
        var pal = paletteOverride ?? _palette;
        var style = new Style(typeof(FlowDocument), MarkdownStyle.GithubLike);
        style.Setters.Add(new Setter(FlowDocument.FontFamilyProperty,
            (FontFamily)Resources["UiFontFamily"]));
        style.Setters.Add(new Setter(FlowDocument.FontSizeProperty, _fontSize));
        style.Setters.Add(new Setter(FlowDocument.PagePaddingProperty, paddingOverride ?? GetResponsivePagePadding()));
        style.Setters.Add(new Setter(FlowDocument.ForegroundProperty, Brush(pal.Foreground)));
        style.Setters.Add(new Setter(FlowDocument.BackgroundProperty, Brush(pal.Background)));
        return style;
    }

    private void ApplyTheme()
    {
        _palette = _settings.Theme == "Dark" ? Palette.Dark : Palette.Light;
        Resources["BgBrush"] = Brush(_palette.Background);
        Resources["SidebarBg"] = Brush(_palette.Sidebar);
        Resources["ToolbarBg"] = Brush(_palette.Toolbar);
        Resources["SubtleBg"] = Brush(_palette.Subtle);
        Resources["HoverBrush"] = Brush(_palette.Hover);
        Resources["ActiveBrush"] = Brush(_palette.Active);
        Resources["FgBrush"] = Brush(_palette.Foreground);
        Resources["SecondaryFgBrush"] = Brush(_palette.SecondaryForeground);
        Resources["MutedBrush"] = Brush(_palette.Muted);
        Resources["BorderBrush"] = Brush(_palette.Border);
        Resources["AccentBrush"] = Brush(_palette.Accent);
        Resources["WarningBrush"] = Brush(_palette.Warning);
        Resources["WarningBgBrush"] = Brush(_palette.WarningBg);
        Resources["DangerBrush"] = Brush(_palette.Danger);
        Resources["DangerBgBrush"] = Brush(_palette.DangerBg);

        Background = Brush(_palette.Background);
        Foreground = Brush(_palette.Foreground);
        Editor.Foreground = Brush(_palette.Foreground);
        Editor.TextArea.Foreground = Brush(_palette.Foreground);
        Editor.LineNumbersForeground = Brush(_palette.LineNumber);
        Editor.TextArea.SelectionBrush = Brush(_palette.Selection);
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.SelectionBorder = null;
        Editor.TextArea.TextView.LinkTextForegroundBrush = Brush(_palette.Accent);
        Viewer.SelectionBrush = Brush(_palette.Accent);
        // 深色时显示太阳（切到浅色），浅色时显示月亮
        ThemeToggleButton.Content = _settings.Theme == "Dark" ? "" : "";
        ApplyTitleBarTheme();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) => ApplyTitleBarTheme();

    /// <summary>Windows 10 2004+/11：让系统标题栏跟随深浅色，失败时静默忽略。</summary>
    private void ApplyTitleBarTheme()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        try
        {
            var dark = _settings.Theme == "Dark" ? 1 : 0;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch
        {
            // 旧系统不支持该属性
        }
    }

    private void ToggleTheme()
    {
        _settings.Theme = _settings.Theme == "Dark" ? "Light" : "Dark";
        ApplyTheme();
        _previewCurrent = false;
        if (_viewMode != "Edit") RenderPreview();
        RefreshOutline();
        RefreshFilesPanel();
        UpdateStatus();
    }

    private void OnToggleThemeClick(object sender, RoutedEventArgs e) => ToggleTheme();

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ---------- 文件加载与外部监控 ----------

    private bool LoadFile(string path) => LoadFileCore(path, isAutoReload: false);

    private bool LoadFileCore(string path, bool isAutoReload, bool restoreReadingPosition = true)
    {
        try
        {
            HideErrorBanner();
            path = Path.GetFullPath(path);
            var sameDocument = string.Equals(_currentFile, path, StringComparison.OrdinalIgnoreCase);
            var previousOffset = sameDocument && isAutoReload ? ViewerScroller?.VerticalOffset ?? 0 : 0;
            var previousLine = sameDocument && isAutoReload ? Editor.TextArea.Caret.Line : 1;
            var previousAnchor = sameDocument && isAutoReload ? _activeOutlineSlug : null;
            if (!sameDocument) RememberCurrentReadingPosition();

            var info = new FileInfo(path);
            var showSkeleton = info.Exists && info.Length > 256 * 1024;
            if (showSkeleton) SetLoadingState(true, $"正在读取 {Path.GetFileName(path)}...");

            var file = MarkdownFile.Read(path);
            if (!isAutoReload && _currentFile != null && !sameDocument)
                PushNavHistory();

            SetDocument(path, file);
            _settings.AddRecentFile(path);
            SetupFileWatcher(path);
            RefreshFilesPanel();
            if (showSkeleton) SetLoadingState(false);

            if (sameDocument && isAutoReload)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (string.Equals(_currentFile, path, StringComparison.OrdinalIgnoreCase))
                        RestoreReadingPosition(previousOffset, previousLine, previousAnchor);
                }, DispatcherPriority.Loaded);
            }
            else if (restoreReadingPosition)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (string.Equals(_currentFile, path, StringComparison.OrdinalIgnoreCase))
                        RestoreSavedReadingPosition(path);
                }, DispatcherPriority.Loaded);
            }

            if (isAutoReload)
                Notify($"已同步磁盘最新内容：{DisplayName}");
            return true;
        }
        catch (Exception ex)
        {
            SetLoadingState(false);
            var message = $"打开失败：{path} — {ex.Message}";
            Notify(message);
            ShowErrorBanner(
                badge: "读取错误",
                message: message,
                isWarning: false,
                primaryLabel: "重试",
                primaryAction: () => LoadFile(path),
                secondaryLabel: null,
                secondaryAction: null);
            RefreshFilesPanel();
            return false;
        }
    }

    private void SetDocument(string? path, MarkdownFile? file)
    {
        _suppressPositionCapture = true;
        try
        {
            var text = file?.Text ?? "";
            var document = new TextDocument(text);
            _currentFile = path;
            _welcome = false;
            _welcomeGuideDismissed = true;
            _encoding = file?.Encoding ?? MarkdownFile.Utf8;
            _newLine = file != null ? file.NewLine : "\n";
            _loadedFingerprint = file?.Fingerprint;
            _previewCurrent = false;
            _activeOutlineSlug = null;
            Viewer.Document = null;
            _headings.Clear();
            _editorSearch.Close();
            _savedText = text;
            _isDirty = false;
            Editor.Document = document;
            Editor.ScrollToHome();

            if (_viewMode == "Edit")
                Editor.TextArea.Focus();
            else
                RenderPreview();

            RefreshOutline();
            UpdateEmptyStateVisibility();
            UpdateFileLabel();
            UpdateTitle();
            UpdateStatus();
        }
        finally
        {
            _suppressPositionCapture = false;
        }
    }

    private void SetupFileWatcher(string? path)
    {
        _watcherDebounceTimer.Stop();
        if (_fileWatcher != null)
        {
            _fileWatcher.EnableRaisingEvents = false;
            _fileWatcher.Dispose();
            _fileWatcher = null;
        }
        _watchedFilePath = null;

        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(full);
            var name = Path.GetFileName(full);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name) || !Directory.Exists(dir)) return;

            _watchedFilePath = full;
            _fileWatcher = new FileSystemWatcher(dir, name)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size
            };
            _fileWatcher.Changed += OnFileWatcherTriggered;
            _fileWatcher.Created += OnFileWatcherTriggered;
            _fileWatcher.Renamed += OnFileWatcherTriggered;
            _fileWatcher.Deleted += OnFileWatcherTriggered;
            _fileWatcher.EnableRaisingEvents = true;
        }
        catch
        {
            // 网络路径或受限目录不支持 FileSystemWatcher 时静默降级
        }
    }

    private void OnFileWatcherTriggered(object sender, FileSystemEventArgs e)
    {
        if (_isSaving) return;
        Dispatcher.BeginInvoke(() =>
        {
            _watcherDebounceTimer.Stop();
            _watcherDebounceTimer.Start();
        });
    }

    private void HandleExternalFileChange()
    {
        if (_isSaving || _currentFile == null || !string.Equals(_currentFile, _watchedFilePath, StringComparison.OrdinalIgnoreCase))
            return;

        var diskFingerprint = MarkdownFile.ReadFingerprint(_currentFile);
        if (diskFingerprint == _loadedFingerprint) return;

        if (diskFingerprint == null)
        {
            ShowErrorBanner(
                badge: "外部变更",
                message: $"磁盘上的原文件已被移动或删除：{_currentFile}",
                isWarning: true,
                primaryLabel: "按原路径保存",
                primaryAction: () => SaveFile(false),
                secondaryLabel: "另存为",
                secondaryAction: () => SaveFile(true));
            return;
        }

        if (!_isDirty && _settings.AutoReload)
        {
            LoadFileCore(_currentFile, isAutoReload: true);
            return;
        }

        ShowErrorBanner(
            badge: "外部修改冲突",
            message: $"“{DisplayName}”已在外部程序中被修改，当前窗口包含未保存编辑或已关闭自动同步。",
            isWarning: true,
            primaryLabel: "加载磁盘版本",
            primaryAction: () =>
            {
                HideErrorBanner();
                LoadFileCore(_currentFile, isAutoReload: true);
            },
            secondaryLabel: "另存当前版本",
            secondaryAction: () => SaveFile(true));
    }

    // ---------- 预览渲染与主题化文档块后处理 ----------

    private void RenderPreview()
    {
        if (_previewCurrent) return;

        _previewError = null;
        _headings.Clear();
        try
        {
            _engine.DocumentStyle = BuildStyle();
            _engine.AssetPathRoot = _currentFile != null ? Path.GetDirectoryName(_currentFile) : null;
            var sourceText = _welcome ? WelcomeText : Editor.Text;
            var doc = _engine.Transform(sourceText);
            doc.PagePadding = GetResponsivePagePadding();
            doc.Foreground = Brush(_palette.Foreground);
            doc.Background = Brush(_palette.Background);
            Typography.SetNumeralAlignment(doc, FontNumeralAlignment.Tabular);

            AdjustBlocks(doc.Blocks, 0, indexHeadings: true);
            Viewer.Document = doc;
            _previewCurrent = true;
        }
        catch (Exception ex)
        {
            Viewer.Document = null;
            _headings.Clear();
            _previewError = "渲染失败，按 Ctrl+E 查看源码：" + ex.Message;
            ShowErrorBanner(
                badge: "渲染错误",
                message: _previewError,
                isWarning: false,
                primaryLabel: "切换到源码",
                primaryAction: () => SetViewMode("Edit"),
                secondaryLabel: null,
                secondaryAction: null);
        }
    }

    private void AdjustBlocks(BlockCollection blocks, int depth, bool indexHeadings = false, Palette? paletteOverride = null)
    {
        var pal = paletteOverride ?? _palette;
        var fg = Brush(pal.Foreground);
        var secondaryFg = Brush(pal.SecondaryForeground);
        var subtleBg = Brush(pal.Subtle);
        var sidebarBg = Brush(pal.Sidebar);
        var border = Brush(pal.Border);
        var monoFont = (FontFamily)Resources["MonoFontFamily"];

        foreach (var block in blocks)
        {
            Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
            switch (block)
            {
                case Paragraph p:
                    var tag = p.Tag as string;
                    AdjustInlines(p.Inlines, pal);
                    if (tag != null && tag.StartsWith("Heading", StringComparison.Ordinal))
                    {
                        var level = tag.Length > 7 && char.IsDigit(tag[7]) ? tag[7] - '0' : 1;
                        var headingSize = level switch
                        {
                            1 => 30,
                            2 => 24,
                            3 => 19,
                            4 => 17,
                            _ => 16
                        };
                        p.FontSize = Math.Round(headingSize * _fontSize / 16);
                        p.FontWeight = level <= 2 ? FontWeights.Bold : FontWeights.SemiBold;
                        p.Foreground = fg;
                        p.LineHeight = p.FontSize * 1.3;
                        p.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                        p.Margin = new Thickness(0, level == 1 ? 12 : level == 2 ? 30 : 24, 0, level <= 2 ? 14 : 8);
                        if (p.BorderThickness.Bottom > 0)
                        {
                            p.BorderBrush = border;
                            p.Padding = new Thickness(0, 0, 0, 8);
                        }
                        if (indexHeadings) IndexHeading(p);
                    }
                    else if (tag == "CodeBlock")
                    {
                        // 代码块：浅底色区分，不加边框
                        p.Background = subtleBg;
                        p.Foreground = fg;
                        p.FontFamily = monoFont;
                        p.FontSize = Math.Max(12, Math.Round(_fontSize * 0.86));
                        p.LineHeight = p.FontSize * 1.55;
                        p.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                        p.Padding = new Thickness(18, 14, 18, 14);
                        p.Margin = new Thickness(0, 4, 0, 20);
                        p.BorderThickness = new Thickness(0);
                    }
                    else
                    {
                        if (p.Background != null && p.Background != Brushes.Transparent)
                            p.Background = subtleBg;
                        p.Foreground = depth > 0 && p.BorderThickness.Left > 0 ? secondaryFg : fg;
                        if (p.BorderThickness.Left > 0)
                            p.BorderBrush = border;
                        p.LineHeight = _fontSize * 1.75;
                        p.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                        p.Margin = depth > 0 ? new Thickness(0, 0, 0, 4) : new Thickness(0, 0, 0, 16);
                    }
                    break;

                case List list:
                    list.Margin = new Thickness(depth > 0 ? 20 : 24, 0, 0, depth > 0 ? 4 : 16);
                    // 无序列表：顶层实心点，嵌套层空心圈（有序列表保持数字）
                    if (list.MarkerStyle is TextMarkerStyle.Circle or TextMarkerStyle.Disc or TextMarkerStyle.Square or TextMarkerStyle.Box)
                        list.MarkerStyle = depth == 0 ? TextMarkerStyle.Disc : TextMarkerStyle.Circle;
                    list.Padding = new Thickness(0);
                    list.Foreground = fg;
                    foreach (var item in list.ListItems)
                    {
                        item.Margin = new Thickness(0, 2, 0, 2);
                        item.Foreground = fg;
                        AdjustBlocks(item.Blocks, depth + 1, indexHeadings, pal);
                    }
                    break;

                case Section section:
                    if (section.BorderThickness.Left > 0)
                    {
                        // 引用块：细竖线 + 次级文字色
                        section.BorderBrush = Brush(pal.QuoteBar);
                        section.BorderThickness = new Thickness(3, 0, 0, 0);
                        section.Padding = new Thickness(16, 2, 0, 2);
                        section.Margin = new Thickness(0, 0, 0, 16);
                        section.Foreground = secondaryFg;
                    }
                    if (section.Background != null && section.Background != Brushes.Transparent)
                        section.Background = sidebarBg;
                    AdjustBlocks(section.Blocks, depth + 1, indexHeadings, pal);
                    break;

                case Table table:
                    table.BorderBrush = border;
                    table.CellSpacing = 0;
                    Typography.SetNumeralAlignment(table, FontNumeralAlignment.Tabular);
                    foreach (var group in table.RowGroups)
                    {
                        var rowIdx = 0;
                        foreach (var row in group.Rows)
                        {
                            // 表头浅底，正文行不做斑马纹，仅靠细分隔线
                            row.Background = rowIdx == 0 ? sidebarBg : Brushes.Transparent;
                            if (rowIdx == 0) row.FontWeight = FontWeights.SemiBold;
                            rowIdx++;
                            foreach (var cell in row.Cells)
                            {
                                cell.BorderBrush = border;
                                cell.Padding = new Thickness(12, 7, 12, 7);
                                AdjustBlocks(cell.Blocks, depth + 1, indexHeadings, pal);
                            }
                        }
                    }
                    break;

                case BlockUIContainer container:
                    if (container.Child is Separator sep)
                    {
                        sep.Background = border;
                        sep.Margin = new Thickness(0, 20, 0, 20);
                    }
                    break;
            }
        }
    }

    private void AdjustInlines(InlineCollection inlines, Palette? paletteOverride = null)
    {
        var pal = paletteOverride ?? _palette;
        var accent = Brush(pal.Accent);
        var subtle = Brush(pal.Subtle);
        var fg = Brush(pal.Foreground);
        var mono = (FontFamily)Resources["MonoFontFamily"];

        foreach (var inline in inlines)
        {
            if (inline is Hyperlink link)
            {
                link.Foreground = accent;
                AdjustInlines(link.Inlines, pal);
            }
            else if (inline is Span span)
            {
                if (span.Background != null && span.Background != Brushes.Transparent)
                {
                    span.Background = subtle;
                    span.Foreground = fg;
                    span.FontFamily = mono;
                }
                AdjustInlines(span.Inlines, pal);
            }
            else if (inline is Run run && run.Background != null && run.Background != Brushes.Transparent)
            {
                run.Background = subtle;
                run.Foreground = fg;
                run.FontFamily = mono;
            }
        }
    }

    private void IndexHeading(Paragraph paragraph)
    {
        var title = new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim();
        var slug = MarkdownFile.BuildSlug(title);
        if (slug.Length == 0) return;

        var anchor = slug;
        for (var suffix = 1; !_headings.TryAdd(anchor, paragraph); suffix++)
            anchor = $"{slug}-{suffix}";
    }

    // ---------- 阅读位置记忆、链接跳转与历史前进/后退 ----------

    private ScrollViewer? ViewerScroller
    {
        get
        {
            if (_viewerScroller != null) return _viewerScroller;
            Viewer.ApplyTemplate();
            _viewerScroller = Viewer.Template.FindName("PART_ContentHost", Viewer) as ScrollViewer;
            if (_viewerScroller != null)
                _viewerScroller.ScrollChanged += OnViewerScrollChanged;
            return _viewerScroller;
        }
    }

    private void OnViewerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        RememberCurrentReadingPosition();
        UpdateReadingProgress();
    }

    private void RememberCurrentReadingPosition()
    {
        if (_suppressPositionCapture || _currentFile == null || Editor.Document == null) return;
        _settings.RememberReadingPosition(
            _currentFile,
            ViewerScroller?.VerticalOffset ?? 0,
            Editor.TextArea.Caret.Line,
            _activeOutlineSlug);
    }

    private void RestoreSavedReadingPosition(string path)
    {
        var position = _settings.GetReadingPosition(path);
        if (position == null)
        {
            UpdateReadingProgress();
            return;
        }
        RestoreReadingPosition(position.ScrollOffset, position.Line, position.Anchor);
    }

    private void RestoreReadingPosition(double scrollOffset, int line, string? anchor)
    {
        _suppressPositionCapture = true;
        try
        {
            if (_viewMode != "Edit" && Viewer.Document != null)
                ViewerScroller?.ScrollToVerticalOffset(scrollOffset);

            if (Editor.Document != null && Editor.Document.LineCount > 0)
            {
                var targetLine = Math.Clamp(line, 1, Editor.Document.LineCount);
                var docLine = Editor.Document.GetLineByNumber(targetLine);
                Editor.CaretOffset = docLine.Offset;
                ScrollEditorToLine(targetLine);
            }

            _activeOutlineSlug = !string.IsNullOrEmpty(anchor) && _headings.ContainsKey(anchor)
                ? anchor
                : null;
        }
        finally
        {
            _suppressPositionCapture = false;
        }
        RefreshOutline();
        UpdateReadingProgress();
    }

    /// <summary>AvalonEdit 的 ScrollToLine(1) 会留下几像素偏移，导致首行被裁切；首行直接回到顶部。</summary>
    private void ScrollEditorToLine(int line)
    {
        if (line <= 1) Editor.ScrollToVerticalOffset(0);
        else Editor.ScrollToLine(line);
    }

    private int GetReadingProgress()
    {
        if (Viewer.Document == null || ViewerScroller is not { } scroller) return 0;
        var scrollable = scroller.ExtentHeight - scroller.ViewportHeight;
        if (!double.IsFinite(scrollable) || scrollable <= 1) return 100;
        return (int)Math.Clamp(Math.Round(scroller.VerticalOffset / scrollable * 100), 0, 100);
    }

    private void UpdateReadingProgress()
    {
        if (_isEditing) return;
        CaretMetricText.Text = $"{GetReadingProgress()}%";
    }

    private void PushNavHistory()
    {
        if (_suppressNavPush) return;
        var currentLine = Editor.Document != null ? Editor.TextArea.Caret.Line : 1;
        _backStack.Push(new NavEntry(_currentFile, _activeOutlineSlug, currentLine));
        _forwardStack.Clear();
        UpdateNavButtons();
    }

    private void UpdateNavButtons()
    {
        NavBackButton.IsEnabled = _backStack.Count > 0;
        NavForwardButton.IsEnabled = _forwardStack.Count > 0;
    }

    private void OnNavBackClick(object sender, RoutedEventArgs e) => NavigateHistory(back: true);
    private void OnNavForwardClick(object sender, RoutedEventArgs e) => NavigateHistory(back: false);

    private void NavigateHistory(bool back)
    {
        var source = back ? _backStack : _forwardStack;
        var targetStack = back ? _forwardStack : _backStack;
        if (source.Count == 0) return;

        RememberCurrentReadingPosition();
        var currentLine = Editor.Document != null ? Editor.TextArea.Caret.Line : 1;
        var currentEntry = new NavEntry(_currentFile, _activeOutlineSlug, currentLine);
        var entry = source.Peek();

        _suppressNavPush = true;
        try
        {
            if (entry.FilePath != null && !string.Equals(entry.FilePath, _currentFile, StringComparison.OrdinalIgnoreCase))
            {
                if (!ConfirmDiscard() || !LoadFileCore(entry.FilePath, isAutoReload: true, restoreReadingPosition: false))
                    return;
            }

            source.Pop();
            targetStack.Push(currentEntry);
            UpdateNavButtons();

            if (!string.IsNullOrEmpty(entry.Anchor) && _headings.TryGetValue(entry.Anchor, out var heading))
            {
                _activeOutlineSlug = entry.Anchor;
                heading.BringIntoView();
            }
            if (Editor.Document != null && entry.Line >= 1 && entry.Line <= Editor.Document.LineCount)
            {
                var docLine = Editor.Document.GetLineByNumber(entry.Line);
                Editor.CaretOffset = docLine.Offset;
                ScrollEditorToLine(entry.Line);
            }
            RefreshOutline();
            UpdateReadingProgress();
        }
        finally
        {
            _suppressNavPush = false;
        }
    }

    private void OnOpenLink(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not string link) return;
        try
        {
            HideErrorBanner();
            var absolute = Uri.TryCreate(link, UriKind.Absolute, out var uri);
            if (absolute && uri!.Scheme is "http" or "https")
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return;
            }

            var parts = link.Split('#', 2);
            var openedDocument = false;
            if (parts[0].Length > 0)
            {
                var directory = Path.GetDirectoryName(_currentFile);
                if (absolute && !uri!.IsFile || directory == null)
                    throw new InvalidDataException("此链接无法在阅读器中打开。");
                var path = absolute ? uri!.LocalPath : Path.GetFullPath(Uri.UnescapeDataString(parts[0]), directory);
                if (!MarkdownFile.IsMarkdownExtension(path))
                    throw new InvalidDataException("本地链接需要指向 Markdown 文件。");
                if (!string.Equals(path, _currentFile, StringComparison.OrdinalIgnoreCase))
                {
                    if (!ConfirmDiscard()) return;
                    if (!LoadFile(path)) return;
                    openedDocument = true;
                }
            }

            if (parts.Length == 2 && parts[1].Length > 0)
            {
                var anchor = Uri.UnescapeDataString(parts[1]);
                if (!_headings.TryGetValue(anchor, out var heading))
                    throw new InvalidDataException($"找不到标题锚点 #{anchor}。");
                if (!openedDocument) PushNavHistory();
                _activeOutlineSlug = anchor;
                heading.BringIntoView();
                RefreshOutline();
            }
        }
        catch (Exception ex)
        {
            var msg = "打开链接失败：" + ex.Message;
            Notify(msg);
            ShowErrorBanner("链接跳转失败", msg, isWarning: false, null, null, null, null);
        }
    }

    // ---------- 侧边栏工作台（大纲 + 文档列表 + 筛选） ----------

    private void OnToggleSidebarClick(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar()
    {
        if (_focusMode)
        {
            // 专注模式下按侧栏键：退出专注并显示侧栏
            SetFocusMode(false);
            if (SidebarPanel.Visibility == Visibility.Visible) return;
        }
        if (_isNarrowViewport)
        {
            var currentlyVisible = SidebarPanel.Visibility == Visibility.Visible;
            _manualSidebarOverrideInNarrow = !currentlyVisible;
            ApplySidebarVisibility(!currentlyVisible);
        }
        else
        {
            _sidebarOpen = !_sidebarOpen;
            _settings.SidebarOpen = _sidebarOpen;
            ApplySidebarVisibility(_sidebarOpen);
        }
    }

    private void ApplySidebarVisibility(bool visible)
    {
        if (_focusMode) visible = false;
        if (visible)
        {
            SidebarPanel.Visibility = Visibility.Visible;
            SidebarSplitter.Visibility = Visibility.Visible;
            var width = Math.Clamp(_settings.SidebarWidth, 180, 420);
            SidebarColumn.MinWidth = 180;
            SidebarColumn.MaxWidth = 420;
            SidebarColumn.Width = new GridLength(width);
            SidebarSplitterColumn.Width = new GridLength(1);
            SidebarToggleButton.Tag = "Active";
        }
        else
        {
            if (SidebarColumn.ActualWidth >= 180)
                _settings.SidebarWidth = SidebarColumn.ActualWidth;
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarSplitter.Visibility = Visibility.Collapsed;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.MaxWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            SidebarSplitterColumn.Width = new GridLength(0);
            SidebarToggleButton.Tag = null;
        }
    }

    private void OnTabOutlineClick(object sender, RoutedEventArgs e) => ApplySidebarTab("Outline");
    private void OnTabFilesClick(object sender, RoutedEventArgs e) => ApplySidebarTab("Files");

    private void ApplySidebarTab(string tab)
    {
        _sidebarTab = tab == "Files" ? "Files" : "Outline";
        _settings.SidebarTab = _sidebarTab;
        var isOutline = _sidebarTab == "Outline";
        OutlineTabPanel.Visibility = isOutline ? Visibility.Visible : Visibility.Collapsed;
        FilesTabPanel.Visibility = isOutline ? Visibility.Collapsed : Visibility.Visible;
        TabOutlineButton.Tag = isOutline ? "Active" : null;
        TabFilesButton.Tag = isOutline ? null : "Active";
    }

    private void OnSidebarFilterChanged(object sender, TextChangedEventArgs e)
    {
        var hasFilter = !string.IsNullOrWhiteSpace(SidebarFilterBox.Text);
        SidebarFilterPlaceholder.Visibility = hasFilter ? Visibility.Collapsed : Visibility.Visible;
        ClearFilterButton.Visibility = hasFilter ? Visibility.Visible : Visibility.Collapsed;
        RefreshOutline();
        RefreshFilesPanel();
    }

    private void OnClearSidebarFilterClick(object sender, RoutedEventArgs e)
    {
        SidebarFilterBox.Text = "";
        SidebarFilterBox.Focus();
    }

    private void RefreshOutline()
    {
        // 空状态页显示时还没有正文，目录留空
        var showingEmptyState = _welcome && !_welcomeGuideDismissed;
        var source = showingEmptyState ? "" : _welcome ? WelcomeText : Editor.Text;
        _outlineItems = MarkdownFile.ExtractOutline(source);
        TabOutlineButton.Content = _outlineItems.Count > 0 ? $"大纲 {_outlineItems.Count}" : "大纲";

        var filter = SidebarFilterBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(filter)
            ? _outlineItems
            : _outlineItems.Where(item =>
                item.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                item.LevelBadge.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        OutlineListPanel.Children.Clear();
        if (filtered.Count == 0)
        {
            OutlineEmptyState.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(filter) && _outlineItems.Count > 0)
            {
                OutlineEmptyTitle.Text = "没有匹配的标题";
                OutlineEmptyDesc.Text = $"没有包含“{filter}”的标题。";
            }
            else if (showingEmptyState)
            {
                OutlineEmptyTitle.Text = "尚未打开文档";
                OutlineEmptyDesc.Text = "打开文档后，这里会显示目录。";
            }
            else
            {
                OutlineEmptyTitle.Text = "没有标题";
                OutlineEmptyDesc.Text = "用 # 到 #### 写标题，这里会生成目录。";
            }
            return;
        }

        OutlineEmptyState.Visibility = Visibility.Collapsed;
        var minLevel = filtered.Min(i => i.Level);

        foreach (var item in filtered)
        {
            var isActive = string.Equals(item.Slug, _activeOutlineSlug, StringComparison.OrdinalIgnoreCase);
            var btn = new Button
            {
                Style = (Style)Resources["SidebarItemButton"],
                Tag = isActive ? "Active" : null,
                ToolTip = $"{item.Title}（第 {item.LineNumber} 行）"
            };

            // 按层级缩进；顶层标题加粗，其余用次级色，当前章节用主色
            var titleBlock = new TextBlock
            {
                Text = item.Title,
                Margin = new Thickness((item.Level - minLevel) * 14, 0, 0, 0),
                FontSize = item.Level == minLevel ? 13 : 12.5,
                FontWeight = item.Level == minLevel || isActive ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = Brush(item.Level == minLevel || isActive ? _palette.Foreground : _palette.SecondaryForeground),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center
            };

            btn.Content = titleBlock;
            var captured = item;
            btn.Click += (_, _) => NavigateToOutlineItem(captured);
            OutlineListPanel.Children.Add(btn);
        }
    }

    private void NavigateToOutlineItem(OutlineItem item)
    {
        PushNavHistory();
        _activeOutlineSlug = item.Slug;

        if (_viewMode != "Edit")
        {
            RenderPreview();
            if (_headings.TryGetValue(item.Slug, out var paragraph))
                paragraph.BringIntoView();
        }

        if (Editor.Document != null && item.LineNumber >= 1 && item.LineNumber <= Editor.Document.LineCount)
        {
            var docLine = Editor.Document.GetLineByNumber(item.LineNumber);
            Editor.CaretOffset = docLine.Offset;
            ScrollEditorToLine(item.LineNumber);
            if (_viewMode == "Edit") Editor.TextArea.Focus();
        }

        RefreshOutline();
        Notify($"{item.Title} · 第 {item.LineNumber} 行");
    }

    private void RefreshFilesPanel()
    {
        var filter = SidebarFilterBox.Text.Trim();
        var siblings = MarkdownFile.ListSiblingMarkdownFiles(_currentFile);
        var filteredSiblings = string.IsNullOrEmpty(filter)
            ? siblings
            : siblings.Where(s => s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        // “最近”里不重复列出已在“此文件夹”中的文档
        var siblingPaths = new HashSet<string>(siblings.Select(s => s.FullPath), StringComparer.OrdinalIgnoreCase);
        var recentPaths = _settings.RecentFiles.Where(p => !siblingPaths.Contains(p)).ToList();
        var filteredRecent = string.IsNullOrEmpty(filter)
            ? recentPaths
            : recentPaths.Where(p => p.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        SiblingCountBadge.Text = $"{filteredSiblings.Count}";
        TabFilesButton.Content = "文档";

        SiblingFilesPanel.Children.Clear();
        RecentFilesPanel.Children.Clear();

        foreach (var sib in filteredSiblings)
        {
            SiblingFilesPanel.Children.Add(BuildFileRow(
                sib.FullPath, showDirectory: false, isCurrent: sib.IsCurrent, exists: true,
                tooltip: $"{sib.FullPath}\n{sib.SizeLabel} · {sib.LastWriteTime:yyyy-MM-dd HH:mm}"));
        }

        foreach (var path in filteredRecent)
        {
            var exists = File.Exists(path);
            RecentFilesPanel.Children.Add(BuildFileRow(
                path, showDirectory: true,
                isCurrent: string.Equals(path, _currentFile, StringComparison.OrdinalIgnoreCase),
                exists: exists, tooltip: exists ? path : $"{path}\n文件已不存在"));
        }

        var totalShown = filteredSiblings.Count + filteredRecent.Count;
        FilesEmptyState.Visibility = totalShown == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (totalShown == 0)
        {
            if (!string.IsNullOrEmpty(filter))
            {
                FilesEmptyTitle.Text = "没有匹配的文档";
                FilesEmptyDesc.Text = $"没有文件名包含“{filter}”的文档。";
            }
            else
            {
                FilesEmptyTitle.Text = "暂无文档";
                FilesEmptyDesc.Text = "打开一个本地 Markdown 文件后，这里会列出同文件夹的文档和最近记录。";
            }
        }

        PopulateEmptyStateRecentTable();
    }

    /// <summary>侧栏 / 空状态共用的文件行：文件名，可选第二行显示所在目录。</summary>
    private Button BuildFileRow(string path, bool showDirectory, bool isCurrent, bool exists, string tooltip)
    {
        var btn = new Button
        {
            Style = (Style)Resources["SidebarItemButton"],
            Tag = isCurrent ? "Active" : null,
            ToolTip = tooltip
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = Path.GetFileName(path),
            FontSize = 12.5,
            FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Brush(!exists ? _palette.Muted : isCurrent ? _palette.Foreground : _palette.SecondaryForeground),
            TextDecorations = exists ? null : TextDecorations.Strikethrough,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap
        });
        if (showDirectory)
        {
            stack.Children.Add(new TextBlock
            {
                Text = ShortenDirectory(Path.GetDirectoryName(path) ?? ""),
                FontSize = 11,
                Margin = new Thickness(0, 1, 0, 0),
                Foreground = Brush(_palette.Muted),
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap
            });
        }
        btn.Content = stack;

        btn.Click += (_, _) =>
        {
            if (string.Equals(path, _currentFile, StringComparison.OrdinalIgnoreCase)) return;
            if (ConfirmDiscard()) LoadFile(path);
        };
        return btn;
    }

    /// <summary>把用户目录缩写为 ~，让路径更短。</summary>
    private static string ShortenDirectory(string directory)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home) && directory.StartsWith(home, StringComparison.OrdinalIgnoreCase))
            return "~" + directory[home.Length..];
        return directory;
    }

    private void PopulateEmptyStateRecentTable()
    {
        EmptyStateRecentTable.Children.Clear();
        var recent = _settings.RecentFiles;
        EmptyStateRecentCountText.Text = recent.Count > 0 ? $"{recent.Count}" : "";

        if (recent.Count == 0)
        {
            EmptyStateRecentTable.Children.Add(new TextBlock
            {
                Text = "还没有打开过文档。",
                FontSize = 12.5,
                Margin = new Thickness(10, 6, 10, 6),
                Foreground = Brush(_palette.Muted)
            });
            return;
        }

        foreach (var path in recent.Take(8))
        {
            var exists = File.Exists(path);
            EmptyStateRecentTable.Children.Add(BuildFileRow(path, showDirectory: true, isCurrent: false,
                exists: exists, tooltip: exists ? path : $"{path}\n文件已不存在"));
        }
    }

    private void OnPruneRecentClick(object sender, RoutedEventArgs e)
    {
        var removed = _settings.PruneMissingRecentFiles();
        RefreshFilesPanel();
        Notify(removed > 0 ? $"已清理 {removed} 条失效的最近文件记录" : "所有最近文件记录均有效");
    }

    // ---------- 5 种工程状态：Loading 骨架屏 / Empty 引导态 / Error 横幅态 ----------

    public void SetLoadingState(bool isLoading, string? statusMessage = null)
    {
        if ( !string.IsNullOrWhiteSpace(statusMessage))
            LoadingStatusText.Text = statusMessage;
        LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateEmptyStateVisibility()
    {
        EmptyStatePanel.Visibility = (_welcome && !_welcomeGuideDismissed && _viewMode == "Preview")
            ? Visibility.Visible
            : Visibility.Collapsed;
        // 空状态下不显示示例文档的字数、编码等指标
        StatusMetricsPanel.Visibility = EmptyStatePanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void OnDismissEmptyGuideClick(object sender, RoutedEventArgs e)
    {
        _welcomeGuideDismissed = true;
        UpdateEmptyStateVisibility();
        RenderPreview();
        RefreshOutline();
    }

    private void OnNewFromClipboardClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        RememberCurrentReadingPosition();
        var clip = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        SetViewMode("Split", renderImmediate: false);
        SetDocument(null, new MarkdownFile(clip, MarkdownFile.Utf8, "\n", ""));
        if (clip.Length > 0)
        {
            _isDirty = true;
            UpdateTitle();
            UpdateFileLabel();
            UpdateStatus();
        }
    }

    public void ShowErrorBanner(
        string badge,
        string message,
        bool isWarning,
        string? primaryLabel,
        Action? primaryAction,
        string? secondaryLabel,
        Action? secondaryAction)
    {
        ErrorBannerBadge.Text = badge;
        ErrorBannerBadge.Foreground = Brush(isWarning ? _palette.Warning : _palette.Danger);
        ErrorBanner.Background = Brush(isWarning ? _palette.WarningBg : _palette.DangerBg);
        ErrorBannerText.Text = message.Replace("\r", " ").Replace("\n", " ");
        ErrorBannerText.ToolTip = message;

        _errorPrimaryAction = primaryAction;
        if (!string.IsNullOrEmpty(primaryLabel) && primaryAction != null)
        {
            ErrorPrimaryButton.Content = primaryLabel;
            ErrorPrimaryButton.Visibility = Visibility.Visible;
        }
        else
        {
            ErrorPrimaryButton.Visibility = Visibility.Collapsed;
        }

        _errorSecondaryAction = secondaryAction;
        if (!string.IsNullOrEmpty(secondaryLabel) && secondaryAction != null)
        {
            ErrorSecondaryButton.Content = secondaryLabel;
            ErrorSecondaryButton.Visibility = Visibility.Visible;
        }
        else
        {
            ErrorSecondaryButton.Visibility = Visibility.Collapsed;
        }

        ErrorBanner.Visibility = Visibility.Visible;
    }

    public void HideErrorBanner()
    {
        ErrorBanner.Visibility = Visibility.Collapsed;
        _errorPrimaryAction = null;
        _errorSecondaryAction = null;
    }

    private void OnErrorPrimaryClick(object sender, RoutedEventArgs e)
    {
        var action = _errorPrimaryAction;
        HideErrorBanner();
        action?.Invoke();
    }

    private void OnErrorSecondaryClick(object sender, RoutedEventArgs e)
    {
        var action = _errorSecondaryAction;
        HideErrorBanner();
        action?.Invoke();
    }

    private void OnErrorDismissClick(object sender, RoutedEventArgs e) => HideErrorBanner();

    // ---------- 文件命令（新建、打开、保存、另存为、重载、路径便捷操作） ----------

    private void OnNewClick(object sender, RoutedEventArgs e) => NewFile();

    private void NewFile()
    {
        if (!ConfirmDiscard()) return;

        RememberCurrentReadingPosition();
        HideErrorBanner();
        SetupFileWatcher(null);
        if (!_isEditing) SetEditing(true);
        SetDocument(null, null);
    }

    private void OnOpenClick(object sender, RoutedEventArgs e) => OpenDialog();

    private void OpenDialog()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Markdown 文件|*.md;*.markdown;*.mdown;*.mkd;*.mmd|文本文件|*.txt|所有文件|*.*",
            Title = "打开 Markdown 文件",
            InitialDirectory = Path.GetDirectoryName(_currentFile)
        };
        if (dlg.ShowDialog(this) == true && ConfirmDiscard()) LoadFile(dlg.FileName);
    }

    private void OnSaveClick(object sender, RoutedEventArgs e) => SaveFile(false);
    private void OnSaveAsClick(object sender, RoutedEventArgs e) => SaveFile(true);
    private void OnExportPdfClick(object sender, RoutedEventArgs e) => ExportPdfDialog();

    private void ExportPdfDialog()
    {
        var baseName = _currentFile != null
            ? Path.GetFileNameWithoutExtension(_currentFile)
            : "未命名";
        var dlg = new SaveFileDialog
        {
            Filter = "PDF 文档|*.pdf",
            FileName = baseName + ".pdf",
            Title = "导出为 PDF",
            AddExtension = true,
            DefaultExt = ".pdf",
            InitialDirectory = Path.GetDirectoryName(_currentFile)
        };
        if (dlg.ShowDialog(this) != true) return;
        ExportToPdf(Path.GetFullPath(dlg.FileName));
    }

    public bool ExportToPdf(string pdfPath)
    {
        try
        {
            HideErrorBanner();
            var printPalette = Palette.Light;
            var printPadding = new Thickness(56, 52, 56, 56);
            var exportEngine = new Markdown
            {
                DocumentStyle = BuildStyle(printPalette, printPadding),
                AssetPathRoot = _currentFile != null ? Path.GetDirectoryName(_currentFile) : null
            };
            var sourceText = _welcome ? WelcomeText : Editor.Text;
            var doc = exportEngine.Transform(sourceText);
            doc.PagePadding = printPadding;
            doc.Foreground = Brush(printPalette.Foreground);
            doc.Background = Brush(printPalette.Background);
            Typography.SetNumeralAlignment(doc, FontNumeralAlignment.Tabular);
            AdjustBlocks(doc.Blocks, 0, indexHeadings: false, paletteOverride: printPalette);

            PdfExporter.ExportFlowDocument(doc, pdfPath, DisplayName);
            Notify($"已导出 PDF：{Path.GetFileName(pdfPath)}");
            return true;
        }
        catch (Exception ex)
        {
            var msg = "导出 PDF 失败：" + ex.Message;
            Notify(msg);
            ShowErrorBanner("导出失败", msg, isWarning: false, "重试", () => ExportToPdf(pdfPath), null, null);
            return false;
        }
    }

    private bool SaveFile(bool saveAs)
    {
        try
        {
            var path = _currentFile;

            if (saveAs || path == null)
            {
                var dlg = new SaveFileDialog
                {
                    Filter = "Markdown 文件|*.md;*.markdown;*.mdown;*.mkd;*.mmd|文本文件|*.txt|所有文件|*.*",
                    FileName = path == null ? "未命名.md" : DisplayName,
                    Title = "保存 Markdown 文件",
                    AddExtension = true,
                    DefaultExt = ".md",
                    InitialDirectory = Path.GetDirectoryName(path),
                    OverwritePrompt = false
                };
                if (dlg.ShowDialog(this) != true) return false;
                path = Path.GetFullPath(dlg.FileName);
            }

            return SaveTo(path);
        }
        catch (Exception ex)
        {
            var msg = "保存失败：" + ex.Message;
            Notify(msg);
            ShowErrorBanner("保存失败", msg, isWarning: false, "另存为", () => SaveFile(true), null, null);
            return false;
        }
    }

    private bool SaveTo(string path)
    {
        var samePath = string.Equals(_currentFile, path, StringComparison.OrdinalIgnoreCase);
        var onDisk = MarkdownFile.ReadFingerprint(path);
        if (samePath && !_isDirty && onDisk == _loadedFingerprint)
        {
            Notify("没有需要保存的修改");
            return true;
        }

        var text = Editor.Text;
        var bytes = MarkdownFile.Encode(text, _encoding, _newLine);
        var fingerprint = MarkdownFile.Hash(bytes);
        if (fingerprint != onDisk)
        {
            string? prompt = null;
            if (samePath && onDisk != _loadedFingerprint)
            {
                prompt = onDisk == null
                    ? "原文件已被移动或删除。要按原路径重新保存吗？"
                    : "原文件已在其他程序中修改。仍要覆盖保存吗？";
            }
            else if (!samePath && onDisk != null)
            {
                prompt = "目标文件已存在。仍要覆盖保存吗？";
            }
            if (prompt != null && IsLoaded && IsVisible &&
                MessageBox.Show(this, $"{prompt}\n\n{path}", "Markdown 阅读器",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return false;

            _isSaving = true;
            try
            {
                MarkdownFile.Write(path, bytes, onDisk);
            }
            finally
            {
                _isSaving = false;
            }
        }

        if (!samePath) RememberCurrentReadingPosition();
        HideErrorBanner();
        _currentFile = path;
        _welcome = false;
        _welcomeGuideDismissed = true;
        _loadedFingerprint = fingerprint;
        _settings.AddRecentFile(path);
        _savedText = text;
        _isDirty = false;
        SetupFileWatcher(path);
        if (!samePath) _previewCurrent = false;
        if (_viewMode != "Edit") RenderPreview();
        RefreshOutline();
        RefreshFilesPanel();
        UpdateEmptyStateVisibility();
        UpdateFileLabel();
        UpdateTitle();
        if (_viewMode != "Edit" && _previewError != null) UpdateStatus();
        else Notify("已保存 " + DateTime.Now.ToString("HH:mm:ss"));
        return true;
    }

    private void OnReloadClick(object sender, RoutedEventArgs e) => Reload();

    private void Reload()
    {
        if (_currentFile is string path && ConfirmDiscard()) LoadFile(path);
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFile)) return;
        try
        {
            Clipboard.SetText(_currentFile);
            Notify("已复制文件路径：" + _currentFile);
        }
        catch (Exception ex)
        {
            Notify("复制路径失败：" + ex.Message);
        }
    }

    private void OnRevealFolderClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_currentFile) || !File.Exists(_currentFile)) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_currentFile}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify("打开所在目录失败：" + ex.Message);
        }
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var content = _welcome ? WelcomeText : Editor.Text;
            Clipboard.SetText(content);
            Notify($"已复制全文 ({content.Length:N0} 字符)");
        }
        catch (Exception ex)
        {
            Notify("复制全文失败：" + ex.Message);
        }
    }

    // ---------- 视图模式切换（阅读 Preview / 分栏 Split / 源码 Edit） ----------

    private void OnModePreviewClick(object sender, RoutedEventArgs e) => SetViewMode("Preview");
    private void OnModeSplitClick(object sender, RoutedEventArgs e) => SetViewMode("Split");
    private void OnModeEditClick(object sender, RoutedEventArgs e) => SetViewMode("Edit");

    private void OnToggleEdit(object sender, RoutedEventArgs e) => ToggleEdit();

    private void ToggleEdit() => SetEditing(!_isEditing);

    private void SetEditing(bool enabled)
    {
        SetViewMode(enabled ? "Edit" : "Preview");
    }

    public void SetViewMode(string mode, bool renderImmediate = true)
    {
        _viewMode = mode is "Split" or "Edit" ? mode : "Preview";
        _settings.ViewMode = _viewMode;
        _isEditing = _viewMode is "Edit" or "Split";

        ModePreviewButton.Tag = _viewMode == "Preview" ? "Active" : null;
        ModeSplitButton.Tag = _viewMode == "Split" ? "Active" : null;
        ModeEditButton.Tag = _viewMode == "Edit" ? "Active" : null;
        EditButton.Content = _isEditing ? "预览" : "编辑";

        EditFormatBar.Visibility = _isEditing && !_focusMode ? Visibility.Visible : Visibility.Collapsed;

        if (_isEditing && _welcome)
        {
            _welcome = false;
            _welcomeGuideDismissed = true;
            _previewCurrent = false;
            UpdateFileLabel();
        }

        ApplySplitPaneGeometry();
        UpdateEmptyStateVisibility();
        UpdateEditorPadding();
        if (Viewer.Document is FlowDocument currentDoc)
            currentDoc.PagePadding = GetResponsivePagePadding();

        if (_viewMode == "Preview")
        {
            _editorSearch.Close();
            if (renderImmediate) RenderPreview();
            Viewer.Focus();
        }
        else if (_viewMode == "Split")
        {
            if (renderImmediate) RenderPreview();
            Editor.TextArea.Focus();
        }
        else
        {
            Editor.TextArea.Focus();
        }

        UpdateTitle();
        UpdateStatus();
    }

    private void ApplySplitPaneGeometry()
    {
        VerticalSplitDivider.Visibility = Visibility.Collapsed;
        HorizontalSplitDivider.Visibility = Visibility.Collapsed;

        if (_viewMode == "Preview")
        {
            EditorHost.Visibility = Visibility.Collapsed;
            Editor.Visibility = Visibility.Collapsed;
            PreviewHost.Visibility = Visibility.Visible;
            Viewer.Visibility = Visibility.Visible;

            EditorPaneColumn.Width = new GridLength(0);
            SplitDividerColumn.Width = new GridLength(0);
            PreviewPaneColumn.Width = new GridLength(1, GridUnitType.Star);
            TopPaneRow.Height = new GridLength(1, GridUnitType.Star);
            HorizontalSplitDividerRow.Height = new GridLength(0);
            BottomPaneRow.Height = new GridLength(0);

            Grid.SetColumn(PreviewHost, 2);
            Grid.SetColumnSpan(PreviewHost, 1);
            Grid.SetRow(PreviewHost, 0);
            Grid.SetRowSpan(PreviewHost, 1);
        }
        else if (_viewMode == "Edit")
        {
            EditorHost.Visibility = Visibility.Visible;
            Editor.Visibility = Visibility.Visible;
            PreviewHost.Visibility = Visibility.Collapsed;
            Viewer.Visibility = Visibility.Collapsed;

            EditorPaneColumn.Width = new GridLength(1, GridUnitType.Star);
            SplitDividerColumn.Width = new GridLength(0);
            PreviewPaneColumn.Width = new GridLength(0);
            TopPaneRow.Height = new GridLength(1, GridUnitType.Star);
            HorizontalSplitDividerRow.Height = new GridLength(0);
            BottomPaneRow.Height = new GridLength(0);

            Grid.SetColumn(EditorHost, 0);
            Grid.SetColumnSpan(EditorHost, 1);
            Grid.SetRow(EditorHost, 0);
            Grid.SetRowSpan(EditorHost, 1);
        }
        else
        {
            // 分栏模式：宽屏左右并排，窄屏 (<640px) 自动响应式折叠为上下分栏
            EditorHost.Visibility = Visibility.Visible;
            Editor.Visibility = Visibility.Visible;
            PreviewHost.Visibility = Visibility.Visible;
            Viewer.Visibility = Visibility.Visible;

            if (_isMobileViewport)
            {
                EditorPaneColumn.Width = new GridLength(1, GridUnitType.Star);
                SplitDividerColumn.Width = new GridLength(0);
                PreviewPaneColumn.Width = new GridLength(0);

                TopPaneRow.Height = new GridLength(1, GridUnitType.Star);
                HorizontalSplitDividerRow.Height = new GridLength(1);
                BottomPaneRow.Height = new GridLength(1, GridUnitType.Star);

                HorizontalSplitDivider.Visibility = Visibility.Visible;

                Grid.SetColumn(EditorHost, 0);
                Grid.SetRow(EditorHost, 0);
                Grid.SetColumn(PreviewHost, 0);
                Grid.SetRow(PreviewHost, 2);
            }
            else
            {
                EditorPaneColumn.Width = new GridLength(1, GridUnitType.Star);
                SplitDividerColumn.Width = new GridLength(1);
                PreviewPaneColumn.Width = new GridLength(1, GridUnitType.Star);

                TopPaneRow.Height = new GridLength(1, GridUnitType.Star);
                HorizontalSplitDividerRow.Height = new GridLength(0);
                BottomPaneRow.Height = new GridLength(0);

                VerticalSplitDivider.Visibility = Visibility.Visible;

                Grid.SetColumn(EditorHost, 0);
                Grid.SetRow(EditorHost, 0);
                Grid.SetColumn(PreviewHost, 2);
                Grid.SetRow(PreviewHost, 0);
            }
        }
    }

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        _previewCurrent = false;
        _isDirty = Editor.Document.TextLength != _savedText.Length || Editor.Text != _savedText;
        UpdateFileLabel();
        UpdateTitle();
        UpdateStatus();

        if (_viewMode == "Split")
        {
            _splitRenderTimer.Stop();
            _splitRenderTimer.Start();
        }
    }

    // ---------- 源码快捷格式化与换行/编码切换 ----------

    private void OnFormatActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string action })
            ApplyMarkdownFormat(action);
    }

    public void ApplyMarkdownFormat(string action)
    {
        if (!_isEditing) SetViewMode("Edit");
        var area = Editor.TextArea;
        var doc = Editor.Document;
        var offset = area.Caret.Offset;
        var selStart = area.Selection.SurroundingSegment?.Offset ?? offset;
        var selLen = area.Selection.SurroundingSegment?.Length ?? 0;
        var selected = selLen > 0 ? doc.GetText(selStart, selLen) : "";
        var nl = _newLine ?? "\n";

        switch (action)
        {
            case "H1":
                SetHeadingLevel(1);
                break;
            case "H2":
                SetHeadingLevel(2);
                break;
            case "H3":
                SetHeadingLevel(3);
                break;
            case "Bold":
                ToggleWrap("**", "**", "粗体文本");
                break;
            case "Italic":
                ToggleWrap("*", "*", "斜体文本");
                break;
            case "Strike":
                ToggleWrap("~~", "~~", "删除线文本");
                break;
            case "InlineCode":
                ToggleWrap("`", "`", "code");
                break;
            case "CodeBlock":
                WrapSelection($"```{nl}", $"{nl}```", "代码片段");
                break;
            case "Link":
                InsertLink(selStart, selected);
                break;
            case "Image":
                InsertImageFromDialog();
                break;
            case "Quote":
                PrefixSelectedLines("> ");
                break;
            case "BulletList":
                PrefixSelectedLines("- ");
                break;
            case "OrderedList":
                PrefixSelectedLines("1. ", ordered: true);
                break;
            case "TaskList":
                ToggleTaskCurrentLine();
                break;
            case "Table":
                var table = $"{nl}| 列 A | 列 B | 列 C |{nl}| :--- | :--- | ---: |{nl}| 内容 | 内容 | 0 |{nl}";
                ReplaceSelection(table);
                break;
        }
        Editor.TextArea.Focus();
    }

    private static bool IsWebUrl(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !text.Any(char.IsWhiteSpace);

    private static string? ClipboardUrl()
    {
        try
        {
            if (!Clipboard.ContainsText()) return null;
            var text = Clipboard.GetText().Trim();
            return IsWebUrl(text) ? text : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>插入链接：选中网址则补标题；选中文字则补网址（优先用剪贴板中的网址），并选中待填部分。</summary>
    private void InsertLink(int start, string selected)
    {
        if (IsWebUrl(selected.Trim()))
        {
            const string label = "链接标题";
            ReplaceSelection($"[{label}]({selected.Trim()})");
            Editor.Select(start + 1, label.Length);
            return;
        }

        var text = selected.Length > 0 && !selected.Contains('\n') ? selected : "链接标题";
        var url = ClipboardUrl() ?? "https://";
        ReplaceSelection($"[{text}]({url})");
        if (selected.Length == 0) Editor.Select(start + 1, text.Length);
        else Editor.Select(start + text.Length + 3, url.Length);
    }

    private void InsertImageFromDialog()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "图片|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.svg;*.bmp|所有文件|*.*",
            Title = "插入图片",
            InitialDirectory = Path.GetDirectoryName(_currentFile)
        };
        if (dlg.ShowDialog(this) != true) return;
        ReplaceSelection($"![{Path.GetFileNameWithoutExtension(dlg.FileName)}]({ToMarkdownPath(dlg.FileName)})");
    }

    /// <summary>转为相对当前文档的 Markdown 路径（正斜杠、空格转义）；未保存文档用绝对路径。</summary>
    private string ToMarkdownPath(string file)
    {
        var path = _currentFile != null && Path.GetDirectoryName(_currentFile) is string baseDir
            ? Path.GetRelativePath(baseDir, file)
            : file;
        return path.Replace('\\', '/').Replace(" ", "%20");
    }

    /// <summary>行内包裹的开关：选区本身或两侧已有同样标记时去掉，否则加上。</summary>
    private void ToggleWrap(string prefix, string suffix, string placeholder)
    {
        var doc = Editor.Document;
        var seg = Editor.TextArea.Selection.SurroundingSegment;
        if (seg != null && seg.Length > 0)
        {
            var text = doc.GetText(seg.Offset, seg.Length);
            if (text.Length >= prefix.Length + suffix.Length && text.StartsWith(prefix, StringComparison.Ordinal) &&
                text.EndsWith(suffix, StringComparison.Ordinal))
            {
                var inner = text[prefix.Length..^suffix.Length];
                doc.Replace(seg.Offset, seg.Length, inner);
                Editor.Select(seg.Offset, inner.Length);
                return;
            }
            var before = seg.Offset - prefix.Length;
            var after = seg.EndOffset;
            if (before >= 0 && after + suffix.Length <= doc.TextLength &&
                doc.GetText(before, prefix.Length) == prefix && doc.GetText(after, suffix.Length) == suffix &&
                !(prefix == "*" && before > 0 && doc.GetCharAt(before - 1) == '*'))
            {
                using (doc.RunUpdate())
                {
                    doc.Remove(after, suffix.Length);
                    doc.Remove(before, prefix.Length);
                }
                Editor.Select(before, seg.Length);
                return;
            }
        }
        WrapSelection(prefix, suffix, placeholder);
    }

    /// <summary>设置当前行标题级别；已是该级别则取消标题。</summary>
    private void SetHeadingLevel(int level)
    {
        var doc = Editor.Document;
        var line = doc.GetLineByOffset(Editor.CaretOffset);
        var text = doc.GetText(line.Offset, line.Length);
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^(#{1,6})[ \t]+");
        var oldPrefix = match.Success ? match.Length : 0;
        var replacement = match.Success && match.Groups[1].Length == level ? "" : new string('#', level) + " ";
        doc.Replace(line.Offset, oldPrefix, replacement);
        Editor.CaretOffset = line.Offset + replacement.Length + (text.Length - oldPrefix);
    }

    /// <summary>给选中的每一行加 / 去前缀（引用、列表）；全部已有前缀时去掉。</summary>
    private void PrefixSelectedLines(string prefix, bool ordered = false)
    {
        var doc = Editor.Document;
        var seg = Editor.TextArea.Selection.SurroundingSegment;
        var startOffset = seg?.Offset ?? Editor.CaretOffset;
        var endOffset = seg is { Length: > 0 } ? seg.EndOffset - 1 : startOffset;
        var first = doc.GetLineByOffset(startOffset).LineNumber;
        var last = doc.GetLineByOffset(Math.Max(startOffset, endOffset)).LineNumber;

        var orderedPattern = new System.Text.RegularExpressions.Regex(@"^\d{1,9}[.)][ \t]+");
        var lines = Enumerable.Range(first, last - first + 1).Select(doc.GetLineByNumber).ToList();
        var skipBlank = lines.Count > 1 && prefix != "> ";
        var targets = lines.Where(l => !skipBlank || doc.GetText(l.Offset, l.Length).Trim().Length > 0).ToList();
        if (targets.Count == 0) targets = lines;

        int MatchLength(string text) => ordered
            ? orderedPattern.Match(text) is { Success: true } m ? m.Length : -1
            : text.StartsWith(prefix, StringComparison.Ordinal) ? prefix.Length : -1;

        var allPrefixed = targets.All(l => MatchLength(doc.GetText(l.Offset, l.Length)) >= 0);
        using (doc.RunUpdate())
        {
            for (var i = targets.Count - 1; i >= 0; i--)
            {
                var line = targets[i];
                var text = doc.GetText(line.Offset, line.Length);
                if (allPrefixed)
                    doc.Remove(line.Offset, MatchLength(text));
                else if (MatchLength(text) < 0)
                    doc.Insert(line.Offset, ordered ? $"{i + 1}. " : prefix);
            }
        }
    }

    private void WrapSelection(string prefix, string suffix, string placeholder)
    {
        var seg = Editor.TextArea.Selection.SurroundingSegment;
        var start = seg?.Offset ?? Editor.CaretOffset;
        var len = seg?.Length ?? 0;
        var inner = len > 0 ? Editor.Document.GetText(start, len) : placeholder;
        var replacement = prefix + inner + suffix;
        Editor.Document.Replace(start, len, replacement);
        Editor.Select(start + prefix.Length, inner.Length);
    }

    private void ReplaceSelection(string replacement)
    {
        var seg = Editor.TextArea.Selection.SurroundingSegment;
        var start = seg?.Offset ?? Editor.CaretOffset;
        var len = seg?.Length ?? 0;
        Editor.Document.Replace(start, len, replacement);
        Editor.CaretOffset = start + replacement.Length;
    }

    private void ToggleTaskCurrentLine()
    {
        var line = Editor.Document.GetLineByNumber(Editor.TextArea.Caret.Line);
        var lineText = Editor.Document.GetText(line.Offset, line.Length);
        var trimmed = lineText.TrimStart();
        var indentLen = lineText.Length - trimmed.Length;
        if (trimmed.StartsWith("- [ ] ", StringComparison.Ordinal))
        {
            Editor.Document.Replace(line.Offset + indentLen, 6, "- [x] ");
        }
        else if (trimmed.StartsWith("- [x] ", StringComparison.OrdinalIgnoreCase))
        {
            Editor.Document.Replace(line.Offset + indentLen, 6, "- [ ] ");
        }
        else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
        {
            // 已是普通列表项：只补复选框
            Editor.Document.Insert(line.Offset + indentLen + 2, "[ ] ");
        }
        else
        {
            Editor.Document.Insert(line.Offset + indentLen, "- [ ] ");
        }
    }

    private void OnToggleWordWrapClick(object sender, RoutedEventArgs e)
    {
        _settings.WordWrap = !_settings.WordWrap;
        Editor.WordWrap = _settings.WordWrap;
        UpdateWordWrapUI();
    }

    private void UpdateWordWrapUI()
    {
        WordWrapButton.Tag = _settings.WordWrap ? "Active" : null;
        WordWrapButton.ToolTip = _settings.WordWrap ? "自动折行：开 (Alt+Z)" : "自动折行：关 (Alt+Z)";
    }

    private void OnCycleEncodingClick(object sender, RoutedEventArgs e)
    {
        // UTF-8 ⇄ UTF-8 BOM；其他编码（GB18030、UTF-16/32）一键转为 UTF-8
        var isUtf8 = MarkdownFile.IsUtf8(_encoding);
        var hasBom = _encoding.GetPreamble().Length > 0;
        _encoding = isUtf8 && !hasBom ? MarkdownFile.Utf8Bom : MarkdownFile.Utf8;
        _isDirty = true;
        UpdateFileLabel();
        UpdateTitle();
        UpdateStatus();
        Notify($"保存编码改为 {MarkdownFile.DescribeEncoding(_encoding)}，Ctrl+S 后生效");
    }

    private void OnCycleNewLineClick(object sender, RoutedEventArgs e)
    {
        _newLine = _newLine == "\r\n" ? "\n" : "\r\n";
        _isDirty = true;
        UpdateFileLabel();
        UpdateTitle();
        UpdateStatus();
        Notify($"换行改为 {MarkdownFile.DescribeNewLine(_newLine)}，Ctrl+S 后生效");
    }

    private void OnToggleAutoReloadClick(object sender, RoutedEventArgs e)
    {
        _settings.AutoReload = !_settings.AutoReload;
        UpdateAutoReloadUI();
        Notify(_settings.AutoReload ? "外部修改时自动同步：开" : "外部修改时自动同步：关");
    }

    private void UpdateAutoReloadUI()
    {
        // 自动同步开关在“更多”菜单中，打开菜单时读取当前值
    }

    // ---------- “更多”菜单 ----------

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)FindResource("MoreMenu");
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            item.IsChecked = (item.Tag as string) switch
            {
                "Sidebar" => SidebarPanel.Visibility == Visibility.Visible,
                "Focus" => _focusMode,
                "Dark" => _settings.Theme == "Dark",
                "Wrap" => _settings.WordWrap,
                "AutoReload" => _settings.AutoReload,
                _ => false
            };
            if (item.Header is string header && header.StartsWith("重置字号", StringComparison.Ordinal))
                item.Header = $"重置字号（当前 {_fontSize:0}px）";
        }
        menu.PlacementTarget = MoreButton;
        menu.Placement = PlacementMode.Custom;
        // 菜单右缘对齐按钮右缘（模板右侧 10px 为阴影留白）
        menu.CustomPopupPlacementCallback = (popupSize, targetSize, _) => new[]
        {
            new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width + 10, targetSize.Height + 4), PopupPrimaryAxis.Horizontal)
        };
        menu.IsOpen = true;
    }

    // ---------- 专注模式 ----------

    private void OnToggleFocusClick(object sender, RoutedEventArgs e) => SetFocusMode(!_focusMode);

    public void SetFocusMode(bool enabled)
    {
        _focusMode = enabled;
        var chrome = enabled ? Visibility.Collapsed : Visibility.Visible;
        ToolbarBorder.Visibility = chrome;
        StatusBarBorder.Visibility = chrome;
        EditFormatBar.Visibility = _isEditing && !enabled ? Visibility.Visible : Visibility.Collapsed;
        ApplyResponsiveLayout(ActualWidth > 0 ? ActualWidth : Width);
        if (enabled) ShowToast("专注模式 · F11 或 Esc 退出");
        else HideToast(null, EventArgs.Empty);
    }

    private DispatcherTimer? _toastTimer;
    private Border? _toast;

    /// <summary>专注模式下状态栏隐藏，用短暂的浮动提示代替。</summary>
    private void ShowToast(string message)
    {
        if (_toast == null)
        {
            _toast = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 24),
                IsHitTestVisible = false,
                Child = new TextBlock { FontSize = 12 }
            };
            RootGrid.Children.Add(_toast);
        }
        _toast.Background = Brush(_palette.Foreground);
        ((TextBlock)_toast.Child).Foreground = Brush(_palette.Background);
        ((TextBlock)_toast.Child).Text = message;
        _toast.Visibility = Visibility.Visible;
        _toastTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.2) };
        _toastTimer.Tick -= HideToast;
        _toastTimer.Tick += HideToast;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void HideToast(object? sender, EventArgs e)
    {
        _toastTimer?.Stop();
        if (_toast != null) _toast.Visibility = Visibility.Collapsed;
    }

    // ---------- 快速切换（Ctrl+P） ----------

    private void OnQuickOpenClick(object sender, RoutedEventArgs e) => ShowQuickOpen();

    public void ShowQuickOpen(string initial = "")
    {
        ShortcutsOverlay.Visibility = Visibility.Collapsed;
        QuickOpenOverlay.Visibility = Visibility.Visible;
        QuickOpenBox.Text = initial;
        QuickOpenBox.CaretIndex = initial.Length;
        RefreshQuickOpen();
        Dispatcher.BeginInvoke(() =>
        {
            QuickOpenBox.Focus();
            Keyboard.Focus(QuickOpenBox);
        }, DispatcherPriority.Input);
    }

    private void HideOverlays()
    {
        var wasOpen = QuickOpenOverlay.Visibility == Visibility.Visible || ShortcutsOverlay.Visibility == Visibility.Visible;
        QuickOpenOverlay.Visibility = Visibility.Collapsed;
        ShortcutsOverlay.Visibility = Visibility.Collapsed;
        if (wasOpen) FocusCanvas();
    }

    private void FocusCanvas()
    {
        if (_isEditing) Editor.TextArea.Focus();
        else Viewer.Focus();
    }

    private void OnOverlayBackgroundClick(object sender, MouseButtonEventArgs e) => HideOverlays();

    private void OnOverlayCardClick(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnQuickOpenTextChanged(object sender, TextChangedEventArgs e)
    {
        QuickOpenPlaceholder.Visibility = QuickOpenBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RefreshQuickOpen();
    }

    /// <summary>候选：最近文档优先，其次同文件夹文档；# 开头时列出当前文档标题。</summary>
    private List<QuickItem> BuildQuickItems(string query)
    {
        if (query.StartsWith('#'))
        {
            var term = query.TrimStart('#').Trim();
            var outline = MarkdownFile.ExtractOutline(_welcome ? WelcomeText : Editor.Text);
            return outline
                .Select(h => (Item: h, Score: Score(h.Title, term)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => new QuickItem(new string(' ', (x.Item.Level - 1) * 2) + x.Item.Title,
                    $"第 {x.Item.LineNumber} 行", null, x.Item))
                .ToList();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_currentFile != null) seen.Add(_currentFile);
        var candidates = new List<QuickItem>();
        foreach (var path in _settings.RecentFiles)
        {
            if (seen.Add(path) && File.Exists(path))
                candidates.Add(new QuickItem(Path.GetFileName(path), ShortenDirectory(Path.GetDirectoryName(path) ?? ""), path, null));
        }
        foreach (var sib in MarkdownFile.ListSiblingMarkdownFiles(_currentFile, 500))
        {
            if (seen.Add(sib.FullPath))
                candidates.Add(new QuickItem(sib.Name, "此文件夹", sib.FullPath, null));
        }

        var trimmed = query.Trim();
        if (trimmed.Length == 0) return candidates;
        return candidates
            // 文件名支持子序列匹配；完整路径只做包含匹配，避免长路径误中
            .Select(c => (Item: c, Score: Math.Max(Score(c.Title, trimmed),
                c.Path != null && c.Path.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ? 0.5 : 0)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Item)
            .ToList();
    }

    /// <summary>简单打分：前缀 &gt; 包含 &gt; 按顺序出现的字符（子序列）。</summary>
    internal static double Score(string text, string term)
    {
        if (term.Length == 0) return 1;
        var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        if (index == 0) return 3;
        if (index > 0) return 2;
        var t = 0;
        foreach (var c in text)
        {
            if (char.ToUpperInvariant(c) == char.ToUpperInvariant(term[t]) && ++t == term.Length)
                return 1;
        }
        return 0;
    }

    private void RefreshQuickOpen()
    {
        var items = BuildQuickItems(QuickOpenBox.Text);
        QuickOpenList.Items.Clear();
        var itemStyle = (Style)Resources["PaletteItem"];
        foreach (var item in items.Take(60))
        {
            var row = new DockPanel { LastChildFill = true };
            var detail = new TextBlock
            {
                Text = item.Detail,
                FontSize = 11.5,
                MaxWidth = 260,
                Margin = new Thickness(16, 0, 0, 0),
                Foreground = Brush(_palette.Muted),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(detail, Dock.Right);
            row.Children.Add(detail);
            row.Children.Add(new TextBlock
            {
                Text = item.Title,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            QuickOpenList.Items.Add(new ListBoxItem { Content = row, Tag = item, Style = itemStyle, ToolTip = item.Path });
        }
        QuickOpenList.SelectedIndex = QuickOpenList.Items.Count > 0 ? 0 : -1;
        QuickOpenList.Visibility = QuickOpenList.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QuickOpenEmptyText.Visibility = QuickOpenList.Items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        QuickOpenEmptyText.Text = QuickOpenBox.Text.StartsWith('#') ? "没有匹配的标题" : "没有匹配的文档";
    }

    private void OnQuickOpenKeyDown(object sender, KeyEventArgs e)
    {
        var count = QuickOpenList.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                QuickOpenList.SelectedIndex = (QuickOpenList.SelectedIndex + 1) % count;
                QuickOpenList.ScrollIntoView(QuickOpenList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when count > 0:
                QuickOpenList.SelectedIndex = (QuickOpenList.SelectedIndex - 1 + count) % count;
                QuickOpenList.ScrollIntoView(QuickOpenList.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                if (QuickOpenList.SelectedItem is ListBoxItem { Tag: QuickItem item }) ActivateQuickItem(item);
                e.Handled = true;
                break;
            case Key.Escape:
                HideOverlays();
                e.Handled = true;
                break;
        }
    }

    private void OnQuickOpenListClick(object sender, MouseButtonEventArgs e)
    {
        if (QuickOpenList.SelectedItem is ListBoxItem { Tag: QuickItem item }) ActivateQuickItem(item);
    }

    private void ActivateQuickItem(QuickItem item)
    {
        HideOverlays();
        if (item.Heading != null)
        {
            NavigateToOutlineItem(item.Heading);
            return;
        }
        if (item.Path != null && !string.Equals(item.Path, _currentFile, StringComparison.OrdinalIgnoreCase) && ConfirmDiscard())
            LoadFile(item.Path);
    }

    // ---------- 快捷键速查（F1） ----------

    private void OnShowShortcutsClick(object sender, RoutedEventArgs e) => ToggleShortcuts();

    private void ToggleShortcuts()
    {
        if (ShortcutsOverlay.Visibility == Visibility.Visible)
        {
            HideOverlays();
            return;
        }
        QuickOpenOverlay.Visibility = Visibility.Collapsed;
        // 尽量一屏放下全部快捷键，窗口太矮时再滚动
        ShortcutsScroller.MaxHeight = Math.Max(160, (ActualHeight > 0 ? ActualHeight : Height) - 200);
        ShortcutsOverlay.Visibility = Visibility.Visible;
        ShortcutsOverlay.Focusable = true;
        ShortcutsOverlay.Focus();
    }

    private void BuildShortcutsGrid()
    {
        ShortcutsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 0 });
        ShortcutsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var mono = (FontFamily)Resources["MonoFontFamily"];
        for (var i = 0; i < ShortcutList.Length; i++)
        {
            ShortcutsGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
            var action = new TextBlock { Text = ShortcutList[i].Action, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            action.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryFgBrush");
            var keys = new TextBlock { Text = ShortcutList[i].Keys, FontFamily = mono, FontSize = 11.5, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            keys.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");
            Grid.SetRow(action, i);
            Grid.SetRow(keys, i);
            Grid.SetColumn(keys, 1);
            ShortcutsGrid.Children.Add(action);
            ShortcutsGrid.Children.Add(keys);
        }
    }

    // ---------- 编辑辅助：选区包裹、列表续写与缩进、智能粘贴、分栏滚动同步 ----------

    private void OnEditorTextEntering(object sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1 || Editor.TextArea.Selection.IsEmpty || Editor.TextArea.Selection.IsMultiline) return;
        var closing = e.Text[0] switch
        {
            '*' => "*",
            '_' => "_",
            '`' => "`",
            '~' => "~",
            '"' => "\"",
            '(' => ")",
            '[' => "]",
            '“' => "”",
            '《' => "》",
            _ => null
        };
        if (closing == null) return;
        WrapSelection(e.Text, closing, "");
        e.Handled = true;
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (e.Key == Key.Enter && mods == ModifierKeys.None && Editor.TextArea.Selection.IsEmpty)
        {
            if (TryContinueList()) e.Handled = true;
        }
        else if (e.Key == Key.Tab && (mods == ModifierKeys.None || mods == ModifierKeys.Shift) && Editor.TextArea.Selection.IsEmpty)
        {
            if (TryIndentListItem(outdent: mods == ModifierKeys.Shift)) e.Handled = true;
        }
    }

    /// <summary>回车续写列表 / 引用标记；空列表项回车清除标记。</summary>
    public bool TryContinueList()
    {
        var doc = Editor.Document;
        var caret = Editor.CaretOffset;
        var line = doc.GetLineByOffset(caret);
        var text = doc.GetText(line.Offset, line.Length);
        var continuation = MarkdownFile.GetListContinuation(text);
        if (continuation == null || caret - line.Offset < continuation.PrefixLength || IsInsideCodeFence(line.LineNumber))
            return false;

        using (doc.RunUpdate())
        {
            if (continuation.EndList)
            {
                doc.Remove(line.Offset, line.Length);
                Editor.CaretOffset = line.Offset;
            }
            else
            {
                var insert = NewLineFor(line) + continuation.NextPrefix;
                doc.Insert(caret, insert);
                Editor.CaretOffset = caret + insert.Length;
            }
        }
        Editor.TextArea.Caret.BringCaretToView();
        return true;
    }

    public bool TryIndentListItem(bool outdent)
    {
        var doc = Editor.Document;
        var caret = Editor.CaretOffset;
        var line = doc.GetLineByOffset(caret);
        var text = doc.GetText(line.Offset, line.Length);
        var unit = MarkdownFile.GetListIndentUnit(text);
        if (unit == null || IsInsideCodeFence(line.LineNumber)) return false;

        if (!outdent)
        {
            doc.Insert(line.Offset, unit);
            Editor.CaretOffset = caret + unit.Length;
            return true;
        }

        var leading = 0;
        while (leading < text.Length && leading < unit.Length && text[leading] is ' ' or '\t') leading++;
        if (leading == 0) return true;
        doc.Remove(line.Offset, leading);
        Editor.CaretOffset = Math.Max(line.Offset, caret - leading);
        return true;
    }

    private bool IsInsideCodeFence(int lineNumber)
    {
        var doc = Editor.Document;
        var inside = false;
        for (var n = 1; n < lineNumber; n++)
        {
            var l = doc.GetLineByNumber(n);
            var t = doc.GetText(l.Offset, Math.Min(l.Length, 16)).TrimStart();
            if (t.StartsWith("```", StringComparison.Ordinal) || t.StartsWith("~~~", StringComparison.Ordinal))
                inside = !inside;
        }
        return inside;
    }

    private string NewLineFor(ICSharpCode.AvalonEdit.Document.DocumentLine line)
    {
        var source = line.DelimiterLength > 0 ? line : line.PreviousLine;
        if (source is { DelimiterLength: > 0 })
            return Editor.Document.GetText(source.EndOffset, source.DelimiterLength);
        return _newLine ?? "\n";
    }

    /// <summary>粘贴：剪贴板只有图片时存为文件并插入引用；选中文字粘贴网址时生成链接。返回 true 表示已处理。</summary>
    private bool TrySmartPaste()
    {
        try
        {
            if (Clipboard.ContainsImage() && !Clipboard.ContainsText())
                return PasteClipboardImage();

            if (!Editor.TextArea.Selection.IsEmpty && ClipboardUrl() is string url)
            {
                var selected = Editor.SelectedText;
                if (!selected.Contains('\n') && !IsWebUrl(selected.Trim()))
                {
                    ReplaceSelection($"[{selected}]({url})");
                    Notify("已生成链接");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Notify("粘贴失败：" + ex.Message);
            return true;
        }
        return false;
    }

    private bool PasteClipboardImage()
    {
        if (_currentFile == null)
        {
            ShowErrorBanner("需要先保存", "图片会存到文档旁的 assets 文件夹，请先保存当前文档。", isWarning: true,
                "保存", () => SaveFile(false), null, null);
            return true;
        }

        BitmapSource? image = null;
        if (Clipboard.GetData("PNG") is MemoryStream png)
            image = BitmapFrame.Create(png, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        image ??= Clipboard.GetImage();
        if (image == null) return false;

        var baseDir = Path.GetDirectoryName(_currentFile)!;
        var path = SaveImageToAssets(image, baseDir, Path.GetFileNameWithoutExtension(_currentFile));
        ReplaceSelection($"![]({ToMarkdownPath(path)})");
        Notify($"截图已保存：{Path.GetRelativePath(baseDir, path)}");
        return true;
    }

    /// <summary>保存到 &lt;文档目录&gt;/assets/&lt;文档名&gt;-时间.png，重名时追加序号，不覆盖已有文件。</summary>
    public static string SaveImageToAssets(BitmapSource image, string documentDirectory, string documentName)
    {
        var dir = Path.Combine(documentDirectory, "assets");
        Directory.CreateDirectory(dir);
        var baseName = $"{documentName}-{DateTime.Now:yyyyMMdd-HHmmss}";
        var path = Path.Combine(dir, baseName + ".png");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(dir, $"{baseName}-{i}.png");

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(stream);
        return path;
    }

    /// <summary>分栏模式：预览按源码滚动比例跟随。</summary>
    private void SyncPreviewToEditor()
    {
        if (_viewMode != "Split" || ViewerScroller is not { } scroller) return;
        var view = Editor.TextArea.TextView;
        var previewScrollable = scroller.ExtentHeight - scroller.ViewportHeight;
        if (!double.IsFinite(previewScrollable) || previewScrollable <= 0) return;
        var editorScrollable = view.DocumentHeight - view.ActualHeight;
        var ratio = editorScrollable > 1 ? Math.Clamp(view.ScrollOffset.Y / editorScrollable, 0, 1) : 0;
        scroller.ScrollToVerticalOffset(ratio * previewScrollable);
    }

    /// <summary>同文件夹上一篇 / 下一篇（按文件名排序，循环）。</summary>
    private void OpenSibling(int delta)
    {
        var siblings = MarkdownFile.ListSiblingMarkdownFiles(_currentFile, 2000);
        var index = siblings.ToList().FindIndex(s => s.IsCurrent);
        if (siblings.Count < 2 || index < 0)
        {
            Notify("此文件夹没有其他 Markdown 文档");
            return;
        }
        var next = siblings[(index + delta + siblings.Count) % siblings.Count];
        if (ConfirmDiscard()) LoadFile(next.FullPath);
    }

    // ---------- 响应式布局 ----------

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyResponsiveLayout(e.NewSize.Width);
    }

    public void ApplyResponsiveLayout(double width)
    {
        if (double.IsNaN(width) || width <= 0) width = Width;

        var wasNarrow = _isNarrowViewport;
        var wasMobile = _isMobileViewport;
        _isNarrowViewport = width < NarrowBreakpoint;
        _isMobileViewport = width < MobileSplitBreakpoint;

        if (_isNarrowViewport != wasNarrow)
        {
            _manualSidebarOverrideInNarrow = null;
            ApplySidebarVisibility(!_isNarrowViewport && _sidebarOpen);
        }
        else if (!_isNarrowViewport)
        {
            ApplySidebarVisibility(_sidebarOpen);
        }
        else
        {
            ApplySidebarVisibility(_manualSidebarOverrideInNarrow ?? false);
        }

        // 窄屏隐藏目录路径与打开按钮，极窄屏再隐藏字数统计
        OpenButton.Visibility = _isMobileViewport ? Visibility.Collapsed : Visibility.Visible;
        StatsMetricText.Visibility = _isMobileViewport ? Visibility.Collapsed : Visibility.Visible;
        UpdateFileLabel();

        UpdateEditorPadding();
        if (Viewer.Document is FlowDocument doc)
            doc.PagePadding = GetResponsivePagePadding();

        if (_isMobileViewport != wasMobile && _viewMode == "Split")
            ApplySplitPaneGeometry();
    }

    // ---------- 状态栏、文件名与窗口标题 ----------

    private void UpdateFileLabel()
    {
        FileLabel.Inlines.Clear();
        var name = _currentFile != null ? Path.GetFileName(_currentFile) : _welcome ? "Markdown 阅读器" : "未命名";
        FileLabel.Inlines.Add(new Run(name) { FontWeight = FontWeights.SemiBold });
        if (_currentFile != null && !_isNarrowViewport && Path.GetDirectoryName(_currentFile) is string dir)
        {
            var folder = new Run("    " + ShortenDirectory(dir)) { FontSize = 12 };
            folder.SetResourceReference(TextElement.ForegroundProperty, "MutedBrush");
            FileLabel.Inlines.Add(folder);
        }
        FileLabel.ToolTip = _currentFile ?? name;
        DirtyBadge.Visibility = _isDirty ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.Tag = _isDirty ? "Active" : null;
    }

    private void UpdateTitle()
    {
        var dirty = _isDirty ? " *" : "";
        Title = _welcome ? "Markdown 阅读器" : $"{DisplayName}{dirty} - Markdown 阅读器";
    }

    /// <summary>临时提示：显示约 4 秒，期间不被默认状态覆盖。</summary>
    private void Notify(string message)
    {
        StatusText.Text = message;
        _statusHoldUntil = DateTime.UtcNow.AddSeconds(4);
        if (_focusMode) ShowToast(message);
    }

    private void UpdateStatus()
    {
        var source = _welcome ? WelcomeText : Editor.Text;
        var metrics = MarkdownFile.AnalyzeText(source);

        if (_isEditing)
            CaretMetricText.Text = $"行 {Editor.TextArea.Caret.Line}，列 {Editor.TextArea.Caret.Column}";
        else
            UpdateReadingProgress();
        StatsMetricText.Text = metrics.ReadingMinutes > 0
            ? $"{metrics.Words:N0} 字 · 约 {metrics.ReadingMinutes} 分钟"
            : $"{metrics.Words:N0} 字";
        EncodingMetricButton.Content = MarkdownFile.DescribeEncoding(_encoding);
        NewLineMetricButton.Content = MarkdownFile.DescribeNewLine(_newLine);

        if (_viewMode != "Edit" && _previewError != null)
        {
            StatusText.Text = _previewError;
            return;
        }
        if (DateTime.UtcNow < _statusHoldUntil) return;

        StatusText.Text = _isDirty ? "未保存 · Ctrl+S 保存"
            : _currentFile == null && !_welcome ? "新文档"
            : "";
    }

    // ---------- 字号、拖放、快捷键 ----------

    private void OnFontUp(object sender, RoutedEventArgs e) => SetFontSize(_fontSize + 1);
    private void OnFontDown(object sender, RoutedEventArgs e) => SetFontSize(_fontSize - 1);
    private void OnFontResetClick(object sender, RoutedEventArgs e) => SetFontSize(16);

    private void SetFontSize(double size)
    {
        _fontSize = Math.Clamp(size, 12, 30);
        _settings.FontSize = _fontSize;
        Editor.FontSize = _fontSize;
        if (Viewer.Document is FlowDocument doc)
        {
            doc.FontSize = _fontSize;
            doc.PagePadding = GetResponsivePagePadding();
            AdjustBlocks(doc.Blocks, 0);
        }
        UpdateEditorPadding();
        UpdateStatus();
        Notify($"字号 {_fontSize:0}px");
    }

    private void OnFileDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } files) return;
        e.Handled = true;
        var dropped = files[0];
        var ext = Path.GetExtension(dropped).ToLowerInvariant();
        if (_isEditing && ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".bmp")
        {
            var refs = files
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".svg" or ".bmp")
                .Select(f => $"![{Path.GetFileNameWithoutExtension(f)}]({ToMarkdownPath(f)})");
            ReplaceSelection(string.Join(_newLine ?? "\n", refs));
            Notify($"已插入图片引用：{Path.GetFileName(dropped)}");
            return;
        }

        if (ConfirmDiscard()) LoadFile(dropped);
    }

    private void OnShortcut(object sender, KeyEventArgs e)
    {
        var mod = Keyboard.Modifiers;
        var ctrl = mod == ModifierKeys.Control;
        var ctrlShift = mod == (ModifierKeys.Control | ModifierKeys.Shift);
        var alt = mod == ModifierKeys.Alt;
        var none = mod == ModifierKeys.None;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var editorFocused = _isEditing && Editor.TextArea.IsKeyboardFocusWithin;

        if (key == Key.P && ctrl)
        {
            if (QuickOpenOverlay.Visibility == Visibility.Visible) HideOverlays();
            else ShowQuickOpen();
            e.Handled = true;
        }
        else if (key == Key.F1 && none) { ToggleShortcuts(); e.Handled = true; }
        else if (key == Key.F11 && none) { SetFocusMode(!_focusMode); e.Handled = true; }
        else if (QuickOpenOverlay.Visibility == Visibility.Visible) { /* 浮层打开时其余快捷键交给输入框 */ }
        else if (key == Key.N && ctrl) { NewFile(); e.Handled = true; }
        else if (key == Key.O && ctrl) { OpenDialog(); e.Handled = true; }
        else if (key == Key.W && ctrl) { Close(); e.Handled = true; }
        else if (key == Key.S && ctrlShift) { SaveFile(true); e.Handled = true; }
        else if (key == Key.E && ctrlShift) { ExportPdfDialog(); e.Handled = true; }
        else if (key == Key.S && ctrl) { SaveFile(false); e.Handled = true; }
        else if (key == Key.E && ctrl) { ToggleEdit(); e.Handled = true; }
        else if (key == Key.D1 && ctrl) { SetViewMode("Preview"); e.Handled = true; }
        else if ((key == Key.D2 && ctrl) || (key == Key.Oem5 && ctrl)) { SetViewMode("Split"); e.Handled = true; }
        else if (key == Key.D3 && ctrl) { SetViewMode("Edit"); e.Handled = true; }
        else if (key == Key.B && ctrlShift) { ToggleSidebar(); e.Handled = true; }
        else if (key == Key.L && ctrlShift) { ToggleTheme(); e.Handled = true; }
        else if (key == Key.X && ctrlShift && _isEditing) { ApplyMarkdownFormat("Strike"); e.Handled = true; }
        else if (key == Key.B && ctrl && _isEditing) { ApplyMarkdownFormat("Bold"); e.Handled = true; }
        else if (key == Key.I && ctrl && _isEditing) { ApplyMarkdownFormat("Italic"); e.Handled = true; }
        else if (key == Key.K && ctrl && _isEditing) { ApplyMarkdownFormat("Link"); e.Handled = true; }
        else if (key == Key.Oem3 && ctrl && _isEditing) { ApplyMarkdownFormat("InlineCode"); e.Handled = true; }
        else if (key == Key.V && ctrl && editorFocused && TrySmartPaste()) { e.Handled = true; }
        else if (key == Key.Z && alt && _isEditing) { OnToggleWordWrapClick(this, new RoutedEventArgs()); e.Handled = true; }
        else if (key == Key.Left && alt) { NavigateHistory(back: true); e.Handled = true; }
        else if (key == Key.Right && alt) { NavigateHistory(back: false); e.Handled = true; }
        else if (key == Key.PageDown && ctrl) { OpenSibling(1); e.Handled = true; }
        else if (key == Key.PageUp && ctrl) { OpenSibling(-1); e.Handled = true; }
        else if (key == Key.F && ctrl) { ShowFind(); e.Handled = true; }
        else if (key == Key.F5) { Reload(); e.Handled = true; }
        else if ((ctrl || ctrlShift) && key is Key.Add or Key.OemPlus) { SetFontSize(_fontSize + 1); e.Handled = true; }
        else if (ctrl && key is Key.Subtract or Key.OemMinus) { SetFontSize(_fontSize - 1); e.Handled = true; }
        else if (ctrl && key is Key.D0 or Key.NumPad0) { SetFontSize(16); e.Handled = true; }
    }

    /// <summary>Esc 逐层退出：浮层 → 专注模式 → 横幅 → 编辑回到阅读。不再用 Esc 关闭窗口（改为 Ctrl+W）。</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (QuickOpenOverlay.Visibility == Visibility.Visible || ShortcutsOverlay.Visibility == Visibility.Visible)
            HideOverlays();
        else if (_focusMode)
            SetFocusMode(false);
        else if (ErrorBanner.Visibility == Visibility.Visible)
            HideErrorBanner();
        else if (_isEditing)
            SetViewMode("Preview");
    }

    private void OnFindClick(object sender, RoutedEventArgs e) => ShowFind();

    private void ShowFind()
    {
        if (_viewMode == "Edit" || (_viewMode == "Split" && !Viewer.IsKeyboardFocusWithin))
        {
            _editorSearch.Open();
            _editorSearch.Reactivate();
            return;
        }

        Viewer.ApplyTemplate();
        var host = Viewer.Template.FindName("PART_FindToolBarHost", Viewer) as Decorator;
        if (host?.Child == null) Viewer.Find();
        if (host?.Child is not Control toolbar) return;

        Viewer.UpdateLayout();
        if (toolbar.Template.FindName("FindTextBox", toolbar) is TextBox find)
        {
            find.Focus();
            find.SelectAll();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard())
        {
            e.Cancel = true;
            return;
        }

        RememberCurrentReadingPosition();
        SetupFileWatcher(null);
        _settings.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            _settings.Left = Left;
            _settings.Top = Top;
            _settings.Width = Width;
            _settings.Height = Height;
        }
        if (SidebarColumn.ActualWidth >= 180)
            _settings.SidebarWidth = SidebarColumn.ActualWidth;
        _settings.FontSize = _fontSize;
        _settings.SidebarOpen = _sidebarOpen;
        _settings.SidebarTab = _sidebarTab;
        _settings.ViewMode = _viewMode;
        _settings.Save();
    }

    /// <summary>有未保存修改时询问；返回 false 表示用户取消当前操作。</summary>
    private bool ConfirmDiscard()
    {
        if (!_isDirty) return true;
        if (!IsLoaded || !IsVisible) return true;
        var r = MessageBox.Show(this, $"“{DisplayName}”有未保存的修改，是否保存？",
            "Markdown 阅读器", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        if (r == MessageBoxResult.Yes) return SaveFile(false);
        return true;
    }
}

public sealed class Palette
{
    public Color Background;
    public Color Sidebar;
    public Color Toolbar;
    public Color Subtle;
    public Color Hover;
    public Color Active;
    public Color Foreground;
    public Color SecondaryForeground;
    public Color Muted;
    public Color LineNumber;
    public Color Border;
    public Color QuoteBar;
    public Color Accent;
    public Color Selection;
    public Color Warning;
    public Color WarningBg;
    public Color Danger;
    public Color DangerBg;

    // 浅色：纯白画布 + 中性浅灰侧栏，顶栏与画布同色，层级只靠 1px 细线
    public static Palette Light => new()
    {
        Background = Color.FromRgb(0xFF, 0xFF, 0xFF),
        Sidebar = Color.FromRgb(0xF7, 0xF7, 0xF8),
        Toolbar = Color.FromRgb(0xFF, 0xFF, 0xFF),
        Subtle = Color.FromRgb(0xF3, 0xF3, 0xF5),
        Hover = Color.FromRgb(0xEE, 0xEE, 0xF1),
        Active = Color.FromRgb(0xE6, 0xE6, 0xEA),
        Foreground = Color.FromRgb(0x1D, 0x1D, 0x1F),
        SecondaryForeground = Color.FromRgb(0x4A, 0x4A, 0x50),
        Muted = Color.FromRgb(0x6E, 0x6E, 0x76),
        LineNumber = Color.FromRgb(0xB4, 0xB4, 0xBA),
        Border = Color.FromRgb(0xE6, 0xE6, 0xEA),
        QuoteBar = Color.FromRgb(0xD4, 0xD4, 0xDA),
        Accent = Color.FromRgb(0x25, 0x63, 0xEB),
        Selection = Color.FromArgb(0x40, 0x25, 0x63, 0xEB),
        Warning = Color.FromRgb(0xB4, 0x6A, 0x00),
        WarningBg = Color.FromRgb(0xFF, 0xF7, 0xE0),
        Danger = Color.FromRgb(0xC5, 0x2A, 0x2A),
        DangerBg = Color.FromRgb(0xFD, 0xEE, 0xEE)
    };

    // 深色：中性炭灰（不偏蓝），正文略降亮度减少刺眼
    public static Palette Dark => new()
    {
        Background = Color.FromRgb(0x1C, 0x1C, 0x1E),
        Sidebar = Color.FromRgb(0x22, 0x22, 0x25),
        Toolbar = Color.FromRgb(0x1C, 0x1C, 0x1E),
        Subtle = Color.FromRgb(0x2A, 0x2A, 0x2E),
        Hover = Color.FromRgb(0x2E, 0x2E, 0x32),
        Active = Color.FromRgb(0x38, 0x38, 0x3D),
        Foreground = Color.FromRgb(0xE6, 0xE6, 0xE9),
        SecondaryForeground = Color.FromRgb(0xB0, 0xB0, 0xB6),
        Muted = Color.FromRgb(0x8E, 0x8E, 0x96),
        LineNumber = Color.FromRgb(0x5A, 0x5A, 0x62),
        Border = Color.FromRgb(0x33, 0x33, 0x38),
        QuoteBar = Color.FromRgb(0x48, 0x48, 0x50),
        Accent = Color.FromRgb(0x6B, 0x9E, 0xFF),
        Selection = Color.FromArgb(0x55, 0x4C, 0x7D, 0xD8),
        Warning = Color.FromRgb(0xE3, 0xB3, 0x41),
        WarningBg = Color.FromRgb(0x36, 0x2C, 0x14),
        Danger = Color.FromRgb(0xF2, 0x7A, 0x72),
        DangerBg = Color.FromRgb(0x3A, 0x1F, 0x1F)
    };
}
