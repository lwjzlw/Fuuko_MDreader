using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MarkdownReader;

/// <summary>
/// 将 WPF FlowDocument 直接排版并导出为原生矢量 PDF（内嵌子集化 TrueType 字体、ToUnicode 映射、矢量几何与图片）。
/// 不依赖外部打印队列或第三方 PDF 库。
/// </summary>
public static class PdfExporter
{
    // A4 纸张尺寸：210mm × 297mm（96 DPI 下为 793.7 × 1122.5 DIP；72 DPI PDF 点下为 595.28 × 841.89 pt）
    public const double PageWidthDip = 793.7007874;
    public const double PageHeightDip = 1122.519685;
    private const double DipToPt = 72.0 / 96.0;
    private static readonly double PageWidthPt = PageWidthDip * DipToPt;
    private static readonly double PageHeightPt = PageHeightDip * DipToPt;

    public static void ExportFlowDocument(FlowDocument document, string pdfPath, string? documentTitle = null)
    {
        if (document == null) throw new ArgumentNullException(nameof(document));
        if (string.IsNullOrWhiteSpace(pdfPath)) throw new ArgumentException("PDF 路径不能为空。", nameof(pdfPath));

        document.PageWidth = PageWidthDip;
        document.PageHeight = PageHeightDip;
        document.ColumnWidth = double.PositiveInfinity;

        EnsureImagesLoaded(document);

        var paginator = ((IDocumentPaginatorSource)document).DocumentPaginator;
        paginator.PageSize = new Size(PageWidthDip, PageHeightDip);
        paginator.ComputePageCount();

        var builder = new PdfDocumentBuilder(documentTitle);
        var pageCount = Math.Max(1, paginator.PageCount);

        for (var i = 0; i < paginator.PageCount; i++)
        {
            using var page = paginator.GetPage(i);
            if (page.Visual is UIElement element)
            {
                element.Measure(new Size(PageWidthDip, PageHeightDip));
                element.Arrange(new Rect(0, 0, PageWidthDip, PageHeightDip));
                element.UpdateLayout();
            }
            builder.AddPage(page.Visual);
        }

        if (paginator.PageCount == 0)
            builder.AddEmptyPage();

        var pdfBytes = builder.Build();
        var fullPath = Path.GetFullPath(pdfPath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tempPath = Path.Combine(dir ?? ".", $".~{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempPath, pdfBytes);
            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { /* ignore cleanup failure */ }
            }
        }
    }

    private static void EnsureImagesLoaded(FlowDocument doc)
    {
        // MdXaml 默认通过异步后台任务 + Dispatcher 填充 Image.Source；导出前排空后台队列并同步测量图片
        for (var round = 0; round < 20; round++)
        {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                static () => { },
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            if (!HasPendingImages(doc.Blocks)) break;
            Thread.Sleep(15);
        }
    }

    private static bool HasPendingImages(BlockCollection blocks)
    {
        foreach (var b in blocks)
        {
            switch (b)
            {
                case Paragraph p:
                    if (HasPendingImagesInInlines(p.Inlines)) return true;
                    break;
                case Section s:
                    if (HasPendingImages(s.Blocks)) return true;
                    break;
                case List l:
                    foreach (var item in l.ListItems)
                        if (HasPendingImages(item.Blocks)) return true;
                    break;
                case Table t:
                    foreach (var g in t.RowGroups)
                        foreach (var r in g.Rows)
                            foreach (var c in r.Cells)
                                if (HasPendingImages(c.Blocks)) return true;
                    break;
                case BlockUIContainer bui when bui.Child is Image img && img.Source == null:
                    return true;
            }
        }
        return false;
    }

    private static bool HasPendingImagesInInlines(InlineCollection inlines)
    {
        foreach (var inl in inlines)
        {
            if (inl is InlineUIContainer { Child: Image { Source: null } })
                return true;
            if (inl is Span span && HasPendingImagesInInlines(span.Inlines))
                return true;
        }
        return false;
    }

