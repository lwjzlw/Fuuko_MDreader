using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MarkdownReader;

public sealed record ListContinuation(bool EndList, int PrefixLength, string NextPrefix);

public sealed record TextMetrics(int Lines, int Chars, int Words, int ReadingMinutes);

public sealed record OutlineItem(int Level, string Title, string Slug, int LineNumber)
{
    public string LevelBadge => $"H{Level}";
    public string LineLabel => $"L{LineNumber}";
    public double IndentLeft => (Level - 1) * 12;
}

public sealed record SiblingFileItem(
    string Name,
    string FullPath,
    string DirectoryPath,
    long SizeBytes,
    string SizeLabel,
    DateTime LastWriteTime,
    bool IsCurrent);

public sealed record MarkdownFile(string Text, Encoding Encoding, string? NewLine, string Fingerprint)
{
    private const int MaxBytes = 16 * 1024 * 1024;
    public static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    public static readonly Encoding Utf8Bom = new UTF8Encoding(true, true);

    private static readonly string[] MarkdownExtensions = { ".md", ".markdown", ".mdown", ".mkd", ".mmd" };

    private static readonly Encoding[] BomEncodings =
    {
        new UTF32Encoding(false, true, true),
        new UTF32Encoding(true, true, true),
        new UTF8Encoding(true, true),
        new UnicodeEncoding(false, true, true),
        new UnicodeEncoding(true, true, true)
    };

