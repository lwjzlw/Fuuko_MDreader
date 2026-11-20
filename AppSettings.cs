using System.IO;
using System.Text.Json;

namespace MarkdownReader;

public class AppSettings
{
    private const int MaxRecentFiles = 15;

    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 1160;
    public double Height { get; set; } = 800;
    public bool Maximized { get; set; }
    public double FontSize { get; set; } = 16;
    public string Theme { get; set; } = "Light";
    public string ViewMode { get; set; } = "Preview";
    public bool SidebarOpen { get; set; } = true;
    public string SidebarTab { get; set; } = "Outline";
    public double SidebarWidth { get; set; } = 248;
    public bool AutoReload { get; set; } = true;
    public bool WordWrap { get; set; } = true;
    public string? LastFile { get; set; }
    public List<string> RecentFiles { get; set; } = new();
    public Dictionary<string, ReadingPosition> ReadingPositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MarkdownReader");

    public static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null)
                {
                    s.Normalize();
                    return s;
                }
            }
        }
        catch
        {
            // 设置损坏时用默认值，不影响启动
        }
        return new AppSettings();
    }

    public void Normalize()
    {
        FontSize = Math.Clamp(FontSize, 12, 30);
        SidebarWidth = Math.Clamp(SidebarWidth, 180, 420);
        if (Theme != "Dark") Theme = "Light";
        if (ViewMode is not ("Preview" or "Split" or "Edit")) ViewMode = "Preview";
        if (SidebarTab is not ("Outline" or "Files")) SidebarTab = "Outline";
        RecentFiles ??= new List<string>();

        var cleaned = new List<string>();
        foreach (var item in RecentFiles)
        {
            if (string.IsNullOrWhiteSpace(item)) continue;
            try
            {
                var full = Path.GetFullPath(item);
                if (!cleaned.Any(existing => string.Equals(existing, full, StringComparison.OrdinalIgnoreCase)))
                    cleaned.Add(full);
            }
            catch
            {
                // 忽略非法路径记录
            }
            if (cleaned.Count >= MaxRecentFiles) break;
        }
        RecentFiles = cleaned;

        if (!string.IsNullOrWhiteSpace(LastFile))
        {
            try
            {
                var fullLast = Path.GetFullPath(LastFile);
                LastFile = fullLast;
                if (!RecentFiles.Any(existing => string.Equals(existing, fullLast, StringComparison.OrdinalIgnoreCase)))
                {
                    RecentFiles.Insert(0, fullLast);
                    if (RecentFiles.Count > MaxRecentFiles)
                        RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
                }
            }
            catch
            {
                LastFile = null;
            }
        }

        ReadingPositions ??= new Dictionary<string, ReadingPosition>(StringComparer.OrdinalIgnoreCase);
        _readingPositionOrder = Math.Max(_readingPositionOrder,
            ReadingPositions.Values.Where(p => p != null).Select(p => p.Order).DefaultIfEmpty().Max());
        var cleanedPositions = new Dictionary<string, ReadingPosition>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in ReadingPositions)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null) continue;
            try
            {
                var full = Path.GetFullPath(pair.Key);
                var position = pair.Value;
                var order = position.Order > 0 ? position.Order : ++_readingPositionOrder;
                cleanedPositions[full] = new ReadingPosition(
                    double.IsFinite(position.ScrollOffset) ? Math.Max(0, position.ScrollOffset) : 0,
                    Math.Max(1, position.Line),
                    position.Anchor,
                    order);
            }
            catch
            {
                // 忽略非法路径对应的阅读位置
            }
        }
        ReadingPositions = cleanedPositions;
        TrimReadingPositions();
    }

    public void AddRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var full = Path.GetFullPath(path);
        LastFile = full;
        RecentFiles.RemoveAll(existing => string.Equals(existing, full, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, full);
        if (RecentFiles.Count > MaxRecentFiles)
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
    }

    public void RemoveRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        RecentFiles.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
        if (string.Equals(LastFile, path, StringComparison.OrdinalIgnoreCase))
            LastFile = RecentFiles.FirstOrDefault();
    }

    public int PruneMissingRecentFiles()
    {
        var removed = RecentFiles.RemoveAll(p => !File.Exists(p));
        if (LastFile != null && !File.Exists(LastFile))
            LastFile = RecentFiles.FirstOrDefault();
        return removed;
    }

    private const int MaxReadingPositions = 30;
    private long _readingPositionOrder;

    public void RememberReadingPosition(string path, double scrollOffset, int line, string? anchor)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var full = Path.GetFullPath(path);
        ReadingPositions.Remove(full);
        ReadingPositions.Add(full, new ReadingPosition(
            double.IsFinite(scrollOffset) ? Math.Max(0, scrollOffset) : 0,
            Math.Max(1, line),
            anchor,
            ++_readingPositionOrder));
        TrimReadingPositions();
    }

    public ReadingPosition? GetReadingPosition(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return ReadingPositions.TryGetValue(Path.GetFullPath(path), out var position) ? position : null;
    }

    private void TrimReadingPositions()
    {
        while (ReadingPositions.Count > MaxReadingPositions)
        {
            var oldest = ReadingPositions
                .OrderBy(pair => pair.Value.Order)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .First();
            ReadingPositions.Remove(oldest.Key);
        }
    }

    public void Save()
    {
        try
        {
            Normalize();
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 设置保存失败不阻塞退出
        }
    }
}

public sealed record ReadingPosition(double ScrollOffset, int Line, string? Anchor, long Order = 0);