    private sealed class PdfFontEntry
    {
        public required int Index { get; init; }
        public required string ResourceName { get; init; }
        public required GlyphTypeface Typeface { get; init; }
        public HashSet<ushort> UsedGlyphs { get; } = new() { 0 };
        public Dictionary<ushort, int> GlyphToUnicode { get; } = new();
        private Dictionary<ushort, int>? _reverseCmap;

        public void RecordGlyph(ushort gid, char? ch)
        {
            UsedGlyphs.Add(gid);
            if (ch is char c && !char.IsSurrogate(c) && c != '\0')
            {
                GlyphToUnicode.TryAdd(gid, c);
                return;
            }
            if (GlyphToUnicode.ContainsKey(gid)) return;

            if (_reverseCmap == null)
            {
                _reverseCmap = new Dictionary<ushort, int>();
                foreach (var kv in Typeface.CharacterToGlyphMap)
                    _reverseCmap.TryAdd(kv.Value, kv.Key);
            }
            if (_reverseCmap.TryGetValue(gid, out var codepoint))
                GlyphToUnicode[gid] = codepoint;
        }
    }

    private sealed class PdfImageEntry
    {
        public required int Index { get; init; }
        public required string ResourceName { get; init; }
        public required int PixelWidth { get; init; }
        public required int PixelHeight { get; init; }
        public required byte[] CompressedRgb { get; init; }
    }

    private sealed class PdfDocumentBuilder
    {
        private readonly string? _title;
        private readonly List<byte[]> _pageStreams = new();
        private readonly Dictionary<string, PdfFontEntry> _fontsByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<PdfFontEntry> _fonts = new();
        private readonly List<PdfImageEntry> _images = new();

        public PdfDocumentBuilder(string? title)
        {
            _title = title;
        }

        public void AddEmptyPage()
        {
            _pageStreams.Add(Encoding.ASCII.GetBytes("1 1 1 rg 0 0 595.28 841.89 re f\n"));
        }

        public void AddPage(Visual? rootVisual)
        {
            var sb = new StringBuilder(16384);
            // 默认白底纸面
            sb.Append("1 1 1 rg 0 0 ").Append(Fmt(PageWidthPt)).Append(' ').Append(Fmt(PageHeightPt)).Append(" re f\n");

            if (rootVisual != null)
                WalkVisual(rootVisual, rootVisual, Matrix.Identity, sb);

            _pageStreams.Add(Encoding.ASCII.GetBytes(sb.ToString()));
        }

        private void WalkVisual(Visual current, Visual root, Matrix inheritedMatrix, StringBuilder sb)
        {
            var localToRoot = inheritedMatrix;
            try
            {
                if (!ReferenceEquals(current, root) && current.TransformToAncestor(root) is Transform t)
                    localToRoot = t.Value;
            }
            catch
            {
                var offset = VisualTreeHelper.GetOffset(current);
                var transform = VisualTreeHelper.GetTransform(current);
                var m = transform?.Value ?? Matrix.Identity;
                m.Translate(offset.X, offset.Y);
                localToRoot = Matrix.Multiply(m, inheritedMatrix);
            }

            if (current is UIElement { Visibility: not Visibility.Visible })
                return;

            var clip = VisualTreeHelper.GetClip(current);
            var pushedClip = false;
            if (clip != null && !clip.Bounds.IsEmpty)
            {
                sb.Append("q\n");
                EmitClipRect(clip.Bounds, localToRoot, sb);
                pushedClip = true;
            }

            var drawing = VisualTreeHelper.GetDrawing(current);
            if (drawing != null)
            {
                WalkDrawing(drawing, localToRoot, sb);
            }
            else if (current is Separator sep && sep.ActualWidth > 0)
            {
                var color = (sep.Background as SolidColorBrush)?.Color ?? Color.FromRgb(0xE6, 0xE6, 0xEA);
                var rect = new Rect(0, Math.Max(0, (sep.ActualHeight - 1) / 2), sep.ActualWidth, Math.Max(1, sep.ActualHeight));
                EmitFilledRect(rect, color, localToRoot, sb);
            }
            else if (current is Image img && img.Source is BitmapSource bmp && img.ActualWidth > 0 && img.ActualHeight > 0)
            {
                EmitImage(bmp, new Rect(0, 0, img.ActualWidth, img.ActualHeight), localToRoot, sb);
            }

            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(current, i) is Visual child)
                    WalkVisual(child, root, localToRoot, sb);
            }