    public static bool IsMarkdownExtension(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var ext = Path.GetExtension(path);
        return MarkdownExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    public static MarkdownFile Read(string path)
    {
        using var stream = OpenForRead(path);
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var encoding = BomEncodings.FirstOrDefault(candidate => bytes.AsSpan().StartsWith(candidate.Preamble)) ?? Utf8;
        string text;
        try
        {
            text = encoding.GetString(bytes.AsSpan(encoding.Preamble.Length));
        }
        catch (DecoderFallbackException ex)
        {
            // 无 BOM 且不是合法 UTF-8 时，尝试严格 GB18030（兼容 GBK/GB2312），且必须能逐字节还原。
            if (encoding.Preamble.Length == 0 && TryDecodeGb18030(bytes, out var gbText))
            {
                encoding = Gb18030;
                text = gbText;
            }
            else
            {
                throw new InvalidDataException("无法解码文件。支持 UTF-8、GBK/GB18030，以及带 BOM 的 UTF-16 和 UTF-32。", ex);
            }
        }
        if (text.Contains('\0'))
            throw new InvalidDataException("文件包含空字符，请检查它是否是文本文件并带有正确的编码标记。");

        return new MarkdownFile(text, encoding, DetectNewLine(text), Hash(bytes));
    }

    private static Encoding? _gb18030;

    /// <summary>严格模式 GB18030：非法字节抛异常，避免把乱码静默写回磁盘。</summary>
    public static Encoding Gb18030
    {
        get
        {
            if (_gb18030 != null) return _gb18030;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return _gb18030 = Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
    }

    public static bool IsUtf8(Encoding encoding) => encoding is UTF8Encoding;

    private static bool TryDecodeGb18030(byte[] bytes, out string text)
    {
        text = "";
        try
        {
            var decoded = Gb18030.GetString(bytes);
            if (!Gb18030.GetBytes(decoded).AsSpan().SequenceEqual(bytes)) return false;
            text = decoded;
            return true;
        }
        catch (DecoderFallbackException) { return false; }
        catch (EncoderFallbackException) { return false; }
    }

    private static FileStream OpenForRead(string path)
    {
        var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            if (stream.Length > MaxBytes)
                throw new InvalidDataException("文件超过 16 MB。");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static string? ReadFingerprint(string path)
    {
        try
        {
            using var stream = OpenForRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    // 对混合换行文档保留编辑器中的原始分隔符。单一风格的文档统一新增和粘贴的换行。
    public static string? DetectNewLine(ReadOnlySpan<char> text)
    {
        string? first = null;
        while (true)
        {
            var index = text.IndexOfAny('\r', '\n');
            if (index < 0) return first ?? "\n";

            var style = text[index] == '\n' ? "\n"
                : index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
            if (first != null && first != style) return null;
            first = style;
            text = text[(index + style.Length)..];
        }
    }

    public static byte[] Encode(string text, Encoding encoding, string? newLine)
    {
        if (newLine != null)
        {
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            if (newLine != "\n") text = text.Replace("\n", newLine);
        }
        if (text.Contains('\0'))
            throw new InvalidDataException("内容包含空字符，无法保存为 Markdown 文本。");

        var size = (long)encoding.GetByteCount(text) + encoding.Preamble.Length;
        if (size > MaxBytes)
            throw new InvalidDataException("保存后的文件将超过 16 MB。");

        var bytes = new byte[size];
        encoding.Preamble.CopyTo(bytes);
        encoding.GetBytes(text.AsSpan(), bytes.AsSpan(encoding.Preamble.Length));
        return bytes;
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static void Write(string path, byte[] bytes, string? expectedFingerprint)
    {
        // 以最终文件为目标，避免覆盖用户通过符号链接打开的链接本身。
        try { path = File.ResolveLinkTarget(path, returnFinalTarget: true)?.FullName ?? path; }
        catch (FileNotFoundException) when (expectedFingerprint == null) { }
        var temp = Path.Combine(Path.GetDirectoryName(path)!, $".mdreader-{Guid.NewGuid():N}.tmp");
        var tempCreated = false;
        try
        {
            using (var stream = File.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                tempCreated = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            // 串行化同一文件的多个阅读器窗口。写入临时文件后才复核磁盘版本。
            var key = Hash(System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant()));
            using var gate = new Mutex(false, $@"Local\MarkdownReader.Save.{key}");
            var acquired = false;
            try
            {
                try { acquired = gate.WaitOne(TimeSpan.FromSeconds(2)); }
                catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                    throw new IOException("另一个窗口正在保存此文件，请稍后重试。");
                if (ReadFingerprint(path) != expectedFingerprint)
                    throw new IOException("磁盘文件在保存过程中发生变化，请重新保存或另存为。");

                if (expectedFingerprint == null)
                    File.Move(temp, path);
                else
                    File.Replace(temp, path, null);
                tempCreated = false;
            }
            finally
            {
                if (acquired) gate.ReleaseMutex();
            }
        }
        finally
        {
            if (tempCreated)
            {
                try { File.Delete(temp); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    public static string DescribeEncoding(Encoding encoding)
    {
        var preamble = encoding.GetPreamble();
        return encoding switch
        {
            UTF8Encoding => preamble.Length > 0 ? "UTF-8 BOM" : "UTF-8",
            UnicodeEncoding => preamble.Length >= 2 && preamble[0] == 0xFE ? "UTF-16 BE" : "UTF-16 LE",
            UTF32Encoding => preamble.Length >= 4 && preamble[0] == 0x00 ? "UTF-32 BE" : "UTF-32 LE",
            _ when encoding.CodePage == 54936 => "GB18030",
            _ => encoding.WebName.ToUpperInvariant()
        };
    }

    public static string DescribeNewLine(string? newLine) => newLine switch
    {
        "\n" => "LF",
        "\r\n" => "CRLF",
        "\r" => "CR",
        null => "混合换行",
        _ => "LF"
    };

    public static string FormatFileSize(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.0} KB";
        return $"{bytes / (1024.0 * 1024.0):0.00} MB";
    }

    public static TextMetrics AnalyzeText(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty) return new TextMetrics(1, 0, 0, 0);

        var lines = 1;
        var chars = 0;
        var words = 0;
        var inWord = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                lines++;
                if (i + 1 < text.Length && text[i + 1] == '\n') i++;
                inWord = false;
                continue;
            }
            if (c == '\n')
            {
                lines++;
                inWord = false;
                continue;
            }

            chars++;
            if (IsCjk(c))
            {
                words++;
                inWord = false;
            }
            else if (char.IsLetterOrDigit(c))
            {
                if (!inWord)
                {
                    words++;
                    inWord = true;
                }
            }
            else if (char.IsWhiteSpace(c) || char.IsPunctuation(c))
            {
                inWord = false;
            }
        }

        var minutes = words == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(words / 420.0));
        return new TextMetrics(lines, chars, words, minutes);
    }

    private static bool IsCjk(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF') ||
        (c >= '\u3400' && c <= '\u4DBF') ||
        (c >= '\u3040' && c <= '\u30FF') ||
        (c >= '\uAC00' && c <= '\uD7AF');

    public static string BuildSlug(string title)
    {
        var lower = title.Trim().ToLowerInvariant();
        return new string(lower.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ')
            .Select(c => c == ' ' ? '-' : c).ToArray());
    }

    public static IReadOnlyList<OutlineItem> ExtractOutline(string text)
    {
        var result = new List<OutlineItem>();
        if (string.IsNullOrEmpty(text)) return result;

        var usedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var inFence = false;
        char fenceChar = '\0';
        var fenceLen = 0;
        var lineNum = 0;

        using var reader = new StringReader(text);
        string? rawLine;
        while ((rawLine = reader.ReadLine()) != null)
        {
            lineNum++;
            var line = rawLine.TrimStart();
            if (line.Length >= 3 && (line[0] == '`' || line[0] == '~'))
            {
                var fc = line[0];
                var count = 0;
                while (count < line.Length && line[count] == fc) count++;
                if (count >= 3)
                {
                    if (!inFence)
                    {
                        inFence = true;
                        fenceChar = fc;
                        fenceLen = count;
                        continue;
                    }
                    if (fc == fenceChar && count >= fenceLen && line[count..].Trim().Length == 0)
                    {
                        inFence = false;
                        continue;
                    }
                }
            }

            if (inFence || line.Length == 0 || line[0] != '#') continue;

            var level = 0;
            while (level < line.Length && level < 6 && line[level] == '#') level++;
            if (level == 0 || level > 4) continue;
            if (level < line.Length && line[level] != ' ' && line[level] != '\t') continue;

            var title = line[level..].Trim();
            // 移除尾部可选的闭合 #
            title = title.TrimEnd('#').Trim();
            if (title.Length == 0) continue;

            // 去除常见内联 Markdown 标记以展示干净标题
            var cleanTitle = StripInlineMarkdown(title);
            if (cleanTitle.Length == 0) cleanTitle = title;

            var baseSlug = BuildSlug(cleanTitle);
            if (baseSlug.Length == 0) baseSlug = $"section-{lineNum}";
            var anchor = baseSlug;
            for (var suffix = 1; !usedSlugs.Add(anchor); suffix++)
                anchor = $"{baseSlug}-{suffix}";

            result.Add(new OutlineItem(level, cleanTitle, anchor, lineNum));
        }

        return result;
    }

    private static string StripInlineMarkdown(string text)
    {
        // 处理 [text](url) -> text 以及行内 ` ** *
        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '[' && text.IndexOf("](", i, StringComparison.Ordinal) is int mid and > 0)
            {
                var end = text.IndexOf(')', mid + 2);
                if (end > mid)
                {
                    sb.Append(text.AsSpan(i + 1, mid - i - 1));
                    i = end;
                    continue;
                }
            }
            if (c is '*' or '_' or '`') continue;
            sb.Append(c);
        }
        return sb.ToString().Trim();
    }

    private static readonly Regex ListItemPattern = new(
        @"^(?<indent>[ \t]*)(?<marker>(?:[-*+]|\d{1,9}[.)])[ \t]+(?:\[[ xX]\][ \t]+)?|>[ \t]?)",
        RegexOptions.Compiled);
    private static readonly Regex ThematicBreakPattern = new(@"^[ \t]*([-*_])([ \t]*\1){2,}[ \t]*$", RegexOptions.Compiled);
    private static readonly Regex OrderedMarkerPattern = new(@"^(\d+)([.)])", RegexOptions.Compiled);

    /// <summary>
    /// 计算列表/引用行按回车后的续写前缀。返回 null 表示不是列表行；
    /// EndList 为 true 表示当前是空列表项，应清除标记结束列表。
    /// </summary>
    public static ListContinuation? GetListContinuation(string line)
    {
        if (ThematicBreakPattern.IsMatch(line)) return null;
        var match = ListItemPattern.Match(line);
        if (!match.Success) return null;

        var indent = match.Groups["indent"].Value;
        var marker = match.Groups["marker"].Value;
        if (line[match.Length..].Trim().Length == 0)
            return new ListContinuation(true, match.Length, "");

        var next = marker;
        var ordered = OrderedMarkerPattern.Match(marker);
        if (ordered.Success && long.TryParse(ordered.Groups[1].Value, out var number))
            next = (number + 1).ToString() + ordered.Groups[2].Value + marker[ordered.Length..];
        next = next.Replace("[x]", "[ ]").Replace("[X]", "[ ]");
        return new ListContinuation(false, match.Length, indent + next);
    }

    /// <summary>列表行缩进一级所用的空白（有序列表按标记宽度对齐，其余两个空格）。</summary>
    public static string? GetListIndentUnit(string line)
    {
        if (ThematicBreakPattern.IsMatch(line)) return null;
        var match = ListItemPattern.Match(line);
        if (!match.Success || match.Groups["marker"].Value.StartsWith('>')) return null;
        var ordered = OrderedMarkerPattern.Match(match.Groups["marker"].Value);
        return ordered.Success ? new string(' ', ordered.Length + 1) : "  ";
    }

    public static IReadOnlyList<SiblingFileItem> ListSiblingMarkdownFiles(string? currentFilePath, int maxCount = 80)
    {
        var items = new List<SiblingFileItem>();
        if (string.IsNullOrWhiteSpace(currentFilePath)) return items;

        try
        {
            var fullCurrent = Path.GetFullPath(currentFilePath);
            var dir = Path.GetDirectoryName(fullCurrent);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return items;

            var dirInfo = new DirectoryInfo(dir);
            foreach (var file in dirInfo.EnumerateFiles()
                .Where(f => IsMarkdownExtension(f.Name))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Take(maxCount))
            {
                var isCurrent = string.Equals(file.FullName, fullCurrent, StringComparison.OrdinalIgnoreCase);
                items.Add(new SiblingFileItem(
                    file.Name,
                    file.FullName,
                    dir,
                    file.Length,
                    FormatFileSize(file.Length),
                    file.LastWriteTime,
                    isCurrent));
            }
        }
        catch
        {
            // 目录无权限或不可读时返回空列表
        }

        return items;
    }
}