            if (pushedClip)
                sb.Append("Q\n");
        }

        private void WalkDrawing(Drawing drawing, Matrix matrix, StringBuilder sb)
        {
            switch (drawing)
            {
                case DrawingGroup group:
                    if (group.Opacity <= 0.001) return;
                    var nextMatrix = matrix;
                    if (group.Transform != null && !group.Transform.Value.IsIdentity)
                        nextMatrix = Matrix.Multiply(group.Transform.Value, matrix);

                    var hasClip = group.ClipGeometry != null && !group.ClipGeometry.Bounds.IsEmpty;
                    if (hasClip)
                    {
                        sb.Append("q\n");
                        EmitClipRect(group.ClipGeometry!.Bounds, nextMatrix, sb);
                    }
                    foreach (var child in group.Children)
                        WalkDrawing(child, nextMatrix, sb);
                    if (hasClip)
                        sb.Append("Q\n");
                    break;

                case GeometryDrawing geomDrawing:
                    EmitGeometryDrawing(geomDrawing, matrix, sb);
                    break;

                case GlyphRunDrawing glyphDrawing:
                    EmitGlyphRunDrawing(glyphDrawing, matrix, sb);
                    break;

                case ImageDrawing imageDrawing:
                    if (imageDrawing.ImageSource is BitmapSource bmp && !imageDrawing.Rect.IsEmpty)
                        EmitImage(bmp, imageDrawing.Rect, matrix, sb);
                    break;
            }
        }

        private void EmitClipRect(Rect bounds, Matrix matrix, StringBuilder sb)
        {
            var tl = ToPdfPoint(bounds.TopLeft, matrix);
            var br = ToPdfPoint(bounds.BottomRight, matrix);
            var x = Math.Min(tl.X, br.X);
            var y = Math.Min(tl.Y, br.Y);
            var w = Math.Abs(br.X - tl.X);
            var h = Math.Abs(tl.Y - br.Y);
            if (w <= 0 || h <= 0) return;
            sb.Append(Fmt(x)).Append(' ').Append(Fmt(y)).Append(' ')
              .Append(Fmt(w)).Append(' ').Append(Fmt(h)).Append(" re W n\n");
        }

        private void EmitFilledRect(Rect rect, Color color, Matrix matrix, StringBuilder sb)
        {
            if (color.A == 0 || rect.IsEmpty) return;
            var tl = ToPdfPoint(rect.TopLeft, matrix);
            var br = ToPdfPoint(rect.BottomRight, matrix);
            var x = Math.Min(tl.X, br.X);
            var y = Math.Min(tl.Y, br.Y);
            var w = Math.Abs(br.X - tl.X);
            var h = Math.Abs(tl.Y - br.Y);
            if (w <= 0 || h <= 0) return;

            EmitFillColor(color, sb);
            sb.Append(Fmt(x)).Append(' ').Append(Fmt(y)).Append(' ')
              .Append(Fmt(w)).Append(' ').Append(Fmt(h)).Append(" re f\n");
        }

        private void EmitGeometryDrawing(GeometryDrawing gd, Matrix matrix, StringBuilder sb)
        {
            if (gd.Geometry == null) return;

            var fillBrush = gd.Brush as SolidColorBrush;
            var hasFill = fillBrush is { Color.A: > 0 };

            var pen = gd.Pen;
            var strokeBrush = pen?.Brush as SolidColorBrush;
            var hasStroke = pen != null && pen.Thickness > 0 && strokeBrush is { Color.A: > 0 };

            if (!hasFill && !hasStroke) return;

            if (hasFill) EmitFillColor(fillBrush!.Color, sb);
            if (hasStroke)
            {
                EmitStrokeColor(strokeBrush!.Color, sb);
                var scale = Math.Sqrt(Math.Abs(matrix.Determinant));
                if (!double.IsFinite(scale) || scale <= 0) scale = 1.0;
                var lineWidthPt = Math.Max(0.25, pen!.Thickness * scale * DipToPt);
                sb.Append(Fmt(lineWidthPt)).Append(" w\n");
            }

            if (gd.Geometry is RectangleGeometry rg && rg.RadiusX <= 0.1 && rg.RadiusY <= 0.1)
            {
                var r = rg.Rect;
                if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return;
                var m = rg.Transform is { Value.IsIdentity: false } gt
                    ? Matrix.Multiply(gt.Value, matrix)
                    : matrix;
                var tl = ToPdfPoint(r.TopLeft, m);
                var br = ToPdfPoint(r.BottomRight, m);
                var x = Math.Min(tl.X, br.X);
                var y = Math.Min(tl.Y, br.Y);
                var w = Math.Abs(br.X - tl.X);
                var h = Math.Abs(tl.Y - br.Y);
                sb.Append(Fmt(x)).Append(' ').Append(Fmt(y)).Append(' ')
                  .Append(Fmt(w)).Append(' ').Append(Fmt(h)).Append(" re ")
                  .Append(hasFill && hasStroke ? "B\n" : hasFill ? "f\n" : "S\n");
                return;
            }

            if (gd.Geometry is LineGeometry lg)
            {
                if (!hasStroke) return;
                var m = lg.Transform is { Value.IsIdentity: false } gt
                    ? Matrix.Multiply(gt.Value, matrix)
                    : matrix;
                var p1 = ToPdfPoint(lg.StartPoint, m);
                var p2 = ToPdfPoint(lg.EndPoint, m);
                sb.Append(Fmt(p1.X)).Append(' ').Append(Fmt(p1.Y)).Append(" m ")
                  .Append(Fmt(p2.X)).Append(' ').Append(Fmt(p2.Y)).Append(" l S\n");
                return;
            }

            var path = gd.Geometry.GetFlattenedPathGeometry(0.25, ToleranceType.Absolute);
            if (path.Figures.Count == 0) return;

            var anyPoints = false;
            foreach (var fig in path.Figures)
            {
                var start = ToPdfPoint(fig.StartPoint, matrix);
                sb.Append(Fmt(start.X)).Append(' ').Append(Fmt(start.Y)).Append(" m\n");
                foreach (var seg in fig.Segments)
                {
                    if (seg is LineSegment ls)
                    {
                        var pt = ToPdfPoint(ls.Point, matrix);
                        sb.Append(Fmt(pt.X)).Append(' ').Append(Fmt(pt.Y)).Append(" l\n");
                        anyPoints = true;
                    }
                    else if (seg is PolyLineSegment pls)
                    {
                        foreach (var rawPt in pls.Points)
                        {
                            var pt = ToPdfPoint(rawPt, matrix);
                            sb.Append(Fmt(pt.X)).Append(' ').Append(Fmt(pt.Y)).Append(" l\n");
                            anyPoints = true;
                        }
                    }
                }
                if (fig.IsClosed)
                    sb.Append("h\n");
            }

            if (!anyPoints) return;
            var evenOdd = path.FillRule == FillRule.EvenOdd;
            var op = (hasFill, hasStroke, evenOdd) switch
            {
                (true, true, true) => "B*\n",
                (true, true, false) => "B\n",
                (true, false, true) => "f*\n",
                (true, false, false) => "f\n",
                _ => "S\n"
            };
            sb.Append(op);
        }

        private void EmitGlyphRunDrawing(GlyphRunDrawing grd, Matrix matrix, StringBuilder sb)
        {
            var gr = grd.GlyphRun;
            if (gr == null || gr.GlyphIndices == null || gr.GlyphIndices.Count == 0) return;

            var color = (grd.ForegroundBrush as SolidColorBrush)?.Color ?? Color.FromRgb(0x1D, 0x1D, 0x1F);
            if (color.A == 0) return;

            var font = GetOrAddFont(gr.GlyphTypeface);
            var scale = Math.Sqrt(Math.Abs(matrix.Determinant));
            if (!double.IsFinite(scale) || scale <= 0) scale = 1.0;
            var fontSizePt = Math.Max(1.0, gr.FontRenderingEmSize * scale * DipToPt);

            EmitFillColor(color, sb);
            sb.Append("BT\n/").Append(font.ResourceName).Append(' ').Append(Fmt(fontSizePt)).Append(" Tf\n");

            var origin = gr.BaselineOrigin;
            var chars = gr.Characters;
            var indices = gr.GlyphIndices;
            var advances = gr.AdvanceWidths;
            var offsets = gr.GlyphOffsets;
            var rtl = (gr.BidiLevel & 1) != 0;

            var runX = origin.X;
            var prevPdfX = 0.0;
            var prevPdfY = 0.0;

            for (var i = 0; i < indices.Count; i++)
            {
                var gid = indices[i];
                var ch = chars != null && i < chars.Count ? chars[i] : (char?)null;
                font.RecordGlyph(gid, ch);

                var adv = advances != null && i < advances.Count ? advances[i] : 0;
                if (rtl) runX -= adv;

                var gx = runX + (offsets != null && i < offsets.Count ? offsets[i].X : 0);
                var gy = origin.Y - (offsets != null && i < offsets.Count ? offsets[i].Y : 0);
                var pdfPt = ToPdfPoint(new Point(gx, gy), matrix);
                var x = Math.Round(pdfPt.X, 2);
                var y = Math.Round(pdfPt.Y, 2);

                var dx = x - prevPdfX;
                var dy = y - prevPdfY;
                sb.Append(Fmt(dx)).Append(' ').Append(Fmt(dy)).Append(" Td <")
                  .Append(gid.ToString("X4", CultureInfo.InvariantCulture)).Append("> Tj\n");

                prevPdfX = x;
                prevPdfY = y;

                if (!rtl) runX += adv;
            }

            sb.Append("ET\n");
        }

        private void EmitImage(BitmapSource bitmap, Rect rect, Matrix matrix, StringBuilder sb)
        {
            if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || rect.Width <= 0 || rect.Height <= 0)
                return;

            var imgEntry = AddImageResource(bitmap);
            var tl = ToPdfPoint(rect.TopLeft, matrix);
            var br = ToPdfPoint(rect.BottomRight, matrix);
            var x = Math.Min(tl.X, br.X);
            var y = Math.Min(tl.Y, br.Y);
            var w = Math.Abs(br.X - tl.X);
            var h = Math.Abs(tl.Y - br.Y);
            if (w <= 0.1 || h <= 0.1) return;

            sb.Append("q ").Append(Fmt(w)).Append(" 0 0 ").Append(Fmt(h)).Append(' ')
              .Append(Fmt(x)).Append(' ').Append(Fmt(y)).Append(" cm /")
              .Append(imgEntry.ResourceName).Append(" Do Q\n");
        }

        private PdfFontEntry GetOrAddFont(GlyphTypeface typeface)
        {
            var key = $"{typeface.FontUri}#{typeface.Style}#{typeface.Weight}#{typeface.Stretch}";
            if (_fontsByKey.TryGetValue(key, out var existing))
                return existing;

            var index = _fonts.Count + 1;
            var entry = new PdfFontEntry
            {
                Index = index,
                ResourceName = $"F{index}",
                Typeface = typeface
            };
            _fontsByKey[key] = entry;
            _fonts.Add(entry);
            return entry;
        }

        private PdfImageEntry AddImageResource(BitmapSource source)
        {
            var converted = source.Format == PixelFormats.Bgra32
                ? source
                : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

            var w = converted.PixelWidth;
            var h = converted.PixelHeight;
            var stride = w * 4;
            var bgra = new byte[h * stride];
            converted.CopyPixels(bgra, stride, 0);

            var rgb = new byte[w * h * 3];
            var dst = 0;
            for (var i = 0; i < bgra.Length; i += 4)
            {
                var b = bgra[i];
                var g = bgra[i + 1];
                var r = bgra[i + 2];
                var a = bgra[i + 3];
                if (a == 255)
                {
                    rgb[dst++] = r;
                    rgb[dst++] = g;
                    rgb[dst++] = b;
                }
                else
                {
                    // Alpha 与白色纸面混合
                    rgb[dst++] = (byte)((r * a + 255 * (255 - a)) / 255);
                    rgb[dst++] = (byte)((g * a + 255 * (255 - a)) / 255);
                    rgb[dst++] = (byte)((b * a + 255 * (255 - a)) / 255);
                }
            }

            var index = _images.Count + 1;
            var entry = new PdfImageEntry
            {
                Index = index,
                ResourceName = $"Im{index}",
                PixelWidth = w,
                PixelHeight = h,
                CompressedRgb = ZlibCompress(rgb)
            };
            _images.Add(entry);
            return entry;
        }

        private static void EmitFillColor(Color c, StringBuilder sb)
        {
            var (r, g, b) = BlendOnWhite(c);
            sb.Append(FmtColor(r)).Append(' ').Append(FmtColor(g)).Append(' ').Append(FmtColor(b)).Append(" rg\n");
        }

        private static void EmitStrokeColor(Color c, StringBuilder sb)
        {
            var (r, g, b) = BlendOnWhite(c);
            sb.Append(FmtColor(r)).Append(' ').Append(FmtColor(g)).Append(' ').Append(FmtColor(b)).Append(" RG\n");
        }

        private static (double R, double G, double B) BlendOnWhite(Color c)
        {
            var a = c.A / 255.0;
            var r = (c.R / 255.0) * a + (1.0 - a);
            var g = (c.G / 255.0) * a + (1.0 - a);
            var b = (c.B / 255.0) * a + (1.0 - a);
            return (r, g, b);
        }

        private static Point ToPdfPoint(Point dip, Matrix matrix)
        {
            var p = matrix.Transform(dip);
            return new Point(p.X * DipToPt, (PageHeightDip - p.Y) * DipToPt);
        }

        public byte[] Build()
        {
            var objects = new List<byte[]>();
            int AddObject(byte[] content)
            {
                objects.Add(content);
                return objects.Count;
            }
            int ReserveObject()
            {
                objects.Add(Array.Empty<byte>());
                return objects.Count;
            }
            void SetObject(int id, byte[] content) => objects[id - 1] = content;

            var catalogId = ReserveObject();
            var pagesId = ReserveObject();

            // 1. 构建字体对象（Type0 + CIDFontType2 + FontDescriptor + FontFile2 + ToUnicode）
            var fontResourcePairs = new List<string>();
            foreach (var font in _fonts)
            {
                var baseName = SanitizePdfName($"F{font.Index}Font");
                byte[] subsetBytes;
                try
                {
                    subsetBytes = font.Typeface.ComputeSubset(font.UsedGlyphs);
                }
                catch
                {
                    using var s = font.Typeface.GetFontStream();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    subsetBytes = ms.ToArray();
                }

                var compressedFont = ZlibCompress(subsetBytes);
                var fontFileId = AddObject(BuildStreamObject(
                    $"<< /Length {compressedFont.Length} /Length1 {subsetBytes.Length} /Filter /FlateDecode >>",
                    compressedFont));

                var ascent = (int)Math.Round(font.Typeface.Baseline * 1000);
                var descent = -(int)Math.Round((font.Typeface.Height - font.Typeface.Baseline) * 1000);
                var capHeight = (int)Math.Round(font.Typeface.CapsHeight * 1000);
                if (capHeight <= 0) capHeight = 700;

                var descriptorId = AddObject(AsciiBytes(
                    $"<< /Type /FontDescriptor /FontName /{baseName} /Flags 4 /FontBBox [-200 {descent} 1200 {ascent}] " +
                    $"/ItalicAngle 0 /Ascent {ascent} /Descent {descent} /CapHeight {capHeight} /StemV 80 /FontFile2 {fontFileId} 0 R >>"));

                // /W 数组：记录所有已用字形的精确千分位宽度
                var widthsSb = new StringBuilder("[");
                foreach (var gid in font.UsedGlyphs.OrderBy(g => g))
                {
                    var w = 1000;
                    if (font.Typeface.AdvanceWidths.TryGetValue(gid, out var adv))
                        w = (int)Math.Round(adv * 1000.0);
                    widthsSb.Append(gid).Append(" [").Append(w).Append("] ");
                }
                widthsSb.Append(']');

                var cidFontId = AddObject(AsciiBytes(
                    $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseName} " +
                    $"/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                    $"/FontDescriptor {descriptorId} 0 R /DW 1000 /W {widthsSb} /CIDToGIDMap /Identity >>"));

                var toUnicodeBytes = ZlibCompress(BuildToUnicodeCMap(font));
                var toUnicodeId = AddObject(BuildStreamObject(
                    $"<< /Length {toUnicodeBytes.Length} /Filter /FlateDecode >>",
                    toUnicodeBytes));

                var type0Id = AddObject(AsciiBytes(
                    $"<< /Type /Font /Subtype /Type0 /BaseFont /{baseName} /Encoding /Identity-H " +
                    $"/DescendantFonts [{cidFontId} 0 R] /ToUnicode {toUnicodeId} 0 R >>"));

                fontResourcePairs.Add($"/{font.ResourceName} {type0Id} 0 R");
            }

            // 2. 构建图片 XObject
            var imageResourcePairs = new List<string>();
            foreach (var img in _images)
            {
                var imgObjId = AddObject(BuildStreamObject(
                    $"<< /Type /XObject /Subtype /Image /Width {img.PixelWidth} /Height {img.PixelHeight} " +
                    $"/ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {img.CompressedRgb.Length} >>",
                    img.CompressedRgb));
                imageResourcePairs.Add($"/{img.ResourceName} {imgObjId} 0 R");
            }

            var resourcesDict = new StringBuilder("<< ");
            if (fontResourcePairs.Count > 0)
                resourcesDict.Append("/Font << ").Append(string.Join(' ', fontResourcePairs)).Append(" >> ");
            if (imageResourcePairs.Count > 0)
                resourcesDict.Append("/XObject << ").Append(string.Join(' ', imageResourcePairs)).Append(" >> ");
            resourcesDict.Append(">>");
            var resourcesId = AddObject(AsciiBytes(resourcesDict.ToString()));

            // 3. 构建每一页的 Content stream 与 Page 对象
            var pageIds = new List<int>();
            foreach (var rawStream in _pageStreams)
            {
                var compressedPage = ZlibCompress(rawStream);
                var contentId = AddObject(BuildStreamObject(
                    $"<< /Length {compressedPage.Length} /Filter /FlateDecode >>",
                    compressedPage));

                var pageId = AddObject(AsciiBytes(
                    $"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {Fmt(PageWidthPt)} {Fmt(PageHeightPt)}] " +
                    $"/Resources {resourcesId} 0 R /Contents {contentId} 0 R >>"));
                pageIds.Add(pageId);
            }

            var kidsRefs = string.Join(' ', pageIds.Select(id => $"{id} 0 R"));
            SetObject(pagesId, AsciiBytes($"<< /Type /Pages /Kids [{kidsRefs}] /Count {pageIds.Count} >>"));
            SetObject(catalogId, AsciiBytes($"<< /Type /Catalog /Pages {pagesId} 0 R >>"));

            var titleHex = EncodeUtf16BeHex(_title ?? "Markdown Document");
            var creatorHex = EncodeUtf16BeHex("Fuuko MDreader");
            var infoId = AddObject(AsciiBytes(
                $"<< /Title <{titleHex}> /Producer <{creatorHex}> /Creator <{creatorHex}> >>"));

            // 4. 序列化 PDF 文件与 xref 表
            using var output = new MemoryStream();
            void WriteAscii(string s) => output.Write(Encoding.ASCII.GetBytes(s));

            WriteAscii("%PDF-1.4\n");
            output.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });

            var offsets = new long[objects.Count + 1];
            for (var i = 0; i < objects.Count; i++)
            {
                offsets[i + 1] = output.Position;
                WriteAscii($"{i + 1} 0 obj\n");
                output.Write(objects[i]);
                WriteAscii("\nendobj\n");
            }

            var xrefPos = output.Position;
            WriteAscii($"xref\n0 {objects.Count + 1}\n");
            WriteAscii("0000000000 65535 f \n");
            for (var i = 1; i <= objects.Count; i++)
                WriteAscii($"{offsets[i]:D10} 00000 n \n");

            WriteAscii($"trailer\n<< /Size {objects.Count + 1} /Root {catalogId} 0 R /Info {infoId} 0 R >>\n");
            WriteAscii($"startxref\n{xrefPos}\n%%EOF\n");
            return output.ToArray();
        }

        private static byte[] BuildToUnicodeCMap(PdfFontEntry font)
        {
            var sb = new StringBuilder();
            sb.Append("/CIDInit /ProcSet findresource begin\n");
            sb.Append("12 dict begin\n");
            sb.Append("begincmap\n");
            sb.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
            sb.Append("/CMapName /Adobe-Identity-UCS def\n");
            sb.Append("/CMapType 2 def\n");
            sb.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");

            var pairs = font.GlyphToUnicode.OrderBy(kv => kv.Key).ToList();
            for (var i = 0; i < pairs.Count; i += 100)
            {
                var chunk = pairs.Skip(i).Take(100).ToList();
                sb.Append(chunk.Count).Append(" beginbfchar\n");
                foreach (var (gid, cp) in chunk)
                {
                    var utf16 = char.ConvertFromUtf32(cp);
                    var hex = new StringBuilder(utf16.Length * 4);
                    foreach (var ch in utf16)
                        hex.Append(((ushort)ch).ToString("X4", CultureInfo.InvariantCulture));
                    sb.Append('<').Append(gid.ToString("X4", CultureInfo.InvariantCulture)).Append("> <").Append(hex).Append(">\n");
                }
                sb.Append("endbfchar\n");
            }

            sb.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
            return Encoding.ASCII.GetBytes(sb.ToString());
        }

        private static byte[] BuildStreamObject(string dictHeader, byte[] payload)
        {
            using var ms = new MemoryStream(dictHeader.Length + payload.Length + 24);
            var h = Encoding.ASCII.GetBytes(dictHeader + "\nstream\n");
            var f = Encoding.ASCII.GetBytes("\nendstream");
            ms.Write(h);
            ms.Write(payload);
            ms.Write(f);
            return ms.ToArray();
        }

        private static byte[] ZlibCompress(byte[] raw)
        {
            using var ms = new MemoryStream();
            using (var zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }
            return ms.ToArray();
        }

        private static string EncodeUtf16BeHex(string text)
        {
            var bytes = Encoding.BigEndianUnicode.GetBytes(text);
            var sb = new StringBuilder("FEFF", 4 + bytes.Length * 2);
            foreach (var b in bytes)
                sb.Append(b.ToString("X2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static string SanitizePdfName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.Length > 0 ? sb.ToString() : "Font";
        }

        private static byte[] AsciiBytes(string s) => Encoding.ASCII.GetBytes(s);
        private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
        private static string FmtColor(double v) => Math.Clamp(v, 0.0, 1.0).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
